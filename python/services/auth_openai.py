"""OpenAI Device Auth flow (Codex CLI protocol)."""

import httpx

_CLIENT_ID   = "app_EMoamEEZ73f0CkXaXp7hrann"
_USERCODE_URL = "https://auth.openai.com/api/accounts/deviceauth/usercode"
_POLL_URL     = "https://auth.openai.com/api/accounts/deviceauth/token"
_EXCHANGE_URL = "https://auth.openai.com/oauth/token"
_REFRESH_URL  = "https://auth.openai.com/oauth/token"


def request_device_code() -> dict:
    with httpx.Client() as client:
        resp = client.post(
            _USERCODE_URL,
            json={"client_id": _CLIENT_ID},
            headers={"Accept": "application/json"},
        )
        if not resp.is_success:
            raise ValueError(f"OpenAI device code request failed ({resp.status_code}): {resp.text[:400]}")
        data = resp.json()

    device_code = data.get("device_auth_id") or data.get("device_code")
    if not device_code:
        raise ValueError(f"Missing device_auth_id in response: {data}")

    user_code = data.get("user_code") or data.get("usercode")
    if not user_code:
        raise ValueError(f"Missing user_code in response: {data}")

    interval = data.get("interval", 5)
    if isinstance(interval, str):
        interval = int(interval)

    return {
        "device_code":      device_code,
        "user_code":        user_code,
        "verification_uri": data.get("verification_uri") or data.get("verification_url") or "https://auth.openai.com/codex/device",
        "expires_in":       data.get("expires_in", 900),
        "interval":         interval,
    }


def poll_for_token(device_code: str, user_code: str) -> dict:
    """Single poll attempt. Returns {status, access_token?, refresh_token?}."""
    with httpx.Client() as client:
        resp = client.post(
            _POLL_URL,
            json={"device_auth_id": device_code, "user_code": user_code},
            headers={"Accept": "application/json"},
        )

    # 403/404 = still pending
    if resp.status_code in (403, 404):
        return {"status": "pending"}

    if not resp.is_success:
        return {"status": "error", "error": f"HTTP {resp.status_code}: {resp.text[:200]}"}

    data = resp.json()

    auth_code    = data.get("authorization_code")
    code_verifier = data.get("code_verifier")
    if auth_code and code_verifier:
        try:
            tokens = _exchange_auth_code(auth_code, code_verifier)
            return {"status": "ok", **tokens}
        except Exception as e:
            return {"status": "error", "error": str(e)}

    access_token = data.get("access_token")
    if access_token:
        return {
            "status":        "ok",
            "access_token":  access_token,
            "refresh_token": data.get("refresh_token", ""),
        }

    return {"status": "error", "error": f"Unexpected response: {str(data)[:200]}"}


def _exchange_auth_code(auth_code: str, code_verifier: str) -> dict:
    with httpx.Client() as client:
        resp = client.post(
            _EXCHANGE_URL,
            data={
                "grant_type":    "authorization_code",
                "code":          auth_code,
                "redirect_uri":  "https://auth.openai.com/deviceauth/callback",
                "client_id":     _CLIENT_ID,
                "code_verifier": code_verifier,
            },
        )
        if not resp.is_success:
            raise ValueError(f"OpenAI code exchange failed ({resp.status_code}): {resp.text[:400]}")
        data = resp.json()

    access_token = data.get("access_token")
    if not access_token:
        raise ValueError(f"Missing access_token in exchange response: {data}")

    return {
        "access_token":  access_token,
        "refresh_token": data.get("refresh_token", ""),
    }


def refresh_token(refresh_tok: str) -> dict:
    with httpx.Client() as client:
        resp = client.post(
            _REFRESH_URL,
            json={
                "client_id":     _CLIENT_ID,
                "grant_type":    "refresh_token",
                "refresh_token": refresh_tok,
            },
            headers={"Accept": "application/json"},
        )
        if not resp.is_success:
            raise ValueError(f"OpenAI token refresh failed ({resp.status_code}): {resp.text[:400]}")
        data = resp.json()

    return {
        "access_token":  data.get("access_token", ""),
        "refresh_token": data.get("refresh_token", refresh_tok),
    }
