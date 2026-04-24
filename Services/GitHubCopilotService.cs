using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CopilotUsage.Models;

namespace CopilotUsage.Services;

internal sealed class GitHubCopilotService
{
	private static readonly HttpClient s_Http = new();

	public async Task<CopilotUsageData> GetUsageAsync( string token )
	{
		if ( string.IsNullOrWhiteSpace( token ) )
			throw new InvalidOperationException( "No GitHub token set. Open Settings and connect your GitHub account." );

		var json = await FetchAsync( token ).ConfigureAwait( false );
		return ParseResponse( json );
	}

	public async Task<string> GetRawUsageJsonAsync( string token )
	{
		if ( string.IsNullOrWhiteSpace( token ) )
			throw new InvalidOperationException( "No GitHub token set. Open Settings and connect your GitHub account." );

		var json = await FetchAsync( token ).ConfigureAwait( false );
		using var doc = JsonDocument.Parse( json );
		return JsonSerializer.Serialize( doc.RootElement, new JsonSerializerOptions { WriteIndented = true } );
	}

	private static async Task<string> FetchAsync( string token )
	{
		using var req = new HttpRequestMessage( HttpMethod.Get, "https://api.github.com/copilot_internal/user" );
		req.Headers.Add( "Authorization", $"token {token}" );
		req.Headers.Add( "User-Agent", "AIUsage/1.0" );
		req.Headers.Add( "Accept", "application/json" );

		var response = await s_Http.SendAsync( req ).ConfigureAwait( false );
		var body = await response.Content.ReadAsStringAsync().ConfigureAwait( false );

		if ( !response.IsSuccessStatusCode )
			throw new InvalidOperationException( $"GitHub API error {(int)response.StatusCode}: {response.ReasonPhrase}\n{TrimBody( body )}" );

		return body;
	}

	private static CopilotUsageData ParseResponse( string json )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;

		double percent = 0;
		int? used = null, limit = null;
		DateTimeOffset? resetsAt = null;

		double weeklyPercent = 0;
		int? weeklyUsed = null, weeklyLimit = null;

		// Shape 1: quota_snapshots.premium_interactions (Pro)
		if ( root.TryGetProperty( "quota_snapshots", out var snapshots )
			&& snapshots.TryGetProperty( "premium_interactions", out var premium ) )
		{
			ExtractCopilotQuota( premium, ref percent, ref used, ref limit );
		}

		// Shape 2: flat root-level premium_interactions_quota fields (Pro)
		if ( used == null )
		{
			var flatUsed   = GetInt( root, "premium_interactions_quota_used" );
			var flatLimit  = GetInt( root, "premium_interactions_quota" );
			if ( flatLimit.HasValue && flatLimit.Value > 0 )
			{
				used    = flatUsed ?? 0;
				limit   = flatLimit;
				percent = Math.Clamp( (used ?? 0) * 100.0 / limit.Value, 0, 100 );
			}
		}

		// Shape 3: free tier — limited_user_quotas (remaining) + monthly_quotas (total)
		if ( used == null
			&& root.TryGetProperty( "limited_user_quotas", out var limitedQuotas )
			&& root.TryGetProperty( "monthly_quotas", out var monthlyQuotas ) )
		{
			var chatRemaining = GetInt( limitedQuotas, "chat" );
			var chatTotal     = GetInt( monthlyQuotas, "chat" );
			if ( chatTotal.HasValue && chatTotal.Value > 0 )
			{
				used    = Math.Max( chatTotal.Value - (chatRemaining ?? 0), 0 );
				limit   = chatTotal;
				percent = Math.Clamp( used.Value * 100.0 / limit.Value, 0, 100 );
			}

			var compRemaining = GetInt( limitedQuotas, "completions" );
			var compTotal     = GetInt( monthlyQuotas, "completions" );
			if ( compTotal.HasValue && compTotal.Value > 0 )
			{
				weeklyUsed    = Math.Max( compTotal.Value - (compRemaining ?? 0), 0 );
				weeklyLimit   = compTotal;
				weeklyPercent = Math.Clamp( weeklyUsed.Value * 100.0 / weeklyLimit.Value, 0, 100 );
			}
		}

		// quota_reset_date (Pro) or limited_user_reset_date (Free)
		var resetStr = GetString( root, "quota_reset_date" ) ?? GetString( root, "limited_user_reset_date" );
		if ( !string.IsNullOrEmpty( resetStr )
			&& DateTimeOffset.TryParse( resetStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt ) )
		{
			resetsAt = dt;
		}

		if ( used == null && limit == null )
			return new CopilotUsageData { IsUnavailable = true, RawJson = TrimBody( json ) };

		return new CopilotUsageData
		{
			SessionPercent  = percent,
			SessionUsed     = used,
			SessionLimit    = limit,
			SessionResetsAt = resetsAt,
			WeeklyPercent   = weeklyPercent,
			WeeklyUsed      = weeklyUsed,
			WeeklyLimit     = weeklyLimit,
			WeeklyResetsAt  = resetsAt,
		};
	}

	private static void ExtractCopilotQuota( JsonElement el, ref double percent, ref int? used, ref int? limit )
	{
		var remaining = GetInt( el, "remaining" ) ?? GetRoundedInt( el, "quota_remaining" );
		var u = GetInt( el, "used" )          ?? GetInt( el, "quota_used" );
		var l = GetInt( el, "limit" )         ?? GetInt( el, "quota" ) ?? GetInt( el, "entitlement" );
		var p = GetDouble( el, "percent_used" );
		var percentRemaining = GetDouble( el, "percent_remaining" );

		if ( !u.HasValue && l.HasValue && remaining.HasValue )
		{
			u = Math.Max( l.Value - remaining.Value, 0 );
		}

		if ( p.HasValue )
		{
			percent = Math.Clamp( p.Value, 0, 100 );
		}
		else if ( percentRemaining.HasValue )
		{
			percent = Math.Clamp( 100.0 - percentRemaining.Value, 0, 100 );
		}
		else if ( l.HasValue && l.Value > 0 )
		{
			used    = u ?? 0;
			limit   = l;
			percent = Math.Clamp( (used ?? 0) * 100.0 / l.Value, 0, 100 );
			return;
		}

		used  = u;
		limit = l;
	}

	private static int? GetInt( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object
			&& el.TryGetProperty( key, out var p )
			&& p.ValueKind == JsonValueKind.Number
			&& p.TryGetInt32( out var v ) )
			return v;
		return null;
	}

	private static double? GetDouble( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object
			&& el.TryGetProperty( key, out var p )
			&& p.ValueKind == JsonValueKind.Number )
			return p.GetDouble();
		return null;
	}

	private static int? GetRoundedInt( JsonElement el, string key )
	{
		var value = GetDouble( el, key );
		return value.HasValue
			? (int)Math.Round( value.Value, MidpointRounding.AwayFromZero )
			: null;
	}

	private static string? GetString( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object
			&& el.TryGetProperty( key, out var p )
			&& p.ValueKind == JsonValueKind.String )
			return p.GetString();
		return null;
	}

	private static string TrimBody( string body ) =>
		body.Length > 400 ? body[..400] + "…" : body;
}
