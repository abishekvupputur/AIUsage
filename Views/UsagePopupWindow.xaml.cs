using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CopilotUsage.Models;
using CopilotUsage.ViewModels;

namespace CopilotUsage.Views;

public partial class UsagePopupWindow : Window
{
	internal UsageViewModel ViewModel { get; }

	// Timer ticks ~30 fps and rotates the spinner arc by 12° per tick (one full rotation per 0.8 s).
	// It is started/stopped by the IsLoading property so it consumes zero CPU when not refreshing.
	private readonly DispatcherTimer m_SpinnerTimer;
	private double m_SpinnerAngle;

	private readonly DispatcherTimer m_CatTimer;
	private readonly Dictionary<string, BitmapImage[]> m_CatFrames;
	private int m_CatFrameIndex;
	private string m_CurrentCatState = string.Empty;

	private Func<Task>? m_RefreshAction;
	private Action<UsageProvider>? m_SwitchProviderAction;

	internal UsagePopupWindow( UsageViewModel viewModel, Func<Task>? refreshAction = null, Action<UsageProvider>? switchProviderAction = null )
	{
		ViewModel = viewModel;
		m_RefreshAction = refreshAction;
		m_SwitchProviderAction = switchProviderAction;
		DataContext = viewModel;
		InitializeComponent();

		m_SpinnerTimer = new DispatcherTimer( DispatcherPriority.Render )
		{
			Interval = TimeSpan.FromMilliseconds( 33 ),
		};
		m_SpinnerTimer.Tick += SpinnerTimer_Tick;

		m_CatFrames = LoadCatFrames();
		m_CatTimer = new DispatcherTimer( DispatcherPriority.Background )
		{
			Interval = TimeSpan.FromMilliseconds( 200 ),
		};
		m_CatTimer.Tick += CatTimer_Tick;
		m_CatTimer.Start();
		AdvanceCatFrame(); // show first frame immediately

		viewModel.PropertyChanged += ViewModel_PropertyChanged;
		Closed += Window_Closed;

		Loaded += ( _, _ ) =>
		{
			UpdateProviderButtons();
			PositionNearTray();
		};
	}


	private void UpdateProviderButtons()
	{
		var active   = (Style)Resources["ProvBtnActiveStyle"];
		var inactive = (Style)Resources["ProvBtnInactiveStyle"];
		ClaudeProviderBtn.Style  = ViewModel.Provider == UsageProvider.Claude        ? active : inactive;
		CopilotProviderBtn.Style = ViewModel.Provider == UsageProvider.GitHubCopilot ? active : inactive;
		GeminiProviderBtn.Style  = ViewModel.Provider == UsageProvider.Gemini        ? active : inactive;
	}

	private void ClaudeProvider_Click( object sender, RoutedEventArgs e )
	{
		m_SwitchProviderAction?.Invoke( UsageProvider.Claude );
	}

	private void CopilotProvider_Click( object sender, RoutedEventArgs e )
	{
		m_SwitchProviderAction?.Invoke( UsageProvider.GitHubCopilot );
	}

	private void GeminiProvider_Click( object sender, RoutedEventArgs e )
	{
		m_SwitchProviderAction?.Invoke( UsageProvider.Gemini );
	}

	private void ViewModel_PropertyChanged( object? sender, PropertyChangedEventArgs e )
	{
		if ( e.PropertyName == nameof( UsageViewModel.Provider ) )
		{
			UpdateProviderButtons();
			return;
		}

		if ( e.PropertyName == nameof( UsageViewModel.CatStateName )
			&& m_WaitingLoopPause
			&& ViewModel.CatStateName != m_CurrentCatState )
		{
			m_WaitingLoopPause = false;
			m_CatTimer.Interval = TimeSpan.FromMilliseconds( 1 );
			return;
		}

		if ( e.PropertyName != nameof( UsageViewModel.IsLoading ) )
		{
			return;
		}

		if ( ViewModel.IsLoading )
		{
			m_SpinnerTimer.Start();
		}
		else
		{
			m_SpinnerTimer.Stop();
		}
	}

	private void SpinnerTimer_Tick( object? sender, EventArgs e )
	{
		m_SpinnerAngle = ( m_SpinnerAngle + 12.0 ) % 360.0;
		SpinnerRotation.Angle = m_SpinnerAngle;
	}

	private void Window_Closed( object? sender, EventArgs e )
	{
		m_SpinnerTimer.Stop();
		m_CatTimer.Stop();
		ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
	}

	private static readonly Dictionary<string, string[]> s_FrameFiles = new()
	{
		["strolling"] = ["r06_c00.png","r06_c01.png","r06_c02.png","r06_c03.png","r06_c04.png","r06_c05.png","r06_c06.png","r06_c07.png","r06_c08.png"],
		["meow"]      = ["r33_c00.png","r33_c01.png","r33_c02.png","r33_c03.png","r33_c04.png","r33_c05.png","r33_c06.png","r33_c07.png"],
		["tired"]     = ["r38_c00.png","r38_c01.png","r38_c02.png","r38_c03.png","r38_c04.png","r38_c05.png","r38_c06.png"],
		["sleeping"]  = ["r16_c00.png","r16_c01.png"],
		["attention"] = ["r52_c00.png","r52_c01.png","r52_c02.png","r52_c03.png"],
		["error"]     = ["r39_c00.png","r39_c01.png","r39_c02.png","r39_c03.png","r39_c04.png","r39_c05.png","r39_c06.png","r39_c07.png","r39_c08.png","r39_c09.png","r39_c10.png","r40_c00.png","r40_c01.png","r40_c02.png","r40_c03.png","r40_c04.png","r40_c05.png","r40_c06.png","r40_c07.png","r40_c08.png","r40_c09.png"],
	};

	private static Dictionary<string, BitmapImage[]> LoadCatFrames()
	{
		var result = new Dictionary<string, BitmapImage[]>();
		foreach ( var (state, files) in s_FrameFiles )
		{
			var frames = files
				.Select( f => new BitmapImage( new Uri( $"pack://application:,,,/Resources/Cat/{state}/{f}" ) ) )
				.ToArray();
			result[state] = frames;
		}
		return result;
	}

	private void CatTimer_Tick( object? sender, EventArgs e ) => AdvanceCatFrame();

	private static readonly Dictionary<string, int> s_StateIntervals = new()
	{
		["attention"] = 200,
		["strolling"] = 200,
		["meow"]      = 250,
		["tired"]     = 350,
		["sleeping"]  = 1000,
		["error"]     = 200,
	};

	private static readonly Random s_Rng = new();
	private bool m_WaitingLoopPause;
	private bool m_PlayedFirstAgain;

	// (minMs, maxMs) for random pause between cycles
	private static readonly Dictionary<string, (int Min, int Max)> s_PauseRange = new()
	{
		["attention"] = (    0, 10000),
		["meow"]      = (    0, 10000),
		["tired"]     = (    0, 20000),
		["error"]     = (    0, 10000),
		["strolling"] = ( 	 0, 10000),
	};

	private int RandomPauseMs( string state ) =>
		s_PauseRange.TryGetValue( state, out var r ) ? s_Rng.Next( r.Min, r.Max + 1 ) : 0;

	private int RandomStrollingFrameMs() => s_Rng.Next( 100, 201 );

	private void AdvanceCatFrame()
	{
		var state = ViewModel.CatStateName;
		if ( !m_CatFrames.TryGetValue( state, out var frames ) || frames.Length == 0 )
			return;

		if ( state != m_CurrentCatState )
		{
			m_CatFrameIndex = 0;
			m_CurrentCatState = state;
			m_WaitingLoopPause = false;
			m_PlayedFirstAgain = false;
			m_CatTimer.Interval = TimeSpan.FromMilliseconds(
				state == "strolling" ? RandomStrollingFrameMs()
				: s_StateIntervals.TryGetValue( state, out var ms ) ? ms : 200 );
		}
		else if ( m_WaitingLoopPause )
		{
			// Pause elapsed — resume from frame 0
			m_WaitingLoopPause = false;
			m_PlayedFirstAgain = false;
			m_CatFrameIndex = 0;
			m_CatTimer.Interval = TimeSpan.FromMilliseconds(
				state == "strolling" ? RandomStrollingFrameMs()
				: s_StateIntervals.TryGetValue( state, out var ms ) ? ms : 200 );
		}
		else
		{
			var next = m_CatFrameIndex + 1;
			if ( next >= frames.Length )
			{
				if ( state == "strolling" )
				{
					m_CatFrameIndex = 0;
					m_WaitingLoopPause = true;
					m_CatTimer.Interval = TimeSpan.FromMilliseconds( RandomPauseMs( state ) );
				}
				else if ( state == "sleeping" )
				{
					m_CatFrameIndex = 0;
				}
				else if ( !m_PlayedFirstAgain )
				{
					// Show first frame once more before pausing
					m_PlayedFirstAgain = true;
					m_CatFrameIndex = 0;
				}
				else
				{
					// End of sequence — show c00 and pause
					m_CatFrameIndex = 0;
					m_WaitingLoopPause = true;
					m_CatTimer.Interval = TimeSpan.FromMilliseconds( RandomPauseMs( state ) );
				}
			}
			else
			{
				m_CatFrameIndex = next;
			}
		}

		ViewModel.CatImageSource = frames[m_CatFrameIndex];
	}


	private void PositionNearTray()
	{
		var workArea = SystemParameters.WorkArea;
		Left = workArea.Right - Width - 12;
		Top = workArea.Bottom - Height - 12;
	}

	private void Border_MouseLeftButtonDown( object sender, MouseButtonEventArgs e )
	{
		// Allow the user to drag the window from anywhere on its surface
		// (button clicks are already handled/marked handled by the button itself).
		if ( e.ButtonState == MouseButtonState.Pressed )
		{
			DragMove();
		}
	}


	private bool m_IsClosing;

	private void CloseWindow()
	{
		if ( m_IsClosing )
		{
			return;
		}

		m_IsClosing = true;
		Close();
	}

	private void CloseButton_Click( object sender, RoutedEventArgs e )
	{
		CloseWindow();
	}

	private async void RefreshButton_Click( object sender, RoutedEventArgs e )
	{
		if ( m_RefreshAction != null )
		{
			await m_RefreshAction();
		}
	}
}
