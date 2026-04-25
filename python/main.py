"""AI Usage Dashboard — Flask web server + MQTT publisher + polling scheduler."""

import json
import os
import time
import threading
from datetime import datetime, timezone
from pathlib import Path

import yaml
from apscheduler.schedulers.background import BackgroundScheduler
from flask import Flask, jsonify, render_template, request

try:
    import paho.mqtt.client as mqtt_client
    MQTT_AVAILABLE = True
except ImportError:
    MQTT_AVAILABLE = False

from services.claude import fetch_claude_usage
from services.copilot import fetch_copilot_usage
from services.gemini import fetch_gemini_usage
from services.openai_svc import fetch_openai_usage
from services.auth_github import request_device_code as gh_request_code, poll_for_token as gh_poll
from services.auth_openai import request_device_code as oai_request_code, poll_for_token as oai_poll, refresh_token as oai_refresh

CONFIG_PATH = Path(__file__).parent / "config.yaml"
CS_SETTINGS_PATH = Path(os.environ.get("APPDATA", "")) / "AIUsage" / "settings.json"

_state: dict = {"data": {}, "lock": threading.Lock()}
_mqtt: object = None
_scheduler = None


DEFAULT_CONFIG = {
    "mqtt": {
        "host": "localhost", "port": 1883, "username": "", "password": "",
        "topic_prefix": "aiusage", "enabled": False, "publish_interval_seconds": 60,
    },
    "web": {"host": "127.0.0.1", "port": 8080},
    "providers": {
        "claude":  {"enabled": True,  "session_key": "",       "refresh_interval_minutes": 5},
        "copilot": {"enabled": False, "github_token": "",      "refresh_interval_minutes": 15},
        "openai":  {"enabled": False, "access_token": "",      "refresh_interval_minutes": 15},
        "gemini":  {"enabled": False, "client_id": "", "client_secret": "",
                    "credentials_path": r"%USERPROFILE%\.gemini\oauth_creds.json",
                    "refresh_interval_minutes": 15, "display_mode": "auto"},
    },
}

# ── Config ─────────────────────────────────────────────────────────────────────

def load_config() -> dict:
    if not CONFIG_PATH.exists():
        save_config(DEFAULT_CONFIG)
        print(f"[config] Created default config at {CONFIG_PATH}")
        return DEFAULT_CONFIG.copy()
    with open(CONFIG_PATH, encoding="utf-8") as f:
        return yaml.safe_load(f) or {}


def save_config(config: dict):
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        yaml.dump(config, f, default_flow_style=False, allow_unicode=True)


# ── MQTT ───────────────────────────────────────────────────────────────────────

_mqtt_status = {"state": "disabled", "error": None}

def setup_mqtt(config: dict):
    global _mqtt, _mqtt_status
    if not MQTT_AVAILABLE:
        _mqtt_status = {"state": "unavailable", "error": "paho-mqtt not installed"}
        print("[MQTT] paho-mqtt not installed")
        return
    cfg = config.get("mqtt", {})
    if not cfg.get("enabled"):
        _mqtt_status = {"state": "disabled", "error": None}
        print("[MQTT] Disabled in config (set mqtt.enabled: true to activate)")
        return
    host, port = cfg.get("host", "localhost"), cfg.get("port", 1883)
    try:
        client = mqtt_client.Client()
        if cfg.get("username"):
            client.username_pw_set(cfg["username"], cfg.get("password", ""))
        client.connect(host, port, keepalive=60)
        client.loop_start()
        _mqtt = client
        _mqtt_status = {"state": "connected", "host": host, "port": port, "error": None}
        print(f"[MQTT] Connected to {host}:{port}")
    except Exception as e:
        _mqtt_status = {"state": "error", "host": host, "port": port, "error": str(e)}
        print(f"[MQTT] Connection failed ({host}:{port}): {e}")


def _publish(prefix: str, data: dict):
    if _mqtt is None:
        return
    try:
        _mqtt.publish(f"{prefix}/all", json.dumps(data, default=str))
        for provider, pdata in data.items():
            _mqtt.publish(f"{prefix}/{provider}", json.dumps(pdata, default=str))
    except Exception as e:
        print(f"[MQTT] Publish error: {e}")


def _mqtt_heartbeat_loop(prefix: str):
    count = 0
    while True:
        with _state["lock"]:
            data = _state["data"]
        if data:
            _publish(prefix, data)
            count += 1
            if count == 1:
                print(f"[MQTT] Heartbeat started — publishing to {prefix}/{{provider}} every 1s")
        time.sleep(1)


# ── Polling ────────────────────────────────────────────────────────────────────

def poll_all():
    config = load_config()
    providers = config.get("providers", {})
    result = {}

    for name, fetcher, key_field in [
        ("claude",  _poll_claude,  "session_key"),
        ("copilot", _poll_copilot, "github_token"),
        ("openai",  _poll_openai,  "access_token"),
        ("gemini",  _poll_gemini,  None),
    ]:
        cfg = providers.get(name, {})
        if not cfg.get("enabled"):
            continue
        try:
            result[name] = fetcher(cfg)
        except Exception as e:
            result[name] = {
                "error": str(e),
                "last_updated": datetime.now(timezone.utc).isoformat(),
            }
            print(f"[{name}] Error: {e}")

    with _state["lock"]:
        _state["data"] = result

    cfg_mqtt = config.get("mqtt", {})
    if cfg_mqtt.get("enabled") and _mqtt:
        _publish(cfg_mqtt.get("topic_prefix", "aiusage"), result)

    print(f"[poll] Updated {list(result.keys())} at {datetime.now().strftime('%H:%M:%S')}")


def _poll_claude(cfg: dict) -> dict:
    key = cfg.get("session_key", "")
    if not key:
        raise ValueError("Claude session_key not set in config.yaml")
    return fetch_claude_usage(key)


def _poll_copilot(cfg: dict) -> dict:
    token = cfg.get("github_token", "")
    if not token:
        raise ValueError("GitHub token not set in config.yaml")
    return fetch_copilot_usage(token)


def _poll_openai(cfg: dict) -> dict:
    token = cfg.get("access_token", "")
    if not token:
        raise ValueError("OpenAI access_token not set in config.yaml")
    return fetch_openai_usage(token)


def _poll_gemini(cfg: dict) -> dict:
    client_id = cfg.get("client_id", "")
    client_secret = cfg.get("client_secret", "")
    creds_path = cfg.get("credentials_path", "")
    if not client_id or not client_secret:
        raise ValueError("Gemini client_id / client_secret not set in config.yaml")
    return fetch_gemini_usage(client_id, client_secret, creds_path)


# ── Scheduler ─────────────────────────────────────────────────────────────────

def _schedule_jobs(scheduler: BackgroundScheduler, config: dict):
    scheduler.remove_all_jobs()
    providers = config.get("providers", {})
    min_interval = min(
        (p.get("refresh_interval_minutes", 15) for p in providers.values() if p.get("enabled")),
        default=15,
    )
    scheduler.add_job(poll_all, "interval", minutes=min_interval, id="poll_all")


# ── Flask ──────────────────────────────────────────────────────────────────────

app = Flask(__name__)


@app.route("/")
def index():
    return render_template("index.html")


@app.route("/api/data")
def api_data():
    with _state["lock"]:
        return jsonify(_state["data"])


@app.route("/api/config", methods=["GET"])
def api_get_config():
    return jsonify(load_config())


@app.route("/api/config", methods=["POST"])
def api_save_config():
    config = request.get_json(force=True)
    if not isinstance(config, dict):
        return jsonify({"ok": False, "error": "Invalid config payload"}), 400
    save_config(config)
    if _scheduler:
        _schedule_jobs(_scheduler, config)
    return jsonify({"ok": True})


@app.route("/api/mqtt/status")
def api_mqtt_status():
    return jsonify(_mqtt_status)


@app.route("/api/poll", methods=["POST"])
def api_manual_poll():
    threading.Thread(target=poll_all, daemon=True).start()
    return jsonify({"ok": True})


@app.route("/api/import-cs-settings")
def api_import_cs():
    """Read credentials from C# app settings.json and merge into config.yaml."""
    if not CS_SETTINGS_PATH.exists():
        return jsonify({"ok": False, "error": f"C# settings not found: {CS_SETTINGS_PATH}"})

    try:
        cs = json.loads(CS_SETTINGS_PATH.read_text(encoding="utf-8"))
    except Exception as e:
        return jsonify({"ok": False, "error": f"Could not read C# settings: {e}"})

    config = load_config()
    p = config.setdefault("providers", {})
    imported = []

    def _set(provider: str, key: str, value):
        if value:
            p.setdefault(provider, {})[key] = value
            if provider not in imported:
                imported.append(provider)

    _set("claude",  "session_key",              cs.get("SessionKey"))
    _set("copilot", "github_token",              cs.get("GitHubToken"))
    _set("openai",  "access_token",              cs.get("OpenAIToken"))
    _set("gemini",  "client_id",                 cs.get("GeminiClientId"))
    _set("gemini",  "client_secret",             cs.get("GeminiClientSecret"))
    _set("gemini",  "credentials_path",          cs.get("GeminiCredentialsPath"))

    for provider, cs_key, cfg_key in [
        ("claude",  "ClaudeRefreshIntervalMinutes",  "refresh_interval_minutes"),
        ("copilot", "CopilotRefreshIntervalMinutes", "refresh_interval_minutes"),
        ("openai",  "OpenAIRefreshIntervalMinutes",  "refresh_interval_minutes"),
        ("gemini",  "GeminiRefreshIntervalMinutes",  "refresh_interval_minutes"),
    ]:
        val = cs.get(cs_key)
        if val:
            p.setdefault(provider, {})[cfg_key] = val

    selected = cs.get("SelectedProviders", [])
    provider_map = {0: "claude", 1: "copilot", 3: "gemini", 4: "openai"}
    for idx, name in provider_map.items():
        if idx in selected or name.title() in selected or name in [s.lower() if isinstance(s, str) else "" for s in selected]:
            p.setdefault(name, {})["enabled"] = True

    save_config(config)
    return jsonify({"ok": True, "imported": imported})


# ── OAuth routes ───────────────────────────────────────────────────────────────

@app.route("/api/auth/github/start", methods=["POST"])
def api_github_start():
    try:
        return jsonify({"ok": True, **gh_request_code()})
    except Exception as e:
        return jsonify({"ok": False, "error": str(e)}), 500


@app.route("/api/auth/github/poll", methods=["POST"])
def api_github_poll():
    body = request.get_json(force=True)
    device_code = body.get("device_code", "")
    if not device_code:
        return jsonify({"status": "error", "error": "device_code required"}), 400
    try:
        result = gh_poll(device_code)
        if result.get("status") == "ok":
            _save_token("copilot", "github_token", result["token"])
        return jsonify(result)
    except Exception as e:
        return jsonify({"status": "error", "error": str(e)}), 500


@app.route("/api/auth/openai/start", methods=["POST"])
def api_openai_start():
    try:
        return jsonify({"ok": True, **oai_request_code()})
    except Exception as e:
        return jsonify({"ok": False, "error": str(e)}), 500


@app.route("/api/auth/openai/poll", methods=["POST"])
def api_openai_poll():
    body = request.get_json(force=True)
    device_code = body.get("device_code", "")
    user_code   = body.get("user_code", "")
    if not device_code or not user_code:
        return jsonify({"status": "error", "error": "device_code and user_code required"}), 400
    try:
        result = oai_poll(device_code, user_code)
        if result.get("status") == "ok":
            _save_token("openai", "access_token",  result.get("access_token", ""))
            _save_token("openai", "refresh_token", result.get("refresh_token", ""))
        return jsonify(result)
    except Exception as e:
        return jsonify({"status": "error", "error": str(e)}), 500


def _save_token(provider: str, key: str, value: str):
    config = load_config()
    config.setdefault("providers", {}).setdefault(provider, {})[key] = value
    save_config(config)


# ── Entry point ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    config = load_config()

    setup_mqtt(config)

    mqtt_cfg = config.get("mqtt", {})
    if mqtt_cfg.get("enabled") and _mqtt:
        prefix = mqtt_cfg.get("topic_prefix", "aiusage")
        threading.Thread(target=_mqtt_heartbeat_loop, args=(prefix,), daemon=True).start()

    _scheduler = BackgroundScheduler(daemon=True)
    _schedule_jobs(_scheduler, config)
    _scheduler.start()

    threading.Thread(target=poll_all, daemon=True).start()

    web = config.get("web", {})
    host = web.get("host", "127.0.0.1")
    port = web.get("port", 8080)

    print(f"[web] Dashboard at http://{host}:{port}")
    app.run(host=host, port=port, debug=False, use_reloader=False)
