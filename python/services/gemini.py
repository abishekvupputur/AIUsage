"""Gemini quota fetcher — OAuth token refresh + cloudcode-pa API."""

import json
import os
from datetime import datetime, timezone, timedelta
from pathlib import Path
import httpx

_TOKEN_URL = "https://oauth2.googleapis.com/token"
_LOAD_URL = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
_QUOTA_URL = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota"


def fetch_gemini_usage(client_id: str, client_secret: str, credentials_path: str) -> dict:
    access_token = _refresh_token(client_id, client_secret, credentials_path)
    project_id = _load_project_id(access_token)
    buckets = _fetch_buckets(access_token, project_id)
    return {
        "buckets": buckets,
        "last_updated": datetime.now(timezone.utc).isoformat(),
        "error": None,
    }


def _refresh_token(client_id: str, client_secret: str, credentials_path: str) -> str:
    expanded = os.path.expandvars(credentials_path)
    path = Path(expanded)
    if not path.exists():
        raise FileNotFoundError(f"Gemini credentials not found: {expanded}")

    creds = json.loads(path.read_text())
    refresh_token = creds.get("refresh_token")
    if not refresh_token:
        raise ValueError(f"No refresh_token in credentials file. Keys: {list(creds.keys())}")

    with httpx.Client() as client:
        resp = client.post(_TOKEN_URL, data={
            "client_id": client_id,
            "client_secret": client_secret,
            "refresh_token": refresh_token,
            "grant_type": "refresh_token",
        })
        if not resp.is_success:
            raise ValueError(f"Token refresh failed ({resp.status_code}): {resp.text[:500]}")
        token_data = resp.json()

    access_token = token_data.get("access_token")
    if not access_token:
        raise ValueError(f"No access_token in token response: {str(token_data)[:500]}")
    return access_token


def _load_project_id(access_token: str) -> str:
    with httpx.Client() as client:
        resp = client.post(
            _LOAD_URL,
            headers={"Authorization": f"Bearer {access_token}"},
            json={"metadata": {"platform": "PLATFORM_UNSPECIFIED", "ideVersion": "", "pluginVersion": ""}},
        )
        if not resp.is_success:
            raise ValueError(f"loadCodeAssist failed ({resp.status_code}): {resp.text[:500]}")
        data = resp.json()

    for key, val in data.items():
        if key.lower() == "cloudaicompanionproject":
            return val
    raise ValueError(f"No project ID in loadCodeAssist response: {str(data)[:800]}")


def _fetch_buckets(access_token: str, project_id: str) -> list[dict]:
    with httpx.Client() as client:
        resp = client.post(
            _QUOTA_URL,
            headers={"Authorization": f"Bearer {access_token}"},
            json={"project": project_id},
        )
        if not resp.is_success:
            raise ValueError(f"Gemini quota API error ({resp.status_code}): {resp.text[:500]}")
        data = resp.json()

    buckets_raw = data.get("buckets", [])
    buckets = []
    for b in buckets_raw:
        model_id = b.get("modelId", "")
        remaining_fraction = float(b.get("remainingFraction", 0.0))
        reset_str = b.get("resetTime", "")
        try:
            reset_time = datetime.fromisoformat(reset_str).isoformat() if reset_str else (datetime.now(timezone.utc) + timedelta(hours=1)).isoformat()
        except ValueError:
            reset_time = (datetime.now(timezone.utc) + timedelta(hours=1)).isoformat()

        buckets.append({
            "model_id": model_id,
            "remaining_fraction": remaining_fraction,
            "used_percent": max(0.0, min(100.0, (1.0 - remaining_fraction) * 100.0)),
            "reset_time": reset_time,
        })

    return buckets
