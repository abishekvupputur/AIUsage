namespace CopilotUsage.Models;

public enum UsageProvider { Claude, GitHubCopilot, Gemini, OpenAI }

public enum GeminiDisplayMode { Auto, Simplified, V2Only, V3Only, All }

public sealed class AppSettings
{
	public List<UsageProvider> SelectedProviders { get; set; } = [ UsageProvider.Claude ];

	public string SessionKey { get; set; } = string.Empty;
	public string GitHubToken { get; set; } = string.Empty;
	public string OpenAIToken { get; set; } = string.Empty;
	public string OpenAIRefreshToken { get; set; } = string.Empty;
	public string GeminiClientId { get; set; } = string.Empty;
	public string GeminiClientSecret { get; set; } = string.Empty;
	public string GeminiCredentialsPath { get; set; } = @"%USERPROFILE%\.gemini\oauth_creds.json";

	public int ClaudeRefreshIntervalMinutes  { get; set; } = 5;
	public int CopilotRefreshIntervalMinutes { get; set; } = 15;
	public int OpenAIRefreshIntervalMinutes  { get; set; } = 15;
	public int GeminiRefreshIntervalMinutes  { get; set; } = 15;

	public GeminiDisplayMode GeminiDisplayMode { get; set; } = GeminiDisplayMode.Auto;

	public bool StartWithWindows { get; set; } = false;
}
