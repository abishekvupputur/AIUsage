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
		if ( !string.IsNullOrWhiteSpace( settings.SessionKey ) )
			SetClaudeStatus( "✓ Session key is saved. Click Test to verify it still works.", WpfBrushes.Green );

		// GitHub token
		if ( !string.IsNullOrWhiteSpace( settings.GitHubToken ) )
			ShowCopilotConnected();

		// OpenAI token
		if ( !string.IsNullOrWhiteSpace( settings.OpenAIToken ) )
			ShowOpenAIConnected();

		// Gemini credentials
		GeminiClientIdBox.Text         = settings.GeminiClientId;
		GeminiClientSecretBox.Password = settings.GeminiClientSecret;
		GeminiCredPathBox.Text         = string.IsNullOrEmpty( settings.GeminiCredentialsPath )
			? @"%USERPROFILE%\.gemini\oauth_creds.json"
			: settings.GeminiCredentialsPath;

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

		UpdateTabHeaders();
	}


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

		// SelectedProviders auto-computed from what is actually connected
		settings.SelectedProviders.Clear();
		if ( !string.IsNullOrWhiteSpace( settings.SessionKey ) )
			settings.SelectedProviders.Add( UsageProvider.Claude );
		if ( !string.IsNullOrWhiteSpace( settings.GitHubToken ) )
			settings.SelectedProviders.Add( UsageProvider.GitHubCopilot );
		if ( !string.IsNullOrWhiteSpace( settings.OpenAIToken ) )
			settings.SelectedProviders.Add( UsageProvider.OpenAI );
		if ( !string.IsNullOrWhiteSpace( settings.GeminiClientId ) && !string.IsNullOrWhiteSpace( settings.GeminiClientSecret ) )
			settings.SelectedProviders.Add( UsageProvider.Gemini );

		if ( settings.SelectedProviders.Count == 0 )
		{
			System.Windows.MessageBox.Show(
				"Please configure at least one provider before saving.",
				"No Provider Configured",
				MessageBoxButton.OK,
				MessageBoxImage.Warning );
			return;
		}

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
