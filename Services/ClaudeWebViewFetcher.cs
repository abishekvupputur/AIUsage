using System.IO;
using System.Text.Json;
using System.Windows;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CopilotUsage.Services;

/// <summary>
/// Uses an embedded Chromium (WebView2) instance to call claude.ai APIs.
/// Because the requests come from a real Chromium browser process, Cloudflare's
/// TLS-fingerprint check passes and the API responds normally.
///
/// Lifecycle: one shared instance per process, lazily initialised on first use.
/// All public methods must be called on the WPF UI thread.
/// </summary>
internal sealed class ClaudeWebViewFetcher : IDisposable
{
	/// <summary>Process-wide singleton.</summary>
	public static readonly ClaudeWebViewFetcher Instance = new();

	private const string ClaudeOrigin = "https://claude.ai";

	private WebView2? m_WebView;
	private Window?   m_HostWindow;
	private bool      m_Ready;

	private readonly string m_UserDataFolder = Path.Combine(
		Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ),
		"AIUsage", "WebView2" );

	private ClaudeWebViewFetcher() { }


	/// <summary>
	/// Creates the hidden WebView2 host window and navigates to claude.ai so that
	/// Cloudflare runs its JS challenge and populates the cookie jar for this
	/// browser instance's TLS fingerprint.
	/// </summary>
	public async Task EnsureReadyAsync()
	{
		if ( m_Ready ) return;

		try
		{
			m_WebView    = new WebView2();
			m_HostWindow = new Window
			{
				Width         = 1,
				Height        = 1,
				Left          = -32000,
				Top           = -32000,
				ShowInTaskbar = false,
				WindowStyle   = WindowStyle.None,
				Opacity       = 0,
				Content       = m_WebView,
			};
			m_HostWindow.Show();

			var env = await CoreWebView2Environment.CreateAsync(
				null, m_UserDataFolder ).ConfigureAwait( true );
			await m_WebView.EnsureCoreWebView2Async( env ).ConfigureAwait( true );

			// Navigate to claude.ai so Cloudflare sets cf_clearance + __cf_bm
			// for this Chromium instance's TLS fingerprint.
			await NavigateAndWaitAsync( ClaudeOrigin ).ConfigureAwait( true );
		}
		catch ( WebView2RuntimeNotFoundException )
		{
			throw new InvalidOperationException(
				"WebView2 Runtime is not installed.\n\n" +
				"Install Microsoft Edge (which includes WebView2), then restart this app.\n\n" +
				"Download: https://microsoft.com/en-us/edge" );
		}
		catch ( Exception ex )
		{
			throw new InvalidOperationException(
				$"Could not start the embedded browser (WebView2).\n\nDetail: {ex.Message}" );
		}

		m_Ready = true;
	}


	/// <summary>
	/// Injects the session key into the browser cookie jar, then calls
	/// <paramref name="apiPath"/> via JS <c>fetch()</c> from inside the
	/// claude.ai page context (which includes all Cloudflare cookies).
	/// Uses <c>window.chrome.webview.postMessage()</c> to return the result
	/// because <c>ExecuteScriptAsync</c> serialises a Promise as <c>{}</c>
	/// rather than awaiting it.
	/// Returns the raw JSON body string.
	/// </summary>
	public async Task<string> FetchApiAsync( string sessionKey, string apiPath )
	{
		await EnsureReadyAsync().ConfigureAwait( true );
		var core = m_WebView!.CoreWebView2;

		InjectSessionKeyCookie( core, sessionKey );

		if ( !core.Source.StartsWith( ClaudeOrigin, StringComparison.OrdinalIgnoreCase ) )
			await NavigateAndWaitAsync( ClaudeOrigin ).ConfigureAwait( true );

		// Set up a one-shot message listener before firing the script.
		var tcs = new TaskCompletionSource<string>( TaskCreationOptions.RunContinuationsAsynchronously );

		void OnMessage( object? s, CoreWebView2WebMessageReceivedEventArgs e )
		{
			core.WebMessageReceived -= OnMessage;
			tcs.TrySetResult( e.WebMessageAsJson );
		}
		core.WebMessageReceived += OnMessage;

		// Use fetch().then() instead of an async IIFE so the script itself is
		// synchronous — ExecuteScriptAsync's Promise limitation doesn't apply.
		// Results are sent back via window.chrome.webview.postMessage().
		var safePath = apiPath.Replace( "\\", "\\\\" ).Replace( "'", "\\'" );
		var script = $@"
fetch('{safePath}', {{
    credentials: 'include',
    headers: {{
        'Accept': 'application/json, text/plain, */*',
        'anthropic-client-type': 'web',
        'anthropic-version': '2023-06-01'
    }}
}}).then(function(r) {{
    if (!r.ok) {{
        r.text().then(function(b) {{
            window.chrome.webview.postMessage({{__status: r.status, __body: b}});
        }}).catch(function() {{
            window.chrome.webview.postMessage({{__status: r.status, __body: ''}});
        }});
        return;
    }}
    r.json().then(function(d) {{ window.chrome.webview.postMessage(d); }});
}}).catch(function(e) {{
    window.chrome.webview.postMessage({{__error: e.message}});
}});";

		await core.ExecuteScriptAsync( script ).ConfigureAwait( true );

		// Wait up to 30 s for the postMessage callback.
		var winner = await Task.WhenAny( tcs.Task, Task.Delay( TimeSpan.FromSeconds( 30 ) ) )
			.ConfigureAwait( true );
		if ( winner != tcs.Task )
		{
			core.WebMessageReceived -= OnMessage;
			throw new TimeoutException( "claude.ai API call timed out after 30 seconds." );
		}

		var messageJson = await tcs.Task.ConfigureAwait( true );

		using var doc  = JsonDocument.Parse( messageJson );
		var       root = doc.RootElement;

		if ( root.ValueKind == JsonValueKind.Object )
		{
			if ( root.TryGetProperty( "__status", out var statusEl ) )
			{
				var status   = statusEl.GetInt32();
				var bodyHint = root.TryGetProperty( "__body", out var bodyEl ) ? bodyEl.GetString() : "";
				var detail   = string.IsNullOrWhiteSpace( bodyHint ) ? "" : $"\n\nServer said: {TrimBody( bodyHint )}";
				throw new InvalidOperationException(
					$"claude.ai API returned HTTP {status} for {apiPath}.{detail}" );
			}

			if ( root.TryGetProperty( "__error", out var errEl ) )
				throw new InvalidOperationException( $"Browser fetch error: {errEl.GetString()}" );
		}

		return messageJson;
	}


	// -------------------------------------------------------------------------

	private static string TrimBody( string body ) =>
		body.Length > 300 ? body[..300] + "…" : body;

	private static void InjectSessionKeyCookie( CoreWebView2 core, string sessionKey )
	{
		var mgr    = core.CookieManager;
		var cookie = mgr.CreateCookie( "sessionKey", sessionKey, ".claude.ai", "/" );
		cookie.IsSecure   = true;
		cookie.IsHttpOnly = true;
		cookie.SameSite   = CoreWebView2CookieSameSiteKind.None;
		mgr.AddOrUpdateCookie( cookie );
	}

	private async Task NavigateAndWaitAsync( string url )
	{
		var tcs = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );

		void OnCompleted( object? s, CoreWebView2NavigationCompletedEventArgs e )
		{
			m_WebView!.CoreWebView2.NavigationCompleted -= OnCompleted;
			tcs.TrySetResult( true );
		}

		m_WebView!.CoreWebView2.NavigationCompleted += OnCompleted;
		m_WebView.CoreWebView2.Navigate( url );
		await tcs.Task.ConfigureAwait( true );
	}

	public void Dispose()
	{
		m_WebView?.Dispose();
		m_HostWindow?.Close();
	}
}
