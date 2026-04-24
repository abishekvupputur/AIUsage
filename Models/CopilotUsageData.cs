namespace CopilotUsage.Models;

/// <summary>
/// Parsed usage data returned by the Claude API for display purposes.
/// </summary>
public sealed class CopilotUsageData
{
	/// <summary>Current session usage as a percentage (0–100).</summary>
	public double SessionPercent { get; set; }

	/// <summary>Absolute messages/tokens used in the current session (null if not reported by API).</summary>
	public int? SessionUsed { get; set; }

	/// <summary>Absolute session cap (null if not reported by API).</summary>
	public int? SessionLimit { get; set; }

	/// <summary>When the current session rate limit resets.</summary>
	public DateTimeOffset? SessionResetsAt { get; set; }

	/// <summary>Weekly usage across all models as a percentage (0–100).</summary>
	public double WeeklyPercent { get; set; }

	/// <summary>Absolute messages/tokens used this week (null if not reported by API).</summary>
	public int? WeeklyUsed { get; set; }

	/// <summary>Absolute weekly cap (null if not reported by API).</summary>
	public int? WeeklyLimit { get; set; }

	/// <summary>When the weekly limit resets.</summary>
	public DateTimeOffset? WeeklyResetsAt { get; set; }

	/// <summary>Whether extra (pay-per-use) usage is enabled on the account.</summary>
	public bool ExtraUsageEnabled { get; set; }

	/// <summary>Monthly extra-usage cap in EUR (monthly_limit / 100).</summary>
	public double? ExtraUsageMonthlyLimitEur { get; set; }

	/// <summary>Extra credits spent this month in EUR (used_credits / 100).</summary>
	public double? ExtraUsageUsedEur { get; set; }

	/// <summary>Extra usage as a percentage (0–100).</summary>
	public double ExtraUsagePercent { get; set; }

	/// <summary>
	/// Set to <c>true</c> when usage data is not available or could not be parsed.
	/// The UI will show an error banner.
	/// </summary>
	public bool IsUnavailable { get; set; }

	/// <summary>Raw API JSON (truncated) shown when parsing produces no data — helps diagnose unknown response shapes.</summary>
	public string? RawJson { get; set; }
}
