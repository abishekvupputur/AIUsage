using CopilotUsage.Helpers;
using CopilotUsage.Services;
using CopilotUsage.Views;

namespace CopilotUsage;

public partial class App : System.Windows.Application
{
	private static Mutex? s_SingleInstanceMutex;
	private TrayApplicationContext? m_TrayContext;


	protected override void OnStartup( System.Windows.StartupEventArgs e )
	{
		base.OnStartup( e );

		s_SingleInstanceMutex = new Mutex( true, "AIUsage_SingleInstance", out var isNewInstance );
		if ( !isNewInstance )
		{
			Shutdown();
			return;
		}

		bool isDemoMode = e.Args.Length > 0 && e.Args[0].Equals( "--demo", StringComparison.OrdinalIgnoreCase );

		if ( !isDemoMode )
		{
			var settings = SettingsService.Load();

			bool isConfigured = settings.Provider == Models.UsageProvider.GitHubCopilot
				? !string.IsNullOrWhiteSpace( settings.GitHubToken )
				: !string.IsNullOrWhiteSpace( settings.SessionKey );

			if ( !isConfigured )
			{
				var settingsWindow = new SettingsWindow();
				if ( settingsWindow.ShowDialog() != true )
				{
					Shutdown();
					return;
				}
			}

			StartupHelper.ApplyStartupSetting( SettingsService.Load().StartWithWindows );
		}

		m_TrayContext = new TrayApplicationContext( isDemoMode );
	}


	protected override void OnExit( System.Windows.ExitEventArgs e )
	{
		m_TrayContext?.Dispose();
		s_SingleInstanceMutex?.ReleaseMutex();
		s_SingleInstanceMutex?.Dispose();
		base.OnExit( e );
	}
}
