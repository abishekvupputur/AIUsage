using System.Net.Http;
using System.Text.Json;

namespace CopilotUsage.Services;

internal sealed class GitHubAuthService
{
	// VS Code GitHub Copilot extension client ID (publicly known)
	private const string ClientId = "Iv1.b507a08c87ecfe98";

	private static readonly HttpClient s_Http = new();

	public record DeviceCodeInfo(
		string DeviceCode,
		string UserCode,
		string VerificationUri,
		int    ExpiresIn,
		int    Interval );

	public static async Task<DeviceCodeInfo> RequestDeviceCodeAsync()
	{
		using var req = new HttpRequestMessage( HttpMethod.Post, "https://github.com/login/device/code" );
		req.Headers.Add( "Accept", "application/json" );
		req.Content = new FormUrlEncodedContent( new Dictionary<string, string>
		{
			["client_id"] = ClientId,
			["scope"]     = "user",
		} );

		var response = await s_Http.SendAsync( req ).ConfigureAwait( false );
		var json     = await response.Content.ReadAsStringAsync().ConfigureAwait( false );

		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;

		return new DeviceCodeInfo(
			DeviceCode:      root.GetProperty( "device_code" ).GetString()!,
			UserCode:        root.GetProperty( "user_code" ).GetString()!,
			VerificationUri: root.GetProperty( "verification_uri" ).GetString()!,
			ExpiresIn:       root.GetProperty( "expires_in" ).GetInt32(),
			Interval:        root.GetProperty( "interval" ).GetInt32() );
	}

	public static async Task<string> PollForTokenAsync( string deviceCode, int intervalSeconds, CancellationToken ct )
	{
		var deadline = DateTime.UtcNow.AddMinutes( 15 );

		while ( DateTime.UtcNow < deadline && !ct.IsCancellationRequested )
		{
			await Task.Delay( intervalSeconds * 1000, ct ).ConfigureAwait( false );

			using var req = new HttpRequestMessage( HttpMethod.Post, "https://github.com/login/oauth/access_token" );
			req.Headers.Add( "Accept", "application/json" );
			req.Content = new FormUrlEncodedContent( new Dictionary<string, string>
			{
				["client_id"]   = ClientId,
				["device_code"] = deviceCode,
				["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
			} );

			var response = await s_Http.SendAsync( req, ct ).ConfigureAwait( false );
			var json     = await response.Content.ReadAsStringAsync( ct ).ConfigureAwait( false );

			using var doc = JsonDocument.Parse( json );
			var root = doc.RootElement;

			if ( root.TryGetProperty( "access_token", out var tokenEl ) )
				return tokenEl.GetString()!;

			if ( root.TryGetProperty( "error", out var errorEl ) )
			{
				var error = errorEl.GetString();
				if ( error == "authorization_pending" ) continue;
				if ( error == "slow_down" ) { intervalSeconds += 5; continue; }
				throw new InvalidOperationException( $"GitHub auth failed: {error}" );
			}
		}

		throw new InvalidOperationException( "GitHub auth timed out." );
	}
}
