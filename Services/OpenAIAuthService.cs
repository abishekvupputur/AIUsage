using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CopilotUsage.Services;

internal sealed class OpenAIAuthService
{
	// Official OpenAI Codex CLI client ID
	private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

	private static readonly HttpClient s_Http = new();

	public record DeviceCodeInfo(
		string DeviceCode,
		string UserCode,
		string VerificationUri,
		int    ExpiresIn,
		int    Interval );

	public record TokenResponse(
		string AccessToken,
		string RefreshToken,
		int    ExpiresIn );

	public static async Task<DeviceCodeInfo> RequestDeviceCodeAsync()
	{
		using var req = new HttpRequestMessage( HttpMethod.Post, "https://auth.openai.com/api/accounts/deviceauth/usercode" );
		req.Headers.Add( "Accept", "application/json" );

		// Codex sends minimal body — just client_id
		var payload = new { client_id = ClientId };
		req.Content = new StringContent( JsonSerializer.Serialize( payload ), Encoding.UTF8, "application/json" );

		var response = await s_Http.SendAsync( req ).ConfigureAwait( false );
		var json     = await response.Content.ReadAsStringAsync().ConfigureAwait( false );

		if ( !response.IsSuccessStatusCode )
			throw new InvalidOperationException( $"OpenAI device code request failed: {response.StatusCode}\n{json}" );

		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;

		var deviceCode = GetString( root, "device_auth_id" ) ?? GetString( root, "device_code" );
		if ( deviceCode == null )
			throw new JsonException( $"Missing 'device_auth_id' in OpenAI response. Raw JSON: {json}" );

		var uri = GetString( root, "verification_uri" ) ?? GetString( root, "verification_url" ) ?? "https://auth.openai.com/codex/device";

		var interval = 5;
		if ( root.TryGetProperty( "interval", out var intEl ) )
		{
			if ( intEl.ValueKind == JsonValueKind.Number ) interval = intEl.GetInt32();
			else if ( intEl.ValueKind == JsonValueKind.String && int.TryParse( intEl.GetString(), out var iv ) ) interval = iv;
		}

		return new DeviceCodeInfo(
			DeviceCode:      deviceCode,
			UserCode:        GetString( root, "user_code" ) ?? GetString( root, "usercode" ) ?? throw new JsonException( "Missing user_code" ),
			VerificationUri: uri,
			ExpiresIn:       GetInt( root, "expires_in" ) ?? 900,
			Interval:        interval );
	}

	public static async Task<TokenResponse> PollForTokenAsync( string deviceCode, string userCode, int intervalSeconds, CancellationToken ct )
	{
		var deadline = DateTime.UtcNow.AddMinutes( 15 );

		while ( DateTime.UtcNow < deadline && !ct.IsCancellationRequested )
		{
			await Task.Delay( intervalSeconds * 1000, ct ).ConfigureAwait( false );

			using var req = new HttpRequestMessage( HttpMethod.Post, "https://auth.openai.com/api/accounts/deviceauth/token" );
			req.Headers.Add( "Accept", "application/json" );

			// Codex sends JSON with just device_auth_id and user_code — no grant_type
			var payload = new { device_auth_id = deviceCode, user_code = userCode };
			req.Content = new StringContent( JsonSerializer.Serialize( payload ), Encoding.UTF8, "application/json" );

			var response = await s_Http.SendAsync( req, ct ).ConfigureAwait( false );
			var body     = await response.Content.ReadAsStringAsync( ct ).ConfigureAwait( false );

			// 403 Forbidden or 404 Not Found = authorization pending — keep polling
			if ( response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
			     response.StatusCode == System.Net.HttpStatusCode.NotFound )
			{
				continue;
			}

			if ( response.IsSuccessStatusCode )
			{
				using var doc = JsonDocument.Parse( body );
				var root = doc.RootElement;

				// Codex device flow returns authorization_code + code_verifier (not access_token)
				var authCode     = GetString( root, "authorization_code" );
				var codeVerifier = GetString( root, "code_verifier" );

				if ( authCode != null && codeVerifier != null )
					return await ExchangeAuthCodeAsync( authCode, codeVerifier ).ConfigureAwait( false );

				// Fallback: some flows may return tokens directly
				var accessToken = GetString( root, "access_token" );
				if ( accessToken != null )
				{
					return new TokenResponse(
						AccessToken:  accessToken,
						RefreshToken: GetString( root, "refresh_token" ) ?? string.Empty,
						ExpiresIn:    GetInt( root, "expires_in" ) ?? 0 );
				}

				throw new InvalidOperationException( $"Unexpected OpenAI polling response: {body}" );
			}

			throw new InvalidOperationException( $"OpenAI auth error {(int)response.StatusCode}: {body}" );
		}

		throw new InvalidOperationException( "OpenAI auth timed out." );
	}

	private static async Task<TokenResponse> ExchangeAuthCodeAsync( string authCode, string codeVerifier )
	{
		using var req = new HttpRequestMessage( HttpMethod.Post, "https://auth.openai.com/oauth/token" );

		req.Content = new FormUrlEncodedContent( new Dictionary<string, string>
		{
			["grant_type"]    = "authorization_code",
			["code"]          = authCode,
			["redirect_uri"]  = "https://auth.openai.com/deviceauth/callback",
			["client_id"]     = ClientId,
			["code_verifier"] = codeVerifier,
		} );

		var response = await s_Http.SendAsync( req ).ConfigureAwait( false );
		var json     = await response.Content.ReadAsStringAsync().ConfigureAwait( false );

		if ( !response.IsSuccessStatusCode )
			throw new InvalidOperationException( $"OpenAI code exchange failed: {response.StatusCode}\n{json}" );

		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;

		return new TokenResponse(
			AccessToken:  GetString( root, "access_token" )  ?? throw new JsonException( "Missing access_token in token exchange" ),
			RefreshToken: GetString( root, "refresh_token" ) ?? string.Empty,
			ExpiresIn:    GetInt( root, "expires_in" )    ?? 0 );
	}

	public static async Task<TokenResponse> RefreshTokenAsync( string refreshToken )
	{
		using var req = new HttpRequestMessage( HttpMethod.Post, "https://auth.openai.com/oauth/token" );
		req.Headers.Add( "Accept", "application/json" );

		// Codex refresh uses JSON (not form-encoded)
		var payload = new
		{
			client_id     = ClientId,
			grant_type    = "refresh_token",
			refresh_token = refreshToken,
		};
		req.Content = new StringContent( JsonSerializer.Serialize( payload ), Encoding.UTF8, "application/json" );

		var response = await s_Http.SendAsync( req ).ConfigureAwait( false );
		var json     = await response.Content.ReadAsStringAsync().ConfigureAwait( false );

		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;

		if ( !response.IsSuccessStatusCode )
		{
			var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty( "error", out var e )
				? ( e.ValueKind == JsonValueKind.String ? e.GetString() : GetString( e, "message" ) ?? e.ToString() )
				: response.ReasonPhrase;
			throw new InvalidOperationException( $"OpenAI token refresh failed: {response.StatusCode} - {error}" );
		}

		return new TokenResponse(
			AccessToken:  GetString( root, "access_token" )  ?? throw new JsonException( "Missing access_token" ),
			RefreshToken: GetString( root, "refresh_token" ) ?? refreshToken,
			ExpiresIn:    GetInt( root, "expires_in" )    ?? 0 );
	}

	private static string? GetString( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object && el.TryGetProperty( key, out var p ) && p.ValueKind == JsonValueKind.String )
			return p.GetString();
		return null;
	}

	private static int? GetInt( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object && el.TryGetProperty( key, out var p ) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32( out var v ) )
			return v;
		return null;
	}
}
