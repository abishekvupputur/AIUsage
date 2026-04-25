"""GitHub Device Code OAuth flow."""

import httpx

_CLIENT_ID = "Iv1.b507a08c87ecfe98"
_DEVICE_URL = "https://github.com/login/device/code"
_TOKEN_URL  = "https://github.com/login/oauth/access_token"


def request_device_code() -> dict:
    with httpx.Client() as client:
        resp = client.post(
            _DEVICE_URL,
            data={"client_id": _CLIENT_ID, "scope": "user"},
            headers={"Accept": "application/json"},
        )
        resp.raise_for_status()
        data = resp.json()

    return {
        "device_code":      data["device_code"],
        "user_code":        data["user_code"],
        "verification_uri": data["verification_uri"],
        "expires_in":       data.get("expires_in", 900),
        "interval":         data.get("interval", 5),
    }


def poll_for_token(device_code: str) -> dict:
    """Single poll attempt. Returns {status, token?}."""
    with httpx.Client() as client:
        resp = client.post(
            _TOKEN_URL,
            data={
                "client_id":   _CLIENT_ID,
                "device_code": device_code,
                "grant_type":  "urn:ietf:params:oauth:grant-type:device_code",
            },
            headers={"Accept": "application/json"},
        )
        data = resp.json()

    if "access_token" in data:
        return {"status": "ok", "token": data["access_token"]}

    error = data.get("error", "")
    if error in ("authorization_pending", "slow_down"):
        return {"status": "pending", "slow_down": error == "slow_down"}

    return {"status": "error", "error": error or f"HTTP {resp.status_code}"}
