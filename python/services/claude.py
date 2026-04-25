"""Claude AI usage fetcher — uses curl_cffi to bypass Cloudflare TLS fingerprinting."""

from datetime import datetime, timezone
from typing import Optional
from curl_cffi import requests as cffi_requests

BASE_URL = "https://claude.ai"
_CANDIDATES = [
    "/api/organizations/{uuid}/rate_limit_status",
    "/api/organizations/{uuid}/usage",
    "/api/organizations/{uuid}/limits",
    "/api/organizations/{uuid}/rate_limits",
]


def fetch_claude_usage(session_key: str) -> dict:
    bare_key = _extract_bare_key(session_key)
    session = cffi_requests.Session(impersonate="chrome120")
    cookies = {"sessionKey": bare_key}

    org_uuid = _get_org_uuid(session, cookies)
    data = _get_rate_limit(session, cookies, org_uuid)
    data["last_updated"] = datetime.now(timezone.utc).isoformat()
    data["error"] = None
    return data


def _extract_bare_key(key: str) -> str:
    idx = key.find("sessionKey=")
    if idx < 0:
        return key.strip()
    start = idx + len("sessionKey=")
    end = key.find(";", start)
    return key[start:end].strip() if end >= 0 else key[start:].strip()


def _get_org_uuid(session, cookies) -> str:
    resp = session.get(f"{BASE_URL}/api/organizations", cookies=cookies)
    resp.raise_for_status()
    orgs = resp.json()
    if not isinstance(orgs, list) or not orgs:
        raise ValueError("No Claude organizations found in account")
    uuid = orgs[0].get("uuid")
    if not uuid:
        raise ValueError("Organization UUID missing from response")
    return uuid


def _get_rate_limit(session, cookies, org_uuid: str) -> dict:
    for path_tpl in _CANDIDATES:
        path = path_tpl.format(uuid=org_uuid)
        try:
            resp = session.get(f"{BASE_URL}{path}", cookies=cookies)
            if resp.status_code == 404:
                continue
            resp.raise_for_status()
            return _parse(resp.json())
        except Exception as e:
            if "404" in str(e):
                continue
            raise
    raise ValueError("Could not find Claude usage endpoint")


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
        "extra_enabled": False,
        "extra_used_eur": None,
        "extra_limit_eur": None,
        "extra_percent": 0.0,
    }

    session_found = False
    weekly_found = False

    rl = data.get("rate_limit_status", {})
    if isinstance(rl, dict):
        for key in ("current_session", "session"):
            if not session_found and key in rl:
                session_found = _extract_entry(rl[key], result, "session")
        for key in ("weekly", "week"):
            if not weekly_found and key in rl:
                weekly_found = _extract_entry(rl[key], result, "weekly")

    for key in ("current_session", "session", "five_hour"):
        if not session_found and key in data:
            session_found = _extract_entry(data[key], result, "session")
    for key in ("weekly", "week", "seven_day"):
        if not weekly_found and key in data:
            weekly_found = _extract_entry(data[key], result, "weekly")

    for search_root in [data, data.get("rate_limit_status"), data.get("limits")]:
        if not isinstance(search_root, list):
            continue
        for entry in search_root:
            etype = entry.get("type") or entry.get("name") or entry.get("id") or ""
            if not session_found and "session" in etype.lower():
                _extract_values(entry, result, "session")
                session_found = True
            if not weekly_found and "week" in etype.lower():
                _extract_values(entry, result, "weekly")
                weekly_found = True

    extra = data.get("extra_usage", {})
    if isinstance(extra, dict) and extra.get("is_enabled"):
        result["extra_enabled"] = True
        ml = extra.get("monthly_limit")
        uc = extra.get("used_credits")
        util = extra.get("utilization")
        if ml is not None:
            result["extra_limit_eur"] = ml / 100.0
        if uc is not None:
            result["extra_used_eur"] = uc / 100.0
        if util is not None:
            result["extra_percent"] = max(0.0, min(100.0, float(util)))

    return result


def _extract_entry(entry: dict, result: dict, prefix: str) -> bool:
    if not isinstance(entry, dict) or entry is None:
        return False
    _extract_values(entry, result, prefix)
    return True


def _extract_values(entry: dict, result: dict, prefix: str):
    pct = entry.get("percent_used") or entry.get("utilization")
    if pct is not None:
        result[f"{prefix}_percent"] = max(0.0, min(100.0, float(pct)))
    else:
        limit = entry.get("limit") or entry.get("budget") or entry.get("entitlement")
        remaining = entry.get("remaining")
        used = entry.get("used")
        if limit and int(limit) > 0:
            actual_used = int(used) if used else (int(limit) - int(remaining or 0))
            result[f"{prefix}_percent"] = max(0.0, min(100.0, actual_used * 100.0 / int(limit)))
            result[f"{prefix}_used"] = actual_used
            result[f"{prefix}_limit"] = int(limit)

    reset_str = entry.get("resets_at") or entry.get("reset_at") or entry.get("resetAt")
    if reset_str and result.get(f"{prefix}_resets_at") is None:
        result[f"{prefix}_resets_at"] = reset_str
