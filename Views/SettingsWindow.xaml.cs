using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CopilotUsage.Models;
using CopilotUsage.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace CopilotUsage.Views;

public partial class SettingsWindow : Window
{
	[DllImport( "dwmapi.dll" )]
	private static extern int DwmSetWindowAttribute( IntPtr hwnd, int attr, ref int attrValue, int attrSize );

	private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	private readonly ClaudeUsageService   m_ClaudeService   = new();
	private readonly GitHubCopilotService m_CopilotService  = new();
	private CancellationTokenSource?      m_AuthCts;


	internal SettingsWindow()
	{
		InitializeComponent();
		Loaded += ( _, _ ) => EnableDarkTitleBar();
		LoadSettings();
	}

	private void EnableDarkTitleBar()
	{
		var hwnd = new WindowInteropHelper( this ).Handle;
		int darkMode = 1;
		DwmSetWindowAttribute( hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof( int ) );
	}


	private void LoadSettings()
	{
		var settings = SettingsService.Load();

		// Provider radio
		if ( settings.Provider == UsageProvider.GitHubCopilot )
		{
			CopilotRadio.IsChecked = true;
		}
		else
		{
			ClaudeRadio.IsChecked = true;
		}

		// Claude key
		SessionKeyBox.Text = settings.SessionKey;
		if ( !string.IsNullOrWhiteSpace( settings.SessionKey ) )
			SetClaudeStatus( "✓ Session key is saved. Click Test to verify it still works.", WpfBrushes.Green );

		// GitHub token
		if ( !string.IsNullOrWhiteSpace( settings.GitHubToken ) )
			ShowCopilotConnected();

		// Refresh interval
		foreach ( ComboBoxItem item in IntervalCombo.Items )
		{
			if ( item.Tag is string tag && int.TryParse( tag, out var val ) && val == settings.RefreshIntervalMinutes )
			{
				IntervalCombo.SelectedItem = item;
				break;
			}
		}
		if ( IntervalCombo.SelectedItem == null )
			IntervalCombo.SelectedIndex = 1;

		StartupCheckBox.IsChecked = settings.StartWithWindows;

		UpdateProviderPanels();
	}


	private void Provider_Checked( object sender, RoutedEventArgs e ) => UpdateProviderPanels();

	private void UpdateProviderPanels()
	{
		bool isCopilot = CopilotRadio.IsChecked == true;
		ClaudePanel.Visibility  = isCopilot ? Visibility.Collapsed : Visibility.Visible;
		CopilotPanel.Visibility = isCopilot ? Visibility.Visible   : Visibility.Collapsed;
	}


	// ── Claude ────────────────────────────────────────────────────────────────

	private void SessionKeyBox_TextChanged( object sender, System.Windows.Controls.TextChangedEventArgs e )
	{
		var len = SessionKeyBox.Text.Trim().Length;
		if ( len == 0 )
		{
			KeyLengthHint.Text       = string.Empty;
			KeyLengthHint.Foreground = WpfBrushes.Gray;
		}
		else if ( len < 80 )
		{
			KeyLengthHint.Text       = $"{len} chars — looks too short, may be truncated";
			KeyLengthHint.Foreground = WpfBrushes.OrangeRed;
		}
		else
		{
			KeyLengthHint.Text       = $"{len} chars ✓";
			KeyLengthHint.Foreground = WpfBrushes.Green;
		}
	}

	private async void TestButton_Click( object sender, RoutedEventArgs e )
	{
		var key = SessionKeyBox.Text.Trim();
		if ( string.IsNullOrWhiteSpace( key ) )
		{
			SetClaudeStatus( "Please paste your session key first.", WpfBrushes.OrangeRed );
			return;
		}

		TestButton.IsEnabled = false;
		SetClaudeStatus( "Testing connection…", WpfBrushes.Gray );

		try
		{
			await m_ClaudeService.GetUsageAsync( key ).ConfigureAwait( true );
			SetClaudeStatus( "✓ Connected to Claude successfully!", WpfBrushes.Green );
		}
		catch ( Exception ex )
		{
			SetClaudeStatus( ex.Message, WpfBrushes.Red );
		}
		finally
		{
			TestButton.IsEnabled = true;
		}
	}

	private void SetClaudeStatus( string message, WpfBrush color )
	{
		AuthStatusText.Text       = message;
		AuthStatusText.Foreground = color;
		AuthStatusText.Visibility = Visibility.Visible;
	}


	// ── GitHub Copilot ────────────────────────────────────────────────────────

	private async void CopilotConnect_Click( object sender, RoutedEventArgs e )
	{
		CopilotConnectButton.IsEnabled = false;
		SetCopilotStatus( "Requesting device code…", WpfBrushes.Gray );

		try
		{
			var info = await GitHubAuthService.RequestDeviceCodeAsync().ConfigureAwait( true );

			CopilotVerificationUrl.Text   = info.VerificationUri;
			CopilotUserCode.Text          = info.UserCode;
			CopilotDeviceCodePanel.Visibility = Visibility.Visible;
			SetCopilotStatus( "Waiting for you to authorise in the browser…", WpfBrushes.Gray );

			// Open browser
			Process.Start( new ProcessStartInfo( info.VerificationUri ) { UseShellExecute = true } );

			m_AuthCts = new CancellationTokenSource();
			var token = await GitHubAuthService.PollForTokenAsync( info.DeviceCode, info.Interval, m_AuthCts.Token )
				.ConfigureAwait( true );

			// Save token immediately
			var settings = SettingsService.Load();
			settings.GitHubToken = token;
			SettingsService.Save( settings );

			CopilotDeviceCodePanel.Visibility = Visibility.Collapsed;
			ShowCopilotConnected();
			SetCopilotStatus( "✓ GitHub account connected!", WpfBrushes.Green );
		}
		catch ( OperationCanceledException )
		{
			SetCopilotStatus( "Cancelled.", WpfBrushes.Gray );
			CopilotDeviceCodePanel.Visibility = Visibility.Collapsed;
		}
		catch ( Exception ex )
		{
			SetCopilotStatus( ex.Message, WpfBrushes.Red );
			CopilotDeviceCodePanel.Visibility = Visibility.Collapsed;
		}
		finally
		{
			CopilotConnectButton.IsEnabled = true;
			m_AuthCts = null;
		}
	}

	private void CopilotCancel_Click( object sender, RoutedEventArgs e )
	{
		m_AuthCts?.Cancel();
	}

	private void CopilotDisconnect_Click( object sender, RoutedEventArgs e )
	{
		var settings = SettingsService.Load();
		settings.GitHubToken = string.Empty;
		SettingsService.Save( settings );

		CopilotConnectedPanel.Visibility  = Visibility.Collapsed;
		CopilotAuthFlowPanel.Visibility   = Visibility.Visible;
		SetCopilotStatus( "Disconnected.", WpfBrushes.Gray );
	}

	private void ShowCopilotConnected()
	{
		CopilotConnectedText.Text         = "✓ GitHub account connected";
		CopilotConnectedPanel.Visibility  = Visibility.Visible;
		CopilotAuthFlowPanel.Visibility   = Visibility.Collapsed;
	}

	private void SetCopilotStatus( string message, WpfBrush color )
	{
		CopilotStatusText.Text       = message;
		CopilotStatusText.Foreground = color;
		CopilotStatusText.Visibility = Visibility.Visible;
	}


	// ── Save / Cancel ─────────────────────────────────────────────────────────

	private void SaveButton_Click( object sender, RoutedEventArgs e )
	{
		var settings = SettingsService.Load();

		settings.Provider = CopilotRadio.IsChecked == true
			? UsageProvider.GitHubCopilot
			: UsageProvider.Claude;

		settings.SessionKey = SessionKeyBox.Text.Trim();

		if ( IntervalCombo.SelectedItem is ComboBoxItem selected &&
			selected.Tag is string tag &&
			int.TryParse( tag, out var interval ) )
		{
			settings.RefreshIntervalMinutes = interval;
		}

		settings.StartWithWindows = StartupCheckBox.IsChecked == true;

		SettingsService.Save( settings );
		DialogResult = true;
		Close();
	}

	private void CancelButton_Click( object sender, RoutedEventArgs e )
	{
		m_AuthCts?.Cancel();
		DialogResult = false;
		Close();
	}
}
