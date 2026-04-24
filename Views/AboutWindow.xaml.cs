using CopilotUsage.Helpers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;

namespace CopilotUsage.Views;

public partial class AboutWindow : Window
{
	[DllImport( "dwmapi.dll" )]
	private static extern int DwmSetWindowAttribute( IntPtr hwnd, int attr, ref int attrValue, int attrSize );

	private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	public AboutWindow()
	{
		InitializeComponent();
		Loaded += ( _, _ ) =>
		{
			var hwnd = new WindowInteropHelper( this ).Handle;
			int dark = 1;
			DwmSetWindowAttribute( hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof( int ) );
		};
		var icon = TrayIconHelper.GetWpfImageSource();
		if ( icon != null )
		{
			Icon = icon;
			AppIcon.Source = icon;
		}
	}

	private void OkButton_Click( object sender, RoutedEventArgs e )
	{
		Close();
	}

	private void Hyperlink_RequestNavigate( object sender, RequestNavigateEventArgs e )
	{
		Process.Start( new ProcessStartInfo( e.Uri.AbsoluteUri ) { UseShellExecute = true } );
		e.Handled = true;
	}
}
