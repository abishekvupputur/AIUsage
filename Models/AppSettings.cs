namespace CopilotUsage.Models;

public enum UsageProvider { Claude, GitHubCopilot, Gemini }

public sealed class AppSettings
{
	public UsageProvider Provider { get; set; } = UsageProvider.Claude;

	/// <summary>Session key from claude.ai cookies. Used when Provider = Claude.</summary>
	public string SessionKey { get; set; } = string.Empty;

	/// <summary>GitHub OAuth access token. Used when Provider = GitHubCopilot.</summary>
	public string GitHubToken { get; set; } = string.Empty;

	/// <summary>Google OAuth Client ID. Used when Provider = Gemini.</summary>
	public string GeminiClientId { get; set; } = string.Empty;

	/// <summary>Google OAuth Client Secret. Used when Provider = Gemini.</summary>
	public string GeminiClientSecret { get; set; } = string.Empty;

	/// <summary>Path to Gemini OAuth credentials JSON (contains refresh_token). Supports %USERPROFILE% etc.</summary>
	public string GeminiCredentialsPath { get; set; } = @"%USERPROFILE%\.gemini\oauth_creds.json";

	/// <summary>Refresh interval in minutes. Valid values: 1, 5, 15, 60.</summary>
	public int RefreshIntervalMinutes { get; set; } = 5;

	public bool StartWithWindows { get; set; } = false;
}
