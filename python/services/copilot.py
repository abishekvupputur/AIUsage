"""GitHub Copilot usage fetcher."""

from datetime import datetime, timezone
import httpx


def fetch_copilot_usage(token: str) -> dict:
    json_body = _fetch(token)
    data = _parse(json_body)
    data["last_updated"] = datetime.now(timezone.utc).isoformat()
    data["error"] = None
    return data


def _fetch(token: str) -> dict:
    with httpx.Client(headers={
        "Authorization": f"token {token}",
        "User-Agent": "AIUsage/1.0",
        "Accept": "application/json",
    }) as client:
        resp = client.get("https://api.github.com/copilot_internal/user")
        if resp.status_code == 401:
            raise PermissionError("GitHub token expired or invalid")
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

    snapshots = data.get("quota_snapshots", {})
    premium = snapshots.get("premium_interactions") if isinstance(snapshots, dict) else None
    if isinstance(premium, dict):
        _extract_copilot_quota(premium, result, "session")

    if result["session_used"] is None:
        flat_used = data.get("premium_interactions_quota_used")
        flat_limit = data.get("premium_interactions_quota")
        if flat_limit and int(flat_limit) > 0:
            result["session_used"] = int(flat_used or 0)
            result["session_limit"] = int(flat_limit)
            result["session_percent"] = max(0.0, min(100.0, result["session_used"] * 100.0 / result["session_limit"]))

    limited = data.get("limited_user_quotas", {})
    monthly = data.get("monthly_quotas", {})
    if result["session_used"] is None and isinstance(limited, dict) and isinstance(monthly, dict):
        chat_rem = limited.get("chat")
        chat_tot = monthly.get("chat")
        if chat_tot and int(chat_tot) > 0:
            result["session_used"] = max(int(chat_tot) - int(chat_rem or 0), 0)
            result["session_limit"] = int(chat_tot)
            result["session_percent"] = max(0.0, min(100.0, result["session_used"] * 100.0 / result["session_limit"]))

        comp_rem = limited.get("completions")
        comp_tot = monthly.get("completions")
        if comp_tot and int(comp_tot) > 0:
            result["weekly_used"] = max(int(comp_tot) - int(comp_rem or 0), 0)
            result["weekly_limit"] = int(comp_tot)
            result["weekly_percent"] = max(0.0, min(100.0, result["weekly_used"] * 100.0 / result["weekly_limit"]))

    reset_str = data.get("quota_reset_date") or data.get("limited_user_reset_date")
    if reset_str:
        result["session_resets_at"] = reset_str
        result["weekly_resets_at"] = reset_str

    return result


def _extract_copilot_quota(el: dict, result: dict, prefix: str):
    remaining = el.get("remaining") or _round_int(el.get("quota_remaining"))
    used = el.get("used") or el.get("quota_used")
    limit = el.get("limit") or el.get("quota") or el.get("entitlement")
    pct = el.get("percent_used")
    pct_rem = el.get("percent_remaining")

    if used is None and limit is not None and remaining is not None:
        used = max(int(limit) - int(remaining), 0)

    if pct is not None:
        result[f"{prefix}_percent"] = max(0.0, min(100.0, float(pct)))
    elif pct_rem is not None:
        result[f"{prefix}_percent"] = max(0.0, min(100.0, 100.0 - float(pct_rem)))
    elif limit and int(limit) > 0:
        result[f"{prefix}_used"] = int(used or 0)
        result[f"{prefix}_limit"] = int(limit)
        result[f"{prefix}_percent"] = max(0.0, min(100.0, int(used or 0) * 100.0 / int(limit)))
        return

    result[f"{prefix}_used"] = int(used) if used is not None else None
    result[f"{prefix}_limit"] = int(limit) if limit is not None else None


def _round_int(val) -> int | None:
    if val is None:
        return None
    return round(float(val))
