using System.Diagnostics;
using System.Linq;
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

	private readonly ClaudeUsageService   m_ClaudeService  = new();
	private readonly GitHubCopilotService m_CopilotService = new();
	private readonly GeminiUsageService   m_GeminiService  = new();
	private readonly OpenAIUsageService   m_OpenAIService  = new();
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

		// Claude key
		SessionKeyBox.Text = settings.SessionKey;
		var hasClaudeKey = !string.IsNullOrWhiteSpace( settings.SessionKey );
		if ( hasClaudeKey )
			SetClaudeStatus( "✓ Session key is saved. Click Test to verify it still works.", WpfBrushes.Green );
		ClaudeEnableCheck.IsEnabled = hasClaudeKey;
		ClaudeEnableCheck.IsChecked = hasClaudeKey && settings.SelectedProviders.Contains( UsageProvider.Claude );

		// GitHub token
		var hasCopilot = !string.IsNullOrWhiteSpace( settings.GitHubToken );
		if ( hasCopilot ) ShowCopilotConnected();
		CopilotEnableCheck.IsEnabled = hasCopilot;
		CopilotEnableCheck.IsChecked = hasCopilot && settings.SelectedProviders.Contains( UsageProvider.GitHubCopilot );

		// OpenAI token
		var hasOpenAI = !string.IsNullOrWhiteSpace( settings.OpenAIToken );
		if ( hasOpenAI ) ShowOpenAIConnected();
		OpenAIEnableCheck.IsEnabled = hasOpenAI;
		OpenAIEnableCheck.IsChecked = hasOpenAI && settings.SelectedProviders.Contains( UsageProvider.OpenAI );

		// Gemini credentials
		GeminiClientIdBox.Text         = settings.GeminiClientId;
		GeminiClientSecretBox.Password = settings.GeminiClientSecret;
		GeminiCredPathBox.Text         = string.IsNullOrEmpty( settings.GeminiCredentialsPath )
			? @"%USERPROFILE%\.gemini\oauth_creds.json"
			: settings.GeminiCredentialsPath;
		var hasGemini = !string.IsNullOrWhiteSpace( settings.GeminiClientId )
		                && !string.IsNullOrWhiteSpace( settings.GeminiClientSecret );
		GeminiEnableCheck.IsEnabled = hasGemini;
		GeminiEnableCheck.IsChecked = hasGemini && settings.SelectedProviders.Contains( UsageProvider.Gemini );

		// Gemini display mode
		var modeTag = settings.GeminiDisplayMode.ToString();
		foreach ( ComboBoxItem item in GeminiDisplayModeCombo.Items )
			if ( item.Tag is string t && t == modeTag ) { GeminiDisplayModeCombo.SelectedItem = item; break; }
		if ( GeminiDisplayModeCombo.SelectedItem == null ) GeminiDisplayModeCombo.SelectedIndex = 0;

		// Refresh intervals
		SelectInterval( ClaudeIntervalCombo,  settings.ClaudeRefreshIntervalMinutes );
		SelectInterval( CopilotIntervalCombo, settings.CopilotRefreshIntervalMinutes );
		SelectInterval( OpenAIIntervalCombo,  settings.OpenAIRefreshIntervalMinutes );
		SelectInterval( GeminiIntervalCombo,  settings.GeminiRefreshIntervalMinutes );

		StartupCheckBox.IsChecked = settings.StartWithWindows;

		UpdateTabHeaders();
	}


	private static void SelectInterval( System.Windows.Controls.ComboBox combo, int minutes )
	{
		foreach ( ComboBoxItem item in combo.Items )
		{
			if ( item.Tag is string tag && int.TryParse( tag, out var val ) && val == minutes )
			{
				combo.SelectedItem = item;
				return;
			}
		}
		combo.SelectedIndex = 1; // default 5 min
	}

	private static int ReadInterval( System.Windows.Controls.ComboBox combo, int fallback ) =>
		combo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse( tag, out var v ) ? v : fallback;


	// ── Tab header badges ─────────────────────────────────────────────────────

	private void UpdateTabHeaders()
	{
		var settings = SettingsService.Load();
		ClaudeTab.Header  = !string.IsNullOrWhiteSpace( settings.SessionKey )  ? "Claude AI ✓"      : "Claude AI";
		CopilotTab.Header = !string.IsNullOrWhiteSpace( settings.GitHubToken ) ? "GitHub Copilot ✓" : "GitHub Copilot";
		OpenAITab.Header  = !string.IsNullOrWhiteSpace( settings.OpenAIToken ) ? "OpenAI Codex ✓"   : "OpenAI Codex";
		GeminiTab.Header  = !string.IsNullOrWhiteSpace( settings.GeminiClientId )
		                    && !string.IsNullOrWhiteSpace( settings.GeminiClientSecret ) ? "Gemini ✓" : "Gemini";
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

			CopilotVerificationUrl.Text          = info.VerificationUri;
			CopilotUserCode.Text                 = info.UserCode;
			CopilotDeviceCodePanel.Visibility    = Visibility.Visible;
			SetCopilotStatus( "Waiting for you to authorise in the browser…", WpfBrushes.Gray );

			Process.Start( new ProcessStartInfo( info.VerificationUri ) { UseShellExecute = true } );

			m_AuthCts = new CancellationTokenSource();
			var token = await GitHubAuthService.PollForTokenAsync( info.DeviceCode, info.Interval, m_AuthCts.Token )
				.ConfigureAwait( true );

			var settings = SettingsService.Load();
			settings.GitHubToken = token;
			SettingsService.Save( settings );

			CopilotDeviceCodePanel.Visibility = Visibility.Collapsed;
			ShowCopilotConnected();
			CopilotEnableCheck.IsEnabled = true;
			CopilotEnableCheck.IsChecked = true;
			SetCopilotStatus( "✓ GitHub account connected!", WpfBrushes.Green );
			UpdateTabHeaders();
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

		CopilotConnectedPanel.Visibility = Visibility.Collapsed;
		CopilotAuthFlowPanel.Visibility  = Visibility.Visible;
		CopilotEnableCheck.IsChecked     = false;
		CopilotEnableCheck.IsEnabled     = false;
		SetCopilotStatus( "Disconnected.", WpfBrushes.Gray );
		UpdateTabHeaders();
	}

	private void ShowCopilotConnected()
	{
		CopilotConnectedText.Text        = "✓ GitHub account connected";
		CopilotConnectedPanel.Visibility = Visibility.Visible;
		CopilotAuthFlowPanel.Visibility  = Visibility.Collapsed;
	}

	private void SetCopilotStatus( string message, WpfBrush color )
	{
		CopilotStatusText.Text       = message;
		CopilotStatusText.Foreground = color;
		CopilotStatusText.Visibility = Visibility.Visible;
	}


	// ── OpenAI ────────────────────────────────────────────────────────────────

	private async void OpenAIConnect_Click( object sender, RoutedEventArgs e )
	{
		OpenAIConnectButton.IsEnabled = false;
		SetOpenAIStatus( "Requesting device code…", WpfBrushes.Gray );

		try
		{
			var info = await OpenAIAuthService.RequestDeviceCodeAsync().ConfigureAwait( true );

			OpenAIVerificationUrl.Text        = info.VerificationUri;
			OpenAIUserCode.Text               = info.UserCode;
			OpenAIDeviceCodePanel.Visibility  = Visibility.Visible;
			SetOpenAIStatus( "Waiting for you to authorise in the browser…", WpfBrushes.Gray );

			Process.Start( new ProcessStartInfo( info.VerificationUri ) { UseShellExecute = true } );

			m_AuthCts = new CancellationTokenSource();
			var tokens = await OpenAIAuthService.PollForTokenAsync( info.DeviceCode, info.UserCode, info.Interval, m_AuthCts.Token )
				.ConfigureAwait( true );

			var settings = SettingsService.Load();
			settings.OpenAIToken        = tokens.AccessToken;
			settings.OpenAIRefreshToken = tokens.RefreshToken;
			SettingsService.Save( settings );

			OpenAIDeviceCodePanel.Visibility = Visibility.Collapsed;
			ShowOpenAIConnected();
			OpenAIEnableCheck.IsEnabled = true;
			OpenAIEnableCheck.IsChecked = true;
			SetOpenAIStatus( "✓ OpenAI account connected!", WpfBrushes.Green );
			UpdateTabHeaders();
		}
		catch ( OperationCanceledException )
		{
			SetOpenAIStatus( "Cancelled.", WpfBrushes.Gray );
			OpenAIDeviceCodePanel.Visibility = Visibility.Collapsed;
		}
		catch ( Exception ex )
		{
			SetOpenAIStatus( ex.Message, WpfBrushes.Red );
			OpenAIDeviceCodePanel.Visibility = Visibility.Collapsed;
		}
		finally
		{
			OpenAIConnectButton.IsEnabled = true;
			m_AuthCts = null;
		}
	}

	private void OpenAICancel_Click( object sender, RoutedEventArgs e )
	{
		m_AuthCts?.Cancel();
	}

	private void OpenAIDisconnect_Click( object sender, RoutedEventArgs e )
	{
		var settings = SettingsService.Load();
		settings.OpenAIToken        = string.Empty;
		settings.OpenAIRefreshToken = string.Empty;
		SettingsService.Save( settings );

		OpenAIConnectedPanel.Visibility = Visibility.Collapsed;
		OpenAIAuthFlowPanel.Visibility  = Visibility.Visible;
		OpenAIEnableCheck.IsChecked     = false;
		OpenAIEnableCheck.IsEnabled     = false;
		SetOpenAIStatus( "Disconnected.", WpfBrushes.Gray );
		UpdateTabHeaders();
	}

	private void ShowOpenAIConnected()
	{
		OpenAIConnectedText.Text        = "✓ OpenAI account connected";
		OpenAIConnectedPanel.Visibility = Visibility.Visible;
		OpenAIAuthFlowPanel.Visibility  = Visibility.Collapsed;
	}

	private void SetOpenAIStatus( string message, WpfBrush color )
	{
		OpenAIStatusText.Text       = message;
		OpenAIStatusText.Foreground = color;
		OpenAIStatusText.Visibility = Visibility.Visible;
	}


	// ── Gemini ────────────────────────────────────────────────────────────────

	private void GeminiBrowse_Click( object sender, RoutedEventArgs e )
	{
		var dlg = new Microsoft.Win32.OpenFileDialog
		{
			Title      = "Select Gemini Credentials File",
			Filter     = "JSON files (*.json)|*.json|All files (*.*)|*.*",
			DefaultExt = ".json",
		};

		var expandedPath = Environment.ExpandEnvironmentVariables( GeminiCredPathBox.Text.Trim() );
		var dir = System.IO.Path.GetDirectoryName( expandedPath );
		if ( !string.IsNullOrEmpty( dir ) && System.IO.Directory.Exists( dir ) )
			dlg.InitialDirectory = dir;

		if ( dlg.ShowDialog( this ) == true )
			GeminiCredPathBox.Text = dlg.FileName;
	}

	private async void GeminiTest_Click( object sender, RoutedEventArgs e )
	{
		var clientId     = GeminiClientIdBox.Text.Trim();
		var clientSecret = GeminiClientSecretBox.Password;
		var credPath     = GeminiCredPathBox.Text.Trim();

		if ( string.IsNullOrWhiteSpace( clientId ) || string.IsNullOrWhiteSpace( clientSecret ) || string.IsNullOrWhiteSpace( credPath ) )
		{
			SetGeminiStatus( "Please fill in all fields first.", WpfBrushes.OrangeRed );
			return;
		}

		GeminiTestButton.IsEnabled = false;
		SetGeminiStatus( "Testing connection…", WpfBrushes.Gray );

		try
		{
			var buckets = await m_GeminiService.GetQuotaAsync( clientId, clientSecret, credPath ).ConfigureAwait( true );
			var summary = string.Join( ", ", buckets.Select( b => $"{b.ModelId}: {b.RemainingPercent:0.#}% remaining" ) );
			SetGeminiStatus( $"✓ Connected! {summary}", WpfBrushes.Green );
		}
		catch ( Exception ex )
		{
			SetGeminiStatus( ex.Message, WpfBrushes.Red );
		}
		finally
		{
			GeminiTestButton.IsEnabled = true;
		}
	}

	private void SetGeminiStatus( string message, WpfBrush color )
	{
		GeminiStatusText.Text       = message;
		GeminiStatusText.Foreground = color;
		GeminiStatusText.Visibility = Visibility.Visible;
	}


	// ── Save / Cancel ─────────────────────────────────────────────────────────

	private void SaveButton_Click( object sender, RoutedEventArgs e )
	{
		var settings = SettingsService.Load();

		settings.SessionKey            = SessionKeyBox.Text.Trim();
		settings.GeminiClientId        = GeminiClientIdBox.Text.Trim();
		settings.GeminiClientSecret    = GeminiClientSecretBox.Password;
		settings.GeminiCredentialsPath = GeminiCredPathBox.Text.Trim();

		// SelectedProviders = has credentials AND user enabled
		var hasClaudeKey = !string.IsNullOrWhiteSpace( settings.SessionKey );
		var hasCopilot   = !string.IsNullOrWhiteSpace( settings.GitHubToken );
		var hasOpenAI    = !string.IsNullOrWhiteSpace( settings.OpenAIToken );
		var hasGemini    = !string.IsNullOrWhiteSpace( settings.GeminiClientId )
		                   && !string.IsNullOrWhiteSpace( settings.GeminiClientSecret );

		if ( !hasClaudeKey && !hasCopilot && !hasOpenAI && !hasGemini )
		{
			System.Windows.MessageBox.Show(
				"Please configure at least one provider before saving.",
				"No Provider Configured",
				MessageBoxButton.OK,
				MessageBoxImage.Warning );
			return;
		}

		// Update enable checkbox availability now that credentials may have changed
		ClaudeEnableCheck.IsEnabled  = hasClaudeKey;
		CopilotEnableCheck.IsEnabled = hasCopilot;
		OpenAIEnableCheck.IsEnabled  = hasOpenAI;
		GeminiEnableCheck.IsEnabled  = hasGemini;

		settings.SelectedProviders.Clear();
		if ( hasClaudeKey && ClaudeEnableCheck.IsChecked  == true ) settings.SelectedProviders.Add( UsageProvider.Claude );
		if ( hasCopilot   && CopilotEnableCheck.IsChecked == true ) settings.SelectedProviders.Add( UsageProvider.GitHubCopilot );
		if ( hasOpenAI    && OpenAIEnableCheck.IsChecked  == true ) settings.SelectedProviders.Add( UsageProvider.OpenAI );
		if ( hasGemini    && GeminiEnableCheck.IsChecked  == true ) settings.SelectedProviders.Add( UsageProvider.Gemini );

		settings.ClaudeRefreshIntervalMinutes  = ReadInterval( ClaudeIntervalCombo,  5  );
		settings.CopilotRefreshIntervalMinutes = ReadInterval( CopilotIntervalCombo, 15 );
		settings.OpenAIRefreshIntervalMinutes  = ReadInterval( OpenAIIntervalCombo,  15 );
		settings.GeminiRefreshIntervalMinutes  = ReadInterval( GeminiIntervalCombo,  15 );

		if ( GeminiDisplayModeCombo.SelectedItem is ComboBoxItem { Tag: string modeStr }
		     && Enum.TryParse<GeminiDisplayMode>( modeStr, out var mode ) )
			settings.GeminiDisplayMode = mode;

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
