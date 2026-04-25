using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using CopilotUsage.Helpers;

namespace CopilotUsage.Views;

public partial class RawJsonWindow : Window
{
	private readonly Dictionary<string, Func<Task<string>>> m_Providers;
	private string m_ActiveProvider;

	internal RawJsonWindow( Dictionary<string, Func<Task<string>>> providers, string initialProvider )
	{
		InitializeComponent();
		m_Providers       = providers;
		m_ActiveProvider  = initialProvider;
		SourceInitialized += ( _, _ ) => WindowHelper.ApplyDarkTitleBar( this );
		Loaded += ( _, _ ) =>
		{
			SetActiveButton( m_ActiveProvider );
			_ = LoadAsync( m_ActiveProvider );
		};
	}

	private void SetActiveButton( string key )
	{
		ClaudeBtn.Style  = key == "Claude"  ? (Style)Resources["ProviderActiveStyle"] : (Style)Resources["ProviderInactiveStyle"];
		CopilotBtn.Style = key == "Copilot" ? (Style)Resources["ProviderActiveStyle"] : (Style)Resources["ProviderInactiveStyle"];
		GeminiBtn.Style  = key == "Gemini"  ? (Style)Resources["ProviderActiveStyle"] : (Style)Resources["ProviderInactiveStyle"];
	}

	private async void ProviderBtn_Click( object sender, RoutedEventArgs e )
	{
		if ( sender is not System.Windows.Controls.Button btn || btn.Tag is not string key ) return;
		m_ActiveProvider = key;
		SetActiveButton( key );
		StatusBanner.Visibility = Visibility.Collapsed;
		JsonTextBox.Text = "Fetching…";
		await LoadAsync( key );
	}

	private async Task LoadAsync( string providerKey )
	{
		if ( !m_Providers.TryGetValue( providerKey, out var getRawJson ) )
		{
			JsonTextBox.Text        = string.Empty;
			StatusText.Text         = $"No credentials configured for {providerKey}. Open Settings to set it up.";
			StatusBanner.Visibility = Visibility.Visible;
			return;
		}

		try
		{
			var json = await getRawJson().ConfigureAwait( true );
			JsonTextBox.Text = json;
		}
		catch ( Exception ex )
		{
			JsonTextBox.Text        = string.Empty;
			StatusText.Text         = ex.Message;
			StatusBanner.Visibility = Visibility.Visible;
		}
	}

	private void CopyButton_Click( object sender, RoutedEventArgs e )
	{
		if ( !string.IsNullOrEmpty( JsonTextBox.Text ) )
			System.Windows.Clipboard.SetText( JsonTextBox.Text );
	}
}
