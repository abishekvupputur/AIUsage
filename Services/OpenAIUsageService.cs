using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CopilotUsage.Models;

namespace CopilotUsage.Services;

internal sealed class OpenAIUsageService
{
	private static readonly HttpClient s_Http = new();

	public async Task<CopilotUsageData> GetUsageAsync( string accessToken )
	{
		if ( string.IsNullOrWhiteSpace( accessToken ) )
			throw new InvalidOperationException( "No OpenAI token set. Open Settings and connect your OpenAI account." );

		var json = await FetchAsync( accessToken ).ConfigureAwait( false );
		return ParseResponse( json );
	}

	public async Task<string> GetRawUsageJsonAsync( string accessToken )
	{
		if ( string.IsNullOrWhiteSpace( accessToken ) )
			throw new InvalidOperationException( "No OpenAI token set. Open Settings and connect your OpenAI account." );

		var json = await FetchAsync( accessToken ).ConfigureAwait( false );
		using var doc = JsonDocument.Parse( json );
		return JsonSerializer.Serialize( doc.RootElement, new JsonSerializerOptions { WriteIndented = true } );
	}

	private static async Task<string> FetchAsync( string accessToken )
	{
		using var req = new HttpRequestMessage( HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage" );
		req.Headers.Add( "Authorization", $"Bearer {accessToken}" );
		req.Headers.Add( "User-Agent", "AIUsage/1.0" );
		req.Headers.Add( "Accept", "application/json" );

		var response = await s_Http.SendAsync( req ).ConfigureAwait( false );
		var body = await response.Content.ReadAsStringAsync().ConfigureAwait( false );

		if ( !response.IsSuccessStatusCode )
		{
			if ( response.StatusCode == System.Net.HttpStatusCode.Unauthorized )
				throw new UnauthorizedAccessException( "OpenAI token expired or invalid." );
			
			throw new InvalidOperationException( $"OpenAI API error {(int)response.StatusCode}: {response.ReasonPhrase}\n{TrimBody( body )}" );
		}

		return body;
	}

	private static CopilotUsageData ParseResponse( string json )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;

		// New structure: rate_limit.{primary_window, secondary_window}
		if ( root.TryGetProperty( "rate_limit", out var rateLimitEl ) && rateLimitEl.ValueKind == JsonValueKind.Object )
		{
			double sessionPercent = 0, weeklyPercent = 0;
			int? sessionUsed = null, sessionLimit = null;
			int? weeklyUsed = null, weeklyLimit = null;
			DateTimeOffset? sessionResetsAt = null, weeklyResetsAt = null;

			if ( rateLimitEl.TryGetProperty( "primary_window", out var primary ) && primary.ValueKind == JsonValueKind.Object )
				ExtractWindow( primary, ref sessionPercent, ref sessionUsed, ref sessionLimit, ref sessionResetsAt );

			if ( rateLimitEl.TryGetProperty( "secondary_window", out var secondary ) && secondary.ValueKind == JsonValueKind.Object )
				ExtractWindow( secondary, ref weeklyPercent, ref weeklyUsed, ref weeklyLimit, ref weeklyResetsAt );

			return new CopilotUsageData
			{
				SessionPercent  = sessionPercent,
				SessionUsed     = sessionUsed,
				SessionLimit    = sessionLimit,
				SessionResetsAt = sessionResetsAt,
				WeeklyPercent   = weeklyPercent,
				WeeklyUsed      = weeklyUsed,
				WeeklyLimit     = weeklyLimit,
				WeeklyResetsAt  = weeklyResetsAt,
			};
		}

		// Legacy structure fallback: local_messages / five_hour_window / weekly_messages
		{
			double sessionPercent = 0, weeklyPercent = 0;
			int? sessionUsed = null, sessionLimit = null;
			int? weeklyUsed = null, weeklyLimit = null;
			DateTimeOffset? sessionResetsAt = null, weeklyResetsAt = null;

			if ( root.TryGetProperty( "local_messages", out var local ) )
				ExtractQuota( local, ref sessionPercent, ref sessionUsed, ref sessionLimit, ref sessionResetsAt );
			else if ( root.TryGetProperty( "five_hour_window", out var fiveHour ) )
				ExtractQuota( fiveHour, ref sessionPercent, ref sessionUsed, ref sessionLimit, ref sessionResetsAt );

			if ( root.TryGetProperty( "weekly_messages", out var weekly ) )
				ExtractQuota( weekly, ref weeklyPercent, ref weeklyUsed, ref weeklyLimit, ref weeklyResetsAt );

			if ( sessionUsed == null && root.TryGetProperty( "cloud_tasks", out var cloud ) )
				ExtractQuota( cloud, ref sessionPercent, ref sessionUsed, ref sessionLimit, ref sessionResetsAt );

			if ( sessionUsed != null || weeklyUsed != null )
			{
				return new CopilotUsageData
				{
					SessionPercent  = sessionPercent,
					SessionUsed     = sessionUsed,
					SessionLimit    = sessionLimit,
					SessionResetsAt = sessionResetsAt,
					WeeklyPercent   = weeklyPercent,
					WeeklyUsed      = weeklyUsed,
					WeeklyLimit     = weeklyLimit,
					WeeklyResetsAt  = weeklyResetsAt,
				};
			}
		}

		return new CopilotUsageData { IsUnavailable = true, RawJson = TrimBody( json ) };
	}

	private static void ExtractWindow( JsonElement el, ref double percent, ref int? used, ref int? limit, ref DateTimeOffset? resetsAt )
	{
		var p = GetDouble( el, "used_percent" );
		var u = GetInt( el, "used" ) ?? GetInt( el, "messages_used" );
		var l = GetInt( el, "limit" ) ?? GetInt( el, "messages_limit" ) ?? GetInt( el, "cap" );

		if ( p.HasValue )
			percent = Math.Clamp( p.Value, 0, 100 );

		if ( l.HasValue && l.Value > 0 )
		{
			limit = l;
			used  = u ?? (int)Math.Round( percent * l.Value / 100.0 );
		}
		else if ( u.HasValue )
		{
			used = u;
		}

		// reset_at is a Unix timestamp (seconds)
		if ( el.TryGetProperty( "reset_at", out var resetEl ) && resetEl.ValueKind == JsonValueKind.Number && resetEl.TryGetInt64( out var unix ) )
			resetsAt = DateTimeOffset.FromUnixTimeSeconds( unix );
	}

	private static void ExtractQuota( JsonElement el, ref double percent, ref int? used, ref int? limit, ref DateTimeOffset? resetsAt )
	{
		var u = GetInt( el, "used" );
		var l = GetInt( el, "limit" ) ?? GetInt( el, "cap" ) ?? GetInt( el, "max" );
		var p = GetDouble( el, "percent_used" ) ?? GetDouble( el, "utilization" );
		var r = GetString( el, "resets_at" ) ?? GetString( el, "reset_at" );

		if ( l.HasValue && l.Value > 0 )
		{
			limit = l;
			used  = u ?? 0;
			percent = p ?? Math.Clamp( ( used.Value * 100.0 ) / l.Value, 0, 100 );
		}
		else if ( p.HasValue )
		{
			percent = Math.Clamp( p.Value, 0, 100 );
		}

		if ( !string.IsNullOrEmpty( r ) && DateTimeOffset.TryParse( r, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt ) )
			resetsAt = dt;
	}

	private static int? GetInt( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object && el.TryGetProperty( key, out var p ) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32( out var v ) )
			return v;
		return null;
	}

	private static double? GetDouble( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object && el.TryGetProperty( key, out var p ) && p.ValueKind == JsonValueKind.Number )
			return p.GetDouble();
		return null;
	}

	private static string? GetString( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object && el.TryGetProperty( key, out var p ) && p.ValueKind == JsonValueKind.String )
			return p.GetString();
		return null;
	}

	private static string TrimBody( string body ) =>
		body.Length > 400 ? body[..400] + "…" : body;
}
