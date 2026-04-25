using System.Collections.Generic;
using System.Linq;
using ThreadingTimer = System.Threading.Timer;

using CopilotUsage.Helpers;
using CopilotUsage.Models;
using CopilotUsage.Services;
using CopilotUsage.ViewModels;
using CopilotUsage.Views;

namespace CopilotUsage;

internal sealed class TrayApplicationContext : IDisposable
{
	private readonly ClaudeUsageService    m_ClaudeService   = new();
	private readonly GitHubCopilotService  m_CopilotService  = new();
	private readonly GeminiUsageService    m_GeminiService   = new();
	private readonly OpenAIUsageService    m_OpenAIService   = new();
	private readonly UsageViewModel m_ViewModel;
	private readonly bool m_IsDemoMode;

	private NotifyIcon? m_NotifyIcon;
	private ThreadingTimer? m_RefreshTimer;
	private UsagePopupWindow? m_PopupWindow;
	private bool m_IsRefreshing;

	private readonly Dictionary<UsageProvider, DateTime> m_LastRefreshed = [];

	// Demo mode state
	private int m_DemoStageIndex;


	public TrayApplicationContext( bool isDemoMode = false )
	{
		m_ViewModel = new UsageViewModel();
		m_IsDemoMode = isDemoMode;

		InitializeTrayIcon();

		if ( m_IsDemoMode )
		{
			StartDemoTimer();
		}
		else
		{
			RestartTimer();
			_ = RefreshAsync();
		}
	}


	private void InitializeTrayIcon()
	{
		var contextMenu = new ContextMenuStrip
		{
			Renderer  = new DarkMenuRenderer(),
			BackColor = Color.FromArgb( 0x1A, 0x1A, 0x1A ),
			ForeColor = Color.FromArgb( 0xEB, 0xEB, 0xEB ),
		};
		contextMenu.Items.Add( "Refresh", null, async ( _, _ ) => await RefreshAsync().ConfigureAwait( false ) );
		contextMenu.Items.Add( "Settings…", null, ( _, _ ) => OpenSettings() );
		contextMenu.Items.Add( "Raw API Response…", null, ( _, _ ) =>
		{
			var s = SettingsService.Load();
			var providers = new Dictionary<string, Func<Task<string>>>
			{
				["Claude"]  = () => m_ClaudeService.GetRawUsageJsonAsync( s.SessionKey ),
				["Copilot"] = () => m_CopilotService.GetRawUsageJsonAsync( s.GitHubToken ),
				["Gemini"]  = () => m_GeminiService.GetRawJsonAsync( s.GeminiClientId, s.GeminiClientSecret, s.GeminiCredentialsPath ),
				["OpenAI"]  = () => m_OpenAIService.GetRawUsageJsonAsync( s.OpenAIToken ),
			};
			var initial = m_ViewModel.Provider switch
			{
				Models.UsageProvider.GitHubCopilot => "Copilot",
				Models.UsageProvider.Gemini        => "Gemini",
				Models.UsageProvider.OpenAI        => "OpenAI",
				_                                  => "Claude",
			};
			System.Windows.Application.Current.Dispatcher.Invoke( () => new RawJsonWindow( providers, initial ).Show() );
		} );
		contextMenu.Items.Add( "About", null, ( _, _ ) =>
			System.Windows.Application.Current.Dispatcher.Invoke( () => new AboutWindow().ShowDialog() ) );
		contextMenu.Items.Add( new ToolStripSeparator() );
		contextMenu.Items.Add( "Exit", null, ( _, _ ) => System.Windows.Application.Current.Shutdown() );

		m_NotifyIcon = new NotifyIcon
		{
			Icon = TrayIconHelper.CreateUsageIcon( null ),
			Text = "Claude AI Usage",
			Visible = true,
			ContextMenuStrip = contextMenu,
		};

		m_NotifyIcon.MouseClick += NotifyIcon_MouseClick;
	}


	private void NotifyIcon_MouseClick( object? sender, MouseEventArgs e )
	{
		if ( e.Button != MouseButtons.Left )
		{
			return;
		}

		System.Windows.Application.Current.Dispatcher.InvokeAsync( () =>
		{
			// Toggle: close if already open, open if closed
			if ( m_PopupWindow != null )
			{
				m_PopupWindow.Close();
				return;
			}

			ShowPopup();
			if ( !m_IsDemoMode )
			{
				_ = RefreshAsync();
			}
		} );
	}


	private void ShowPopup()
	{
		try
		{
			m_PopupWindow?.Close();
		}
		catch { /* already closing — ignore */ }

		m_PopupWindow = new UsagePopupWindow( m_ViewModel, m_IsDemoMode ? null : ( () => RefreshAsync() ), m_IsDemoMode ? null : SwitchProvider );
		m_PopupWindow.Closed += ( _, _ ) => m_PopupWindow = null;
		m_PopupWindow.Show();
		m_PopupWindow.Activate();
	}


	// Refresh all due providers (timer-driven).
	private async Task RefreshDueAsync()
	{
		var settings = SettingsService.Load();
		var now      = DateTime.UtcNow;
		var due = settings.SelectedProviders
			.Where( p => !m_LastRefreshed.TryGetValue( p, out var last )
			             || ( now - last ).TotalMinutes >= GetIntervalMinutes( settings, p ) )
			.ToList();
		if ( due.Count > 0 )
			await RefreshAsync( due ).ConfigureAwait( false );
	}

	private static int GetIntervalMinutes( AppSettings s, UsageProvider p ) => p switch
	{
		UsageProvider.GitHubCopilot => s.CopilotRefreshIntervalMinutes,
		UsageProvider.OpenAI        => s.OpenAIRefreshIntervalMinutes,
		UsageProvider.Gemini        => s.GeminiRefreshIntervalMinutes,
		_                           => s.ClaudeRefreshIntervalMinutes,
	};

	public async Task RefreshAsync( List<UsageProvider>? providers = null )
	{
		if ( m_IsRefreshing ) return;

		m_IsRefreshing = true;
		m_ViewModel.IsLoading = true;

		var settings       = SettingsService.Load();
		var toRefresh      = providers ?? [.. settings.SelectedProviders];
		var activeProvider = m_ViewModel.Provider;

		if ( !settings.SelectedProviders.Contains( activeProvider ) )
		{
			activeProvider       = settings.SelectedProviders.FirstOrDefault();
			m_ViewModel.Provider = activeProvider;
		}

		try
		{
			foreach ( var provider in toRefresh )
			{
				if ( !settings.SelectedProviders.Contains( provider ) ) continue;
				try
				{
					if ( provider == Models.UsageProvider.Gemini )
					{
						var buckets = await m_GeminiService.GetQuotaAsync(
							settings.GeminiClientId, settings.GeminiClientSecret, settings.GeminiCredentialsPath )
							.ConfigureAwait( true );
						if ( activeProvider == provider )
						{
							m_ViewModel.GeminiDisplayMode = settings.GeminiDisplayMode;
							m_ViewModel.UpdateFromGeminiData( buckets );
						}
					}
					else if ( provider == Models.UsageProvider.OpenAI )
					{
						CopilotUsageData data;
						try
						{
							data = await m_OpenAIService.GetUsageAsync( settings.OpenAIToken ).ConfigureAwait( true );
						}
						catch ( UnauthorizedAccessException )
						{
							if ( string.IsNullOrEmpty( settings.OpenAIRefreshToken ) ) throw;
							var tokens = await OpenAIAuthService.RefreshTokenAsync( settings.OpenAIRefreshToken ).ConfigureAwait( true );
							settings.OpenAIToken        = tokens.AccessToken;
							settings.OpenAIRefreshToken = tokens.RefreshToken;
							SettingsService.Save( settings );
							data = await m_OpenAIService.GetUsageAsync( settings.OpenAIToken ).ConfigureAwait( true );
						}
						if ( activeProvider == provider )
							m_ViewModel.UpdateFromData( data, provider );
					}
					else
					{
						var data = provider == Models.UsageProvider.GitHubCopilot
							? await m_CopilotService.GetUsageAsync( settings.GitHubToken ).ConfigureAwait( true )
							: await m_ClaudeService.GetUsageAsync( settings.SessionKey ).ConfigureAwait( true );
						if ( activeProvider == provider )
							m_ViewModel.UpdateFromData( data, provider );
					}

					m_LastRefreshed[provider] = DateTime.UtcNow;
				}
				catch ( Exception ex )
				{
					if ( activeProvider == provider )
						m_ViewModel.ErrorMessage = $"{provider}: {ex.Message}";
				}
			}

			UpdateTrayIcon();
		}
		finally
		{
			m_ViewModel.IsLoading = false;
			m_IsRefreshing = false;
		}
	}


	private void UpdateTrayIcon()
	{
		if ( m_NotifyIcon == null )
		{
			return;
		}

		var percent = m_ViewModel.UsagePercent;
		var oldIcon = m_NotifyIcon.Icon;
		m_NotifyIcon.Icon = TrayIconHelper.CreateUsageIcon( percent );

		var settings = SettingsService.Load();
		var provider = m_ViewModel.Provider;
		var prefix = m_IsDemoMode ? "[DEMO] " : string.Empty;

		// Rebuild context menu to show only selected providers
		UpdateContextMenu( settings );

		string trayText;
		if ( provider == Models.UsageProvider.Gemini )
		{
			var buckets = m_ViewModel.GeminiBuckets;
			trayText = buckets.Count > 0
				? $"{prefix}Gemini: " + string.Join( ", ", buckets.Select( b => $"{ShortGeminiModelName( b.ModelId )} {b.RemainingPercent:0.#}%↑" ) )
				: $"{prefix}Gemini: {percent:0}% max used";
			if ( trayText.Length > 127 ) trayText = trayText[..127];
		}
		else
		{
			var label = provider switch
			{
				Models.UsageProvider.GitHubCopilot => "Copilot",
				Models.UsageProvider.OpenAI        => "OpenAI",
				_                                  => "Claude",
			};
			trayText = ( provider == Models.UsageProvider.GitHubCopilot || provider == Models.UsageProvider.OpenAI )
				&& m_ViewModel.SessionUsed.HasValue
				&& m_ViewModel.SessionLimit.HasValue
				? string.IsNullOrEmpty( m_ViewModel.CopilotCreditsSummary )
					? $"{prefix}{label}: {m_ViewModel.SessionUsed}/{m_ViewModel.SessionLimit} ({percent:0}%)"
					: $"{prefix}{label}: {m_ViewModel.SessionUsed}/{m_ViewModel.SessionLimit}, {m_ViewModel.CopilotCreditsSummary}"
				: $"{prefix}{label}: {percent:0}% used";
		}

		m_NotifyIcon.Text = trayText;
		oldIcon?.Dispose();
	}

	private void UpdateContextMenu( AppSettings settings )
	{
		var menu = m_NotifyIcon!.ContextMenuStrip!;
		
		// Remove old provider items (they are at the top)
		while ( menu.Items.Count > 0 && menu.Items[0].Tag is UsageProvider )
		{
			menu.Items.RemoveAt( 0 );
		}

		// Add selected providers
		int insertIdx = 0;
		foreach ( var provider in settings.SelectedProviders )
		{
			var name = provider switch
			{
				UsageProvider.GitHubCopilot => "GitHub Copilot",
				UsageProvider.OpenAI        => "OpenAI Codex",
				UsageProvider.Gemini        => "Gemini",
				_                           => "Claude AI",
			};

			var item = new ToolStripMenuItem( name, null, ( _, _ ) => SwitchProvider( provider ) )
			{
				Tag     = provider,
				Checked = m_ViewModel.Provider == provider
			};
			menu.Items.Insert( insertIdx++, item );
		}

		// Ensure there's a separator after providers if we added any
		if ( insertIdx > 0 && menu.Items.Count > insertIdx && menu.Items[insertIdx] is not ToolStripSeparator )
		{
			menu.Items.Insert( insertIdx, new ToolStripSeparator() );
		}
	}


	private void UpdateTrayIconError()
	{
		if ( m_NotifyIcon == null )
		{
			return;
		}

		var oldIcon = m_NotifyIcon.Icon;
		m_NotifyIcon.Icon = TrayIconHelper.CreateUsageIcon( null );
		var prefix = m_IsDemoMode ? "[DEMO] " : string.Empty;
		var label  = m_ViewModel.Provider switch
		{
			Models.UsageProvider.GitHubCopilot => "Copilot",
			Models.UsageProvider.Gemini        => "Gemini",
			Models.UsageProvider.OpenAI        => "OpenAI",
			_                                  => "Claude",
		};
		m_NotifyIcon.Text = $"{prefix}{label} — error fetching data";
		oldIcon?.Dispose();
	}


	public void RestartTimer()
	{
		m_RefreshTimer?.Dispose();
		// Tick every 60 s; RefreshDueAsync checks per-provider elapsed time
		m_RefreshTimer = new ThreadingTimer(
			async _ => await System.Windows.Application.Current.Dispatcher.InvokeAsync( RefreshDueAsync ),
			null, 60_000, 60_000 );
	}


	// (sessionPercent, weeklyPercent, errorMessage) per stage
	private static readonly (double Session, double Weekly, string? Error)[] s_DemoStages =
	[
		(  5,   4, null  ),  // attention
		( 35,  28, null  ),  // strolling
		( 70,  56, null  ),  // meow
		( 85,  68, null  ),  // tired
		(100,  80, null  ),  // sleeping
		( 45,  36, "API error: rate limit exceeded (demo)" ),  // error
	];

	private const int DemoStageDurationMs = 30_000;

	private void StartDemoTimer()
	{
		ApplyDemoStep();

		m_RefreshTimer = new ThreadingTimer(
			_ => System.Windows.Application.Current.Dispatcher.Invoke( AdvanceDemoStep ),
			null, DemoStageDurationMs, DemoStageDurationMs );
	}

	private void AdvanceDemoStep()
	{
		m_DemoStageIndex = ( m_DemoStageIndex + 1 ) % s_DemoStages.Length;
		ApplyDemoStep();
	}

	private void ApplyDemoStep()
	{
		var (sessionPercent, weeklyPercent, errorMessage) = s_DemoStages[m_DemoStageIndex];

		const int SessionCap = 50;
		const int WeeklyCap  = 200;

		m_ViewModel.UsagePercent  = sessionPercent;
		m_ViewModel.SessionUsed   = (int)Math.Round( sessionPercent / 100.0 * SessionCap );
		m_ViewModel.SessionLimit  = SessionCap;
		m_ViewModel.SessionResetsAt = DateTimeOffset.Now.AddHours( 4 );
		m_ViewModel.WeeklyPercent = weeklyPercent;
		m_ViewModel.WeeklyUsed    = (int)Math.Round( weeklyPercent / 100.0 * WeeklyCap );
		m_ViewModel.WeeklyLimit   = WeeklyCap;
		m_ViewModel.WeeklyResetsAt = GetNextSunday();
		m_ViewModel.LastUpdated = DateTime.Now;
		m_ViewModel.ErrorMessage = errorMessage;
		m_ViewModel.IsDemoMode = true;

		UpdateTrayIcon();
	}

	private static string ShortGeminiModelName( string modelId ) => modelId switch
	{
		"gemini-2.5-pro"        => "Pro",
		"gemini-2.5-flash"      => "Flash",
		"gemini-2.5-flash-lite" => "Lite",
		_                       => modelId,
	};

	private static DateTimeOffset GetNextSunday()
	{
		var now = DateTime.Now;
		int daysUntilSunday = ( (int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7 ) % 7;
		if ( daysUntilSunday == 0 ) daysUntilSunday = 7;
		return new DateTimeOffset( now.Date.AddDays( daysUntilSunday ).AddHours( 15 ).AddMinutes( 59 ) );
	}


	internal void SwitchProvider( Models.UsageProvider provider )
	{
		m_ViewModel.Provider = provider;
		UpdateTrayIcon();
		_ = RefreshAsync( [provider] ); // force refresh only this provider
	}

	private void OpenSettings()
	{
		System.Windows.Application.Current.Dispatcher.Invoke( () =>
		{
			var settingsWindow = new SettingsWindow();
			if ( settingsWindow.ShowDialog() == true )
			{
				RestartTimer();
				StartupHelper.ApplyStartupSetting( SettingsService.Load().StartWithWindows );
				_ = RefreshAsync();
			}
		} );
	}


	public void Dispose()
	{
		m_RefreshTimer?.Dispose();
		m_NotifyIcon?.Dispose();
	}
}
