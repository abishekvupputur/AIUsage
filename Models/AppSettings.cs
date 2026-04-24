namespace CopilotUsage.Models;

public enum UsageProvider { Claude, GitHubCopilot }

public sealed class AppSettings
{
	public UsageProvider Provider { get; set; } = UsageProvider.Claude;

	/// <summary>Session key from claude.ai cookies. Used when Provider = Claude.</summary>
	public string SessionKey { get; set; } = string.Empty;

	/// <summary>GitHub OAuth access token. Used when Provider = GitHubCopilot.</summary>
	public string GitHubToken { get; set; } = string.Empty;

	/// <summary>Refresh interval in minutes. Valid values: 1, 5, 15, 60.</summary>
	public int RefreshIntervalMinutes { get; set; } = 5;

	public bool StartWithWindows { get; set; } = false;
}
