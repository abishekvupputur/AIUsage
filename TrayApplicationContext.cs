using ThreadingTimer = System.Threading.Timer;

using CopilotUsage.Helpers;
using CopilotUsage.Services;
using CopilotUsage.ViewModels;
using CopilotUsage.Views;

namespace CopilotUsage;

internal sealed class TrayApplicationContext : IDisposable
{
	private readonly ClaudeUsageService    m_ClaudeService   = new();
	private readonly GitHubCopilotService  m_CopilotService  = new();
	private readonly UsageViewModel m_ViewModel;
	private readonly bool m_IsDemoMode;

	private NotifyIcon? m_NotifyIcon;
	private ThreadingTimer? m_RefreshTimer;
	private UsagePopupWindow? m_PopupWindow;
	private bool m_IsRefreshing;

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
			Func<Task<string>> getRaw = s.Provider == Models.UsageProvider.GitHubCopilot
				? () => m_CopilotService.GetRawUsageJsonAsync( s.GitHubToken )
				: () => m_ClaudeService.GetRawUsageJsonAsync( s.SessionKey );
			System.Windows.Application.Current.Dispatcher.Invoke( () => new RawJsonWindow( getRaw ).Show() );
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

		m_PopupWindow = new UsagePopupWindow( m_ViewModel, m_IsDemoMode ? null : RefreshAsync );
		m_PopupWindow.Closed += ( _, _ ) => m_PopupWindow = null;
		m_PopupWindow.Show();
		m_PopupWindow.Activate();
	}


	public async Task RefreshAsync()
	{
		if ( m_IsRefreshing )
		{
			return;
		}

		m_IsRefreshing = true;
		m_ViewModel.IsLoading = true;

		var settings = SettingsService.Load();

		try
		{
			var data = settings.Provider == Models.UsageProvider.GitHubCopilot
			? await m_CopilotService.GetUsageAsync( settings.GitHubToken ).ConfigureAwait( true )
			: await m_ClaudeService.GetUsageAsync( settings.SessionKey ).ConfigureAwait( true );

			m_ViewModel.UpdateFromData( data, settings.Provider );

			if ( data.IsUnavailable )
			{
				UpdateTrayIconError();
			}
			else
			{
				UpdateTrayIcon();
			}
		}
		catch ( Exception ex )
		{
			m_ViewModel.ErrorMessage = ex.Message;
			UpdateTrayIconError();
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

		var provider = SettingsService.Load().Provider;
		var prefix = m_IsDemoMode ? "[DEMO] " : string.Empty;
		var label  = provider == Models.UsageProvider.GitHubCopilot ? "Copilot" : "Claude";
		m_NotifyIcon.Text = provider == Models.UsageProvider.GitHubCopilot
			&& m_ViewModel.SessionUsed.HasValue
			&& m_ViewModel.SessionLimit.HasValue
			? string.IsNullOrEmpty( m_ViewModel.CopilotCreditsSummary )
				? $"{prefix}{label}: {m_ViewModel.SessionUsed}/{m_ViewModel.SessionLimit} ({percent:0}%)"
				: $"{prefix}{label}: {m_ViewModel.SessionUsed}/{m_ViewModel.SessionLimit}, {m_ViewModel.CopilotCreditsSummary}"
			: $"{prefix}{label}: {percent:0}% used";
		oldIcon?.Dispose();
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
		var label  = SettingsService.Load().Provider == Models.UsageProvider.GitHubCopilot ? "Copilot" : "Claude";
		m_NotifyIcon.Text = $"{prefix}{label} — error fetching data";
		oldIcon?.Dispose();
	}


	public void RestartTimer()
	{
		m_RefreshTimer?.Dispose();
		var settings = SettingsService.Load();
		var intervalMs = ( settings.RefreshIntervalMinutes > 0 ? settings.RefreshIntervalMinutes : 15 ) * 60 * 1000;
		m_RefreshTimer = new ThreadingTimer(
			async _ => await System.Windows.Application.Current.Dispatcher.InvokeAsync( RefreshAsync ),
			null, intervalMs, intervalMs );
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

	private static DateTimeOffset GetNextSunday()
	{
		var now = DateTime.Now;
		int daysUntilSunday = ( (int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7 ) % 7;
		if ( daysUntilSunday == 0 ) daysUntilSunday = 7;
		return new DateTimeOffset( now.Date.AddDays( daysUntilSunday ).AddHours( 15 ).AddMinutes( 59 ) );
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
