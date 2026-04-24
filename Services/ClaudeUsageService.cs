using System.Globalization;
using System.Text.Json;
using CopilotUsage.Models;
using WpfApp = System.Windows.Application;

namespace CopilotUsage.Services;

/// <summary>
/// Fetches Claude Pro usage limits from the claude.ai internal API.
/// Calls are routed through <see cref="ClaudeWebViewFetcher"/>, which uses
/// an embedded Chromium browser to bypass Cloudflare's TLS fingerprint checks.
/// </summary>
internal sealed class ClaudeUsageService
{
	/// <summary>
	/// Returns the raw JSON string from the rate-limit endpoint for diagnostic inspection.
	/// </summary>
	public async Task<string> GetRawUsageJsonAsync( string sessionKey )
	{
		if ( string.IsNullOrWhiteSpace( sessionKey ) )
			throw new InvalidOperationException( "No session key set. Open Settings and paste your claude.ai session key." );

		var bareKey = ExtractBareSessionKey( sessionKey );
		var orgUuid = await GetOrganizationUuidAsync( bareKey ).ConfigureAwait( true );

		string[] candidates =
		[
			$"/api/organizations/{orgUuid}/rate_limit_status",
			$"/api/organizations/{orgUuid}/usage",
			$"/api/organizations/{orgUuid}/limits",
			$"/api/organizations/{orgUuid}/rate_limits",
		];

		Exception? lastError = null;
		foreach ( var path in candidates )
		{
			try
			{
				var body = await GetAsync( bareKey, path ).ConfigureAwait( true );
				// Pretty-print so it's readable in the window
				using var doc = System.Text.Json.JsonDocument.Parse( body );
				return System.Text.Json.JsonSerializer.Serialize(
					doc.RootElement,
					new System.Text.Json.JsonSerializerOptions { WriteIndented = true } );
			}
			catch ( InvalidOperationException ex ) when ( ex.Message.Contains( "HTTP 404" ) )
			{
				lastError = ex;
			}
		}

		throw new InvalidOperationException( $"Could not find usage endpoint.\n\nLast error: {lastError?.Message}" );
	}

	public async Task<CopilotUsageData> GetUsageAsync( string sessionKey )
	{
		if ( string.IsNullOrWhiteSpace( sessionKey ) )
		{
			throw new InvalidOperationException(
				"No session key set. Open Settings and paste your claude.ai session key." );
		}

		// Strip "sessionKey=" prefix in case the user pasted a full cookie string.
		var bareKey = ExtractBareSessionKey( sessionKey );

		var orgUuid = await GetOrganizationUuidAsync( bareKey ).ConfigureAwait( true );
		return await GetRateLimitStatusAsync( bareKey, orgUuid ).ConfigureAwait( true );
	}


	private async Task<string> GetOrganizationUuidAsync( string sessionKey )
	{
		var body = await GetAsync( sessionKey, "/api/organizations" ).ConfigureAwait( true );

		using var doc = JsonDocument.Parse( body );
		var root = doc.RootElement;

		if ( root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 )
		{
			throw new InvalidOperationException(
				$"No Claude organizations found in your account.\n\nResponse: {TrimBody( body )}" );
		}

		var firstOrg = root[0];
		if ( firstOrg.ValueKind == JsonValueKind.Object && firstOrg.TryGetProperty( "uuid", out var uuidEl ) )
		{
			var uuid = uuidEl.GetString();
			if ( !string.IsNullOrEmpty( uuid ) )
			{
				return uuid;
			}
		}

		throw new InvalidOperationException(
			$"Organization UUID not found in response.\n\nResponse: {TrimBody( body )}" );
	}


	private async Task<CopilotUsageData> GetRateLimitStatusAsync( string sessionKey, string orgUuid )
	{
		// Try known endpoint variants in case the claude.ai API path has changed.
		string[] candidates =
		[
			$"/api/organizations/{orgUuid}/rate_limit_status",
			$"/api/organizations/{orgUuid}/usage",
			$"/api/organizations/{orgUuid}/limits",
			$"/api/organizations/{orgUuid}/rate_limits",
		];

		Exception? lastError = null;
		foreach ( var path in candidates )
		{
			try
			{
				var body = await GetAsync( sessionKey, path ).ConfigureAwait( true );
				return ParseRateLimitResponse( body );
			}
			catch ( InvalidOperationException ex ) when ( ex.Message.Contains( "HTTP 404" ) )
			{
				lastError = ex;
				// Try next candidate.
			}
		}

		throw new InvalidOperationException(
			$"Could not find the usage/rate-limit endpoint on claude.ai.\n\n" +
			$"Last error: {lastError?.Message}\n\n" +
			"Open claude.ai in your browser, press F12 → Network tab, look for an API call " +
			"that returns your usage data, and report the path so it can be added here." );
	}


	/// <summary>
	/// Dispatches to the WPF UI thread (required by WebView2) and calls the API
	/// via JS fetch() inside the embedded Chromium browser.
	/// </summary>
	private static Task<string> GetAsync( string sessionKey, string path )
	{
		var dispatcher = WpfApp.Current.Dispatcher;

		if ( dispatcher.CheckAccess() )
		{
			// Already on the UI thread — call directly.
			return ClaudeWebViewFetcher.Instance.FetchApiAsync( sessionKey, path );
		}

		// Marshal to UI thread and unwrap the inner Task<string>.
		return dispatcher.InvokeAsync(
			() => ClaudeWebViewFetcher.Instance.FetchApiAsync( sessionKey, path ) ).Task.Unwrap();
	}


	// -------------------------------------------------------------------------
	// JSON parsing (unchanged — tries multiple API response shapes)
	// -------------------------------------------------------------------------

	private static CopilotUsageData ParseRateLimitResponse( string json )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;

		double sessionPct = 0;
		DateTimeOffset? sessionResetsAt = null;
		int? sessionUsed = null, sessionLimit = null;
		double weeklyPct = 0;
		DateTimeOffset? weeklyResetsAt = null;
		int? weeklyUsed = null, weeklyLimit = null;

		bool sessionFound = false, weeklyFound = false;

		// Shape 1: { "rate_limit_status": { "current_session": {...}, "weekly": {...} } }
		if ( root.ValueKind == JsonValueKind.Object && root.TryGetProperty( "rate_limit_status", out var rlStatus ) )
		{
			if ( !sessionFound ) sessionFound = TryParseLimitEntry( rlStatus, "current_session", ref sessionPct, ref sessionResetsAt, ref sessionUsed, ref sessionLimit );
			if ( !sessionFound ) sessionFound = TryParseLimitEntry( rlStatus, "session",         ref sessionPct, ref sessionResetsAt, ref sessionUsed, ref sessionLimit );
			if ( !weeklyFound )  weeklyFound  = TryParseLimitEntry( rlStatus, "weekly",          ref weeklyPct,  ref weeklyResetsAt,  ref weeklyUsed,  ref weeklyLimit );
			if ( !weeklyFound )  weeklyFound  = TryParseLimitEntry( rlStatus, "week",            ref weeklyPct,  ref weeklyResetsAt,  ref weeklyUsed,  ref weeklyLimit );
		}

		// Shape 2: flat root-level keys
		if ( !sessionFound ) sessionFound = TryParseLimitEntry( root, "current_session", ref sessionPct, ref sessionResetsAt, ref sessionUsed, ref sessionLimit );
		if ( !sessionFound ) sessionFound = TryParseLimitEntry( root, "session",         ref sessionPct, ref sessionResetsAt, ref sessionUsed, ref sessionLimit );
		if ( !sessionFound ) sessionFound = TryParseLimitEntry( root, "five_hour",       ref sessionPct, ref sessionResetsAt, ref sessionUsed, ref sessionLimit );
		if ( !weeklyFound )  weeklyFound  = TryParseLimitEntry( root, "weekly",          ref weeklyPct,  ref weeklyResetsAt,  ref weeklyUsed,  ref weeklyLimit );
		if ( !weeklyFound )  weeklyFound  = TryParseLimitEntry( root, "week",            ref weeklyPct,  ref weeklyResetsAt,  ref weeklyUsed,  ref weeklyLimit );
		if ( !weeklyFound )  weeklyFound  = TryParseLimitEntry( root, "seven_day",       ref weeklyPct,  ref weeklyResetsAt,  ref weeklyUsed,  ref weeklyLimit );

		// Shape 3: array of entries with a "type" / "name" / "id" discriminator
		foreach ( var searchRoot in new[] { root, GetChildElement( root, "rate_limit_status" ), GetChildElement( root, "limits" ) } )
		{
			if ( searchRoot.ValueKind != JsonValueKind.Array ) continue;

			foreach ( var entry in searchRoot.EnumerateArray() )
			{
				var entryType = GetString( entry, "type" )
					?? GetString( entry, "name" )
					?? GetString( entry, "id" )
					?? string.Empty;

				if ( !sessionFound && entryType.Contains( "session", StringComparison.OrdinalIgnoreCase ) )
				{
					ExtractPercent( entry, ref sessionPct, ref sessionResetsAt, ref sessionUsed, ref sessionLimit );
					sessionFound = true;
				}

				if ( !weeklyFound && entryType.Contains( "week", StringComparison.OrdinalIgnoreCase ) )
				{
					ExtractPercent( entry, ref weeklyPct, ref weeklyResetsAt, ref weeklyUsed, ref weeklyLimit );
					weeklyFound = true;
				}
			}
		}

		if ( !sessionFound && !weeklyFound )
			return new CopilotUsageData { IsUnavailable = true, RawJson = TrimBody( json ) };

		// Parse extra_usage block (pay-per-use credits)
		bool extraEnabled = false;
		double? extraMonthlyLimitEur = null;
		double? extraUsedEur = null;
		double extraPercent = 0;

		if ( root.ValueKind == JsonValueKind.Object
			&& root.TryGetProperty( "extra_usage", out var extraEl )
			&& extraEl.ValueKind == JsonValueKind.Object
			&& extraEl.TryGetProperty( "is_enabled", out var isEnabledEl )
			&& isEnabledEl.ValueKind == JsonValueKind.True )
		{
			extraEnabled = true;
			if ( extraEl.TryGetProperty( "monthly_limit", out var limitEl ) && limitEl.ValueKind == JsonValueKind.Number )
				extraMonthlyLimitEur = limitEl.GetDouble() / 100.0;
			if ( extraEl.TryGetProperty( "used_credits", out var usedEl ) && usedEl.ValueKind == JsonValueKind.Number )
				extraUsedEur = usedEl.GetDouble() / 100.0;
			if ( extraEl.TryGetProperty( "utilization", out var utilEl ) && utilEl.ValueKind == JsonValueKind.Number )
				extraPercent = Math.Clamp( utilEl.GetDouble(), 0, 100 );
		}

		return new CopilotUsageData
		{
			SessionPercent         = sessionPct,
			SessionUsed            = sessionUsed,
			SessionLimit           = sessionLimit,
			SessionResetsAt        = sessionResetsAt,
			WeeklyPercent          = weeklyPct,
			WeeklyUsed             = weeklyUsed,
			WeeklyLimit            = weeklyLimit,
			WeeklyResetsAt         = weeklyResetsAt,
			ExtraUsageEnabled      = extraEnabled,
			ExtraUsageMonthlyLimitEur = extraMonthlyLimitEur,
			ExtraUsageUsedEur      = extraUsedEur,
			ExtraUsagePercent      = extraPercent,
		};
	}


	private static bool TryParseLimitEntry(
		JsonElement parent, string key,
		ref double percent, ref DateTimeOffset? resetsAt,
		ref int? outUsed, ref int? outLimit )
	{
		if ( parent.ValueKind != JsonValueKind.Object ) return false;
		if ( !parent.TryGetProperty( key, out var entry ) ) return false;
		if ( entry.ValueKind == JsonValueKind.Null ) return false;
		ExtractPercent( entry, ref percent, ref resetsAt, ref outUsed, ref outLimit );
		return true;
	}


	private static void ExtractPercent(
		JsonElement entry,
		ref double percent, ref DateTimeOffset? resetsAt,
		ref int? outUsed, ref int? outLimit )
	{
		if ( entry.ValueKind != JsonValueKind.Object ) return;
		if ( entry.TryGetProperty( "percent_used", out var pctEl ) && pctEl.ValueKind == JsonValueKind.Number )
		{
			percent = Math.Clamp( pctEl.GetDouble(), 0, 100 );
		}
		else if ( entry.TryGetProperty( "utilization", out var utilEl ) && utilEl.ValueKind == JsonValueKind.Number )
		{
			percent = Math.Clamp( utilEl.GetDouble(), 0, 100 );
		}
		else
		{
			int? limit     = GetInt( entry, "limit" ) ?? GetInt( entry, "budget" ) ?? GetInt( entry, "entitlement" );
			int? remaining = GetInt( entry, "remaining" );
			int? used      = GetInt( entry, "used" );

			if ( limit.HasValue && limit.Value > 0 )
			{
				int actualUsed = used.HasValue && used.Value > 0
					? used.Value
					: ( limit.Value - ( remaining ?? 0 ) );
				percent  = Math.Clamp( actualUsed * 100.0 / limit.Value, 0, 100 );
				outUsed  = actualUsed;
				outLimit = limit.Value;
			}
		}

		if ( resetsAt == null )
		{
			var str = GetString( entry, "resets_at" )
				?? GetString( entry, "reset_at" )
				?? GetString( entry, "resetAt" );
			if ( !string.IsNullOrEmpty( str )
				&& DateTimeOffset.TryParse( str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt ) )
			{
				resetsAt = dt;
			}
		}
	}


	// -------------------------------------------------------------------------
	// Helpers
	// -------------------------------------------------------------------------

	/// <summary>
	/// Extracts the bare session key value from either:
	///   - a raw key: "sk-ant-sid02-..."
	///   - a full cookie string: "sessionKey=sk-ant-...; __cf_bm=..."
	/// </summary>
	private static string ExtractBareSessionKey( string input )
	{
		var idx = input.IndexOf( "sessionKey=", StringComparison.Ordinal );
		if ( idx < 0 ) return input.Trim();

		var start = idx + "sessionKey=".Length;
		var end   = input.IndexOf( ';', start );
		return end < 0
			? input[ start.. ].Trim()
			: input[ start..end ].Trim();
	}

	private static JsonElement GetChildElement( JsonElement parent, string key )
	{
		if ( parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty( key, out var child ) )
			return child;
		return default;
	}

	private static string? GetString( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object
			&& el.TryGetProperty( key, out var prop )
			&& prop.ValueKind == JsonValueKind.String )
		{
			return prop.GetString();
		}
		return null;
	}

	private static int? GetInt( JsonElement el, string key )
	{
		if ( el.ValueKind == JsonValueKind.Object
			&& el.TryGetProperty( key, out var prop )
			&& prop.ValueKind == JsonValueKind.Number
			&& prop.TryGetInt32( out var val ) )
		{
			return val;
		}
		return null;
	}

	private static string TrimBody( string body ) =>
		body.Length > 400 ? body[..400] + "…" : body;
}
