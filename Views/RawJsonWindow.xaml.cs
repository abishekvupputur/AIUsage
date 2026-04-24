using System.Windows;
using CopilotUsage.Helpers;

namespace CopilotUsage.Views;

public partial class RawJsonWindow : Window
{
	internal RawJsonWindow( Func<Task<string>> getRawJson )
	{
		InitializeComponent();
		SourceInitialized += ( _, _ ) => WindowHelper.ApplyDarkTitleBar( this );
		Loaded += async ( _, _ ) => await LoadAsync( getRawJson );
	}

	private async Task LoadAsync( Func<Task<string>> getRawJson )
	{
		try
		{
			var json = await getRawJson().ConfigureAwait( true );
			JsonTextBox.Text = json;
		}
		catch ( Exception ex )
		{
			JsonTextBox.Text = string.Empty;
			StatusText.Text = ex.Message;
			StatusBanner.Visibility = Visibility.Visible;
		}
	}

	private void CopyButton_Click( object sender, RoutedEventArgs e )
	{
		if ( !string.IsNullOrEmpty( JsonTextBox.Text ) )
			System.Windows.Clipboard.SetText( JsonTextBox.Text );
	}
}
