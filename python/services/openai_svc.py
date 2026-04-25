"""OpenAI Codex usage fetcher."""

from datetime import datetime, timezone
import httpx


def fetch_openai_usage(access_token: str) -> dict:
    json_body = _fetch(access_token)
    data = _parse(json_body)
    data["last_updated"] = datetime.now(timezone.utc).isoformat()
    data["error"] = None
    return data


def _fetch(access_token: str) -> dict:
    with httpx.Client(headers={
        "Authorization": f"Bearer {access_token}",
        "User-Agent": "AIUsage/1.0",
        "Accept": "application/json",
    }) as client:
        resp = client.get("https://chatgpt.com/backend-api/wham/usage")
        if resp.status_code == 401:
            raise PermissionError("OpenAI token expired or invalid")
        resp.raise_for_status()
        return resp.json()


def _parse(data: dict) -> dict:
    result = {
        "session_percent": 0.0,
        "session_used": None,
        "session_limit": None,
        "session_resets_at": None,
        "weekly_percent": 0.0,
        "weekly_used": None,
        "weekly_limit": None,
        "weekly_resets_at": None,
    }

    rate_limit = data.get("rate_limit", {})
    if isinstance(rate_limit, dict):
        primary = rate_limit.get("primary_window")
        secondary = rate_limit.get("secondary_window")
        if isinstance(primary, dict):
            _extract_window(primary, result, "session")
        if isinstance(secondary, dict):
            _extract_window(secondary, result, "weekly")
        if result["session_used"] is not None or result["weekly_used"] is not None:
            return result

    for src_key, prefix in (
        ("local_messages", "session"),
        ("five_hour_window", "session"),
        ("weekly_messages", "weekly"),
        ("cloud_tasks", "session"),
    ):
        if src_key in data and (prefix != "session" or result["session_used"] is None):
            _extract_quota(data[src_key], result, prefix)

    return result


def _extract_window(el: dict, result: dict, prefix: str):
    pct = el.get("used_percent")
    used = el.get("used") or el.get("messages_used")
    limit = el.get("limit") or el.get("messages_limit") or el.get("cap")
    reset_at = el.get("reset_at")

    if pct is not None:
        result[f"{prefix}_percent"] = max(0.0, min(100.0, float(pct)))

    if limit and int(limit) > 0:
        result[f"{prefix}_limit"] = int(limit)
        result[f"{prefix}_used"] = int(used) if used is not None else round(result[f"{prefix}_percent"] * int(limit) / 100.0)
    elif used is not None:
        result[f"{prefix}_used"] = int(used)

    if reset_at is not None:
        try:
            result[f"{prefix}_resets_at"] = datetime.fromtimestamp(int(reset_at), tz=timezone.utc).isoformat()
        except (TypeError, ValueError, OSError):
            pass


def _extract_quota(el: dict, result: dict, prefix: str):
    used = el.get("used")
    limit = el.get("limit") or el.get("cap") or el.get("max")
    pct = el.get("percent_used") or el.get("utilization")
    reset = el.get("resets_at") or el.get("reset_at")

    if limit and int(limit) > 0:
        result[f"{prefix}_limit"] = int(limit)
        result[f"{prefix}_used"] = int(used or 0)
        result[f"{prefix}_percent"] = pct if pct is not None else max(0.0, min(100.0, result[f"{prefix}_used"] * 100.0 / int(limit)))
    elif pct is not None:
        result[f"{prefix}_percent"] = max(0.0, min(100.0, float(pct)))

    if reset:
        result[f"{prefix}_resets_at"] = reset
