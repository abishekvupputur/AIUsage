using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CopilotUsage.Models;

namespace CopilotUsage.Services;

internal sealed class GeminiUsageService
{
	private static readonly HttpClient s_Http = new();

	public async Task<List<GeminiQuotaBucket>> GetQuotaAsync(
		string clientId, string clientSecret, string credentialsPath )
	{
		var accessToken = await RefreshTokenAsync( clientId, clientSecret, credentialsPath ).ConfigureAwait( false );
		return await FetchBucketsAsync( accessToken ).ConfigureAwait( false );
	}

	public async Task<string> GetRawJsonAsync(
		string clientId, string clientSecret, string credentialsPath )
	{
		var accessToken = await RefreshTokenAsync( clientId, clientSecret, credentialsPath ).ConfigureAwait( false );
		var raw = await FetchRawAsync( accessToken ).ConfigureAwait( false );
		using var doc = JsonDocument.Parse( raw );
		return JsonSerializer.Serialize( doc.RootElement, new JsonSerializerOptions { WriteIndented = true } );
	}

	private static async Task<string> RefreshTokenAsync( string clientId, string clientSecret, string credentialsPath )
	{
		if ( string.IsNullOrWhiteSpace( clientId ) )
			throw new InvalidOperationException( "Gemini Client ID is not set. Open Settings → Gemini." );
		if ( string.IsNullOrWhiteSpace( clientSecret ) )
			throw new InvalidOperationException( "Gemini Client Secret is not set. Open Settings → Gemini." );

		var expandedPath = Environment.ExpandEnvironmentVariables( credentialsPath );
		if ( !File.Exists( expandedPath ) )
			throw new InvalidOperationException( $"Credentials file not found: {expandedPath}" );

		var credsJson = await File.ReadAllTextAsync( expandedPath ).ConfigureAwait( false );
		using var credsDoc = JsonDocument.Parse( credsJson );
		if ( !credsDoc.RootElement.TryGetProperty( "refresh_token", out var rtEl ) )
			throw new InvalidOperationException(
				$"No 'refresh_token' in credentials file. Keys found: {string.Join( ", ", credsDoc.RootElement.EnumerateObject().Select( p => p.Name ) )}" );
		var refreshToken = rtEl.GetString()
			?? throw new InvalidOperationException( "refresh_token is null in credentials file." );

		var form = new FormUrlEncodedContent( new Dictionary<string, string>
		{
			["client_id"]     = clientId,
			["client_secret"] = clientSecret,
			["refresh_token"] = refreshToken,
			["grant_type"]    = "refresh_token",
		} );

		var response = await s_Http.PostAsync( "https://oauth2.googleapis.com/token", form ).ConfigureAwait( false );
		var body     = await response.Content.ReadAsStringAsync().ConfigureAwait( false );

		if ( !response.IsSuccessStatusCode )
			throw new InvalidOperationException( $"Token refresh failed ({(int)response.StatusCode}): {body}" );

		using var tokenDoc = JsonDocument.Parse( body );
		if ( !tokenDoc.RootElement.TryGetProperty( "access_token", out var atEl ) )
			throw new InvalidOperationException(
				$"No 'access_token' in token response. Response: {body[..Math.Min( 500, body.Length )]}" );
		return atEl.GetString()
			?? throw new InvalidOperationException( "access_token is null in token response." );
	}

	private static async Task<string> LoadProjectIdAsync( string accessToken )
	{
		using var req = new HttpRequestMessage(
			HttpMethod.Post,
			"https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist" );
		req.Headers.Add( "Authorization", $"Bearer {accessToken}" );
		req.Content = new StringContent(
			"""{"metadata":{"platform":"PLATFORM_UNSPECIFIED","ideVersion":"","pluginVersion":""}}""",
			Encoding.UTF8, "application/json" );

		var response = await s_Http.SendAsync( req ).ConfigureAwait( false );
		var body     = await response.Content.ReadAsStringAsync().ConfigureAwait( false );

		if ( !response.IsSuccessStatusCode )
			throw new InvalidOperationException( $"loadCodeAssist failed ({(int)response.StatusCode}): {body}" );

		using var doc  = JsonDocument.Parse( body );
		var projProp = doc.RootElement.EnumerateObject()
			.FirstOrDefault( p => p.Name.Equals( "cloudaicompanionproject", StringComparison.OrdinalIgnoreCase ) );
		if ( projProp.Value.ValueKind == JsonValueKind.Undefined )
			throw new InvalidOperationException(
				$"No project ID field in loadCodeAssist response. Response: {body[..Math.Min( 800, body.Length )]}" );
		return projProp.Value.GetString()
			?? throw new InvalidOperationException( "Project ID is null in loadCodeAssist response." );
	}

	private static async Task<List<GeminiQuotaBucket>> FetchBucketsAsync( string accessToken )
	{
		var raw = await FetchRawAsync( accessToken ).ConfigureAwait( false );
		return ParseBuckets( raw );
	}

	private static async Task<string> FetchRawAsync( string accessToken )
	{
		var projectId = await LoadProjectIdAsync( accessToken ).ConfigureAwait( false );
		var body      = $$$"""{"project":"{{{projectId}}}"}""";

		using var req = new HttpRequestMessage(
			HttpMethod.Post,
			"https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota" );
		req.Headers.Add( "Authorization", $"Bearer {accessToken}" );
		req.Content = new StringContent( body, Encoding.UTF8, "application/json" );

		var response = await s_Http.SendAsync( req ).ConfigureAwait( false );
		var respBody = await response.Content.ReadAsStringAsync().ConfigureAwait( false );

		if ( !response.IsSuccessStatusCode )
			throw new InvalidOperationException( $"Gemini quota API error ({(int)response.StatusCode}): {respBody}" );

		return respBody;
	}

	private static List<GeminiQuotaBucket> ParseBuckets( string json )
	{
		using var doc  = JsonDocument.Parse( json );
		var root = doc.RootElement;

		if ( !root.TryGetProperty( "buckets", out var bucketsEl ) )
			throw new InvalidOperationException(
				$"No 'buckets' in Gemini response: {json[..Math.Min( 500, json.Length )]}" );

		var buckets = new List<GeminiQuotaBucket>();
		foreach ( var b in bucketsEl.EnumerateArray() )
		{
			var modelId  = b.TryGetProperty( "modelId",           out var mEl ) ? mEl.GetString() ?? "" : "";
			var fraction = b.TryGetProperty( "remainingFraction", out var fEl ) ? fEl.GetDouble() : 0.0;
			var resetStr = b.TryGetProperty( "resetTime",         out var rEl ) ? rEl.GetString() ?? "" : "";

			buckets.Add( new GeminiQuotaBucket
			{
				ModelId           = modelId,
				RemainingFraction = fraction,
				ResetTime         = DateTimeOffset.TryParse( resetStr, out var dt )
					? dt
					: DateTimeOffset.Now.AddHours( 1 ),
			} );
		}

		return buckets;
	}
}
