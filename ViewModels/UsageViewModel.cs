using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CopilotUsage.Models;

namespace CopilotUsage.ViewModels;

internal sealed class UsageViewModel : INotifyPropertyChanged
{
	private double m_UsagePercent;       // Current session % — drives tray icon border
	private int? m_SessionUsed;
	private int? m_SessionLimit;
	private DateTimeOffset? m_SessionResetsAt;
	private double m_WeeklyPercent;
	private int? m_WeeklyUsed;
	private int? m_WeeklyLimit;
	private DateTimeOffset? m_WeeklyResetsAt;
	private bool m_ExtraUsageEnabled;
	private double? m_ExtraMonthlyLimitEur;
	private double? m_ExtraUsedEur;
	private double m_ExtraPercent;
	private DateTime m_LastUpdated;
	private string? m_ErrorMessage;
	private bool m_IsLoading;
	private bool m_IsDemoMode;
	private UsageProvider m_Provider = UsageProvider.Claude;


	// ── Session (primary bar + tray icon) ──────────────────────────────────────

	/// <summary>Current session usage percent (0–100). Drives the tray icon border.</summary>
	public double UsagePercent
	{
		get => m_UsagePercent;
		set
		{
			m_UsagePercent = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( UsageSummary ) );
			OnPropertyChanged( nameof( UsageColorZone ) );
			OnPropertyChanged( nameof( CatStateLabel ) );
			OnPropertyChanged( nameof( CatStateName ) );
			OnPropertyChanged( nameof( HasData ) );
		}
	}

	public int? SessionUsed
	{
		get => m_SessionUsed;
		set
		{
			m_SessionUsed = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( UsageSummary ) );
			OnPropertyChanged( nameof( MonthProgressSummary ) );
			OnPropertyChanged( nameof( CatStateLabel ) );
			OnPropertyChanged( nameof( CatStateName ) );
			OnPropertyChanged( nameof( CopilotCreditsSummary ) );
		}
	}

	public int? SessionLimit
	{
		get => m_SessionLimit;
		set
		{
			m_SessionLimit = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( UsageSummary ) );
			OnPropertyChanged( nameof( MonthProgressSummary ) );
			OnPropertyChanged( nameof( CatStateLabel ) );
			OnPropertyChanged( nameof( CatStateName ) );
			OnPropertyChanged( nameof( CopilotCreditsSummary ) );
		}
	}

	/// <summary>Returns "Green", "Yellow", or "Red" for XAML DataTrigger colour selection.</summary>
	public string UsageColorZone => m_UsagePercent switch
	{
		< 60 => "Green",
		< 80 => "Yellow",
		_ => "Red",
	};

	public string CatStateLabel =>
		HasError ? "error :(" :
		GetCatStateLabel();

	public string CatStateName =>
		HasError ? "error" :
		GetCatStateName();

	private ImageSource? m_CatImageSource;
	public ImageSource? CatImageSource
	{
		get => m_CatImageSource;
		set { m_CatImageSource = value; OnPropertyChanged(); }
	}

	/// <summary>e.g. "45 / 50  (90%)" when counts are available, otherwise "90% used".</summary>
	public string UsageSummary => m_SessionUsed.HasValue && m_SessionLimit.HasValue
		? $"{m_SessionUsed} / {m_SessionLimit}  ({m_UsagePercent:0}%)"
		: $"{m_UsagePercent:0}% used";

	public DateTimeOffset? SessionResetsAt
	{
		get => m_SessionResetsAt;
		set
		{
			m_SessionResetsAt = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( ResetAtSummary ) );
			OnPropertyChanged( nameof( MonthProgressPercent ) );
			OnPropertyChanged( nameof( MonthProgressSummary ) );
			OnPropertyChanged( nameof( WeeklyResetSummary ) );
			OnPropertyChanged( nameof( CatStateLabel ) );
			OnPropertyChanged( nameof( CatStateName ) );
			OnPropertyChanged( nameof( CopilotCreditsSummary ) );
		}
	}

	/// <summary>Relative reset time for the session, e.g. "Resets in 2 hr 53 min".</summary>
	public string ResetAtSummary
	{
		get
		{
			if ( !m_SessionResetsAt.HasValue ) return string.Empty;
			var remaining = m_SessionResetsAt.Value - DateTimeOffset.Now;
			if ( remaining <= TimeSpan.Zero ) return "Resetting soon";
			if ( m_Provider == UsageProvider.GitHubCopilot && remaining.TotalDays >= 1 )
			{
				var days = Math.Max( 1, (int)Math.Ceiling( remaining.TotalDays ) );
				return days == 1 ? "Resets in 1 day" : $"Resets in {days} days";
			}
			if ( remaining.TotalMinutes < 1 ) return "Resets in < 1 min";
			if ( remaining.TotalHours < 1 ) return $"Resets in {(int)remaining.TotalMinutes} min";
			int hrs = (int)remaining.TotalHours;
			int mins = remaining.Minutes;
			return mins == 0
				? $"Resets in {hrs} hr"
				: $"Resets in {hrs} hr {mins} min";
		}
	}


	// ── Weekly (secondary bar) ─────────────────────────────────────────────────

	/// <summary>Weekly all-models usage percent (0–100).</summary>
	public double WeeklyPercent
	{
		get => m_WeeklyPercent;
		set
		{
			m_WeeklyPercent = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( MonthProgressPercent ) );
			OnPropertyChanged( nameof( MonthProgressSummary ) );
			OnPropertyChanged( nameof( WeeklyColorZone ) );
			OnPropertyChanged( nameof( CatStateName ) );
		}
	}

	public int? WeeklyUsed
	{
		get => m_WeeklyUsed;
		set { m_WeeklyUsed = value; OnPropertyChanged(); OnPropertyChanged( nameof( MonthProgressSummary ) ); }
	}

	public int? WeeklyLimit
	{
		get => m_WeeklyLimit;
		set { m_WeeklyLimit = value; OnPropertyChanged(); OnPropertyChanged( nameof( MonthProgressSummary ) ); }
	}

	/// <summary>Returns "Green", "Yellow", or "Red" for the weekly bar colour trigger.</summary>
	public string WeeklyColorZone => m_WeeklyPercent switch
	{
		< 60 => "Green",
		< 80 => "Yellow",
		_ => "Red",
	};

	/// <summary>Alias used by existing XAML binding.</summary>
	public double MonthProgressPercent
	{
		get
		{
			if ( m_Provider != UsageProvider.GitHubCopilot )
			{
				return m_WeeklyPercent;
			}

			var ( elapsedDays, totalDays ) = GetCopilotMonthProgressDays();
			if ( totalDays <= 0 )
			{
				return 0;
			}

			return Math.Clamp( elapsedDays * 100.0 / totalDays, 0, 100 );
		}
	}

	/// <summary>e.g. "180 / 200  (90%)" when counts are available, otherwise "90% used".</summary>
	public string MonthProgressSummary
	{
		get
		{
			if ( m_Provider == UsageProvider.GitHubCopilot )
			{
				var ( elapsedDays, totalDays ) = GetCopilotMonthProgressDays();
				var percent = MonthProgressPercent;
				var creditsSummary = CopilotCreditsSummary;
				return totalDays > 0
					? string.IsNullOrEmpty( creditsSummary )
						? $"{elapsedDays} / {totalDays} days  ({percent:0}%)"
						: $"{elapsedDays} / {totalDays} days  ({percent:0}%)  •  {creditsSummary}"
					: creditsSummary;
			}

			return m_WeeklyUsed.HasValue && m_WeeklyLimit.HasValue
				? $"{m_WeeklyUsed} / {m_WeeklyLimit}  ({m_WeeklyPercent:0}%)"
				: $"{m_WeeklyPercent:0}% used";
		}
	}

	/// <summary>Static label used by existing XAML binding.</summary>
	public string MonthProgressLabel => m_Provider == UsageProvider.GitHubCopilot ? "Month progress" : "Weekly Usage";

	public string CopilotCreditsSummary
	{
		get
		{
			if ( m_Provider != UsageProvider.GitHubCopilot )
			{
				return string.Empty;
			}

			var budget = GetCopilotCreditBudget();
			return budget.HasValue
				? $"{budget.Value.RemainingCredits} left, {budget.Value.CreditsPerDay:0.#}/day"
				: string.Empty;
		}
	}

	public DateTimeOffset? WeeklyResetsAt
	{
		get => m_WeeklyResetsAt;
		set
		{
			m_WeeklyResetsAt = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( WeeklyResetSummary ) );
		}
	}

	/// <summary>Absolute reset time for weekly limit, e.g. "Resets Sun 3:59 PM".</summary>
	public string WeeklyResetSummary
	{
		get
		{
			if ( m_Provider == UsageProvider.GitHubCopilot )
			{
				return ResetAtSummary;
			}

			if ( !m_WeeklyResetsAt.HasValue ) return string.Empty;
			var local = m_WeeklyResetsAt.Value.ToLocalTime();
			return "Resets " + local.ToString( "ddd h:mm tt", CultureInfo.InvariantCulture );
		}
	}


	// ── Extra usage (pay-per-use credits) ─────────────────────────────────────

	public bool ExtraUsageEnabled
	{
		get => m_ExtraUsageEnabled;
		set { m_ExtraUsageEnabled = value; OnPropertyChanged(); }
	}

	public double ExtraPercent
	{
		get => m_ExtraPercent;
		set
		{
			m_ExtraPercent = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( ExtraColorZone ) );
			OnPropertyChanged( nameof( ExtraSummary ) );
		}
	}

	public string ExtraColorZone => m_ExtraPercent switch
	{
		< 60 => "Green",
		< 80 => "Yellow",
		_ => "Red",
	};

	/// <summary>e.g. "€2.36 / €17.00  (14%)"</summary>
	public string ExtraSummary
	{
		get
		{
			if ( m_ExtraUsedEur.HasValue && m_ExtraMonthlyLimitEur.HasValue )
				return $"€{m_ExtraUsedEur.Value:F2} / €{m_ExtraMonthlyLimitEur.Value:F2}  ({m_ExtraPercent:0}%)";
			return $"{m_ExtraPercent:0}% used";
		}
	}


	// ── Metadata ────────────────────────────────────────────────────────────────

	public DateTime LastUpdated
	{
		get => m_LastUpdated;
		set
		{
			m_LastUpdated = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( LastUpdatedSummary ) );
		}
	}

	public string LastUpdatedSummary =>
		m_LastUpdated == default
			? string.Empty
			: "Last updated " + m_LastUpdated.ToString( "HH:mm:ss", CultureInfo.InvariantCulture );

	public string? ErrorMessage
	{
		get => m_ErrorMessage;
		set
		{
			m_ErrorMessage = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( HasError ) );
			OnPropertyChanged( nameof( HasData ) );
			OnPropertyChanged( nameof( CatStateName ) );
			OnPropertyChanged( nameof( CatStateLabel ) );
		}
	}

	public bool HasError => !string.IsNullOrEmpty( m_ErrorMessage );

	public bool HasData => !HasError && m_LastUpdated != default;

	public bool IsLoading
	{
		get => m_IsLoading;
		set { m_IsLoading = value; OnPropertyChanged(); }
	}

	public bool IsDemoMode
	{
		get => m_IsDemoMode;
		set { m_IsDemoMode = value; OnPropertyChanged(); }
	}

	public UsageProvider Provider
	{
		get => m_Provider;
		set
		{
			m_Provider = value;
			OnPropertyChanged();
			OnPropertyChanged( nameof( AppTitle ) );
			OnPropertyChanged( nameof( SessionBarLabel ) );
			OnPropertyChanged( nameof( ShowWeeklyBar ) );
			OnPropertyChanged( nameof( ResetAtSummary ) );
			OnPropertyChanged( nameof( MonthProgressLabel ) );
			OnPropertyChanged( nameof( MonthProgressPercent ) );
			OnPropertyChanged( nameof( MonthProgressSummary ) );
			OnPropertyChanged( nameof( WeeklyResetSummary ) );
			OnPropertyChanged( nameof( CatStateLabel ) );
			OnPropertyChanged( nameof( CatStateName ) );
			OnPropertyChanged( nameof( CopilotCreditsSummary ) );
		}
	}

	public string AppTitle        => m_Provider == UsageProvider.GitHubCopilot ? "GitHub Copilot Usage" : "Claude AI Usage";
	public string SessionBarLabel => m_Provider == UsageProvider.GitHubCopilot ? "Monthly interactions" : "Current session";
	public bool   ShowWeeklyBar   => true;

	private (int ElapsedDays, int TotalDays) GetCopilotMonthProgressDays()
	{
		if ( !m_SessionResetsAt.HasValue )
		{
			return (0, 0);
		}

		var cycleEnd = m_SessionResetsAt.Value.ToLocalTime();
		var cycleStart = new DateTimeOffset( cycleEnd.Year, cycleEnd.Month, cycleEnd.Day, 0, 0, 0, cycleEnd.Offset ).AddMonths( -1 );
		var now = DateTimeOffset.Now;
		var totalDays = Math.Max( 1, (int)Math.Round( ( cycleEnd - cycleStart ).TotalDays, MidpointRounding.AwayFromZero ) );
		var elapsedDays = (int)Math.Ceiling( ( now - cycleStart ).TotalDays );
		elapsedDays = Math.Clamp( elapsedDays, 0, totalDays );

		return (elapsedDays, totalDays);
	}

	private (int RemainingCredits, int RemainingDays, double CreditsPerDay)? GetCopilotCreditBudget()
	{
		if ( !m_SessionUsed.HasValue || !m_SessionLimit.HasValue || !m_SessionResetsAt.HasValue )
		{
			return null;
		}

		var remainingCredits = Math.Max( m_SessionLimit.Value - m_SessionUsed.Value, 0 );
		var remainingDays = Math.Max( 1, (int)Math.Ceiling( ( m_SessionResetsAt.Value - DateTimeOffset.Now ).TotalDays ) );
		var creditsPerDay = remainingCredits / (double)remainingDays;

		return (remainingCredits, remainingDays, creditsPerDay);
	}

	private string GetCatStateLabel() => GetCatStateName() switch
	{
		"sleeping" => "zzzZZZ",
		"tired" => "tired...",
		"meow" => "meow~",
		"attention" => "!",
		_ => "strolling",
	};

	private string GetCatStateName()
	{
		if ( m_Provider == UsageProvider.GitHubCopilot )
		{
			var copilotState = GetCopilotCatStateName();
			if ( !string.IsNullOrEmpty( copilotState ) )
			{
				return copilotState;
			}
		}

		return m_WeeklyPercent >= 100 || m_UsagePercent >= 100 ? "sleeping" :
			m_UsagePercent >= 80 ? "tired" :
			m_UsagePercent >= 60 ? "meow" :
			m_UsagePercent <= 10 ? "attention" :
			"strolling";
	}

	private string? GetCopilotCatStateName()
	{
		if ( !m_SessionUsed.HasValue || !m_SessionLimit.HasValue )
		{
			return null;
		}

		if ( m_SessionUsed.Value >= m_SessionLimit.Value )
		{
			return "sleeping";
		}

		var ( elapsedDays, totalDays ) = GetCopilotMonthProgressDays();
		if ( elapsedDays <= 0 || totalDays <= 0 )
		{
			return null;
		}

		var expectedUsed = m_SessionLimit.Value * elapsedDays / (double)totalDays;
		var paceRatio = expectedUsed <= 0 ? 0 : m_SessionUsed.Value / expectedUsed;
		if ( paceRatio >= 1.35 )
		{
			return "tired";
		}

		if ( paceRatio >= 1.10 )
		{
			return "meow";
		}

		if ( paceRatio <= 0.50 )
		{
			return "attention";
		}

		return "strolling";
	}


	// ── Update from API data ────────────────────────────────────────────────────

	public void UpdateFromData( CopilotUsageData data, UsageProvider provider = UsageProvider.Claude )
	{
		Provider = provider;

		if ( data.IsUnavailable )
		{
			var name = provider == UsageProvider.GitHubCopilot ? "GitHub Copilot" : "Claude";
			ErrorMessage = string.IsNullOrEmpty( data.RawJson )
				? $"{name} usage data is not available. The API response format may have changed."
				: $"Could not parse usage from API response:\n{data.RawJson}";
			LastUpdated = DateTime.Now;
			return;
		}

		UsagePercent = data.SessionPercent;
		SessionUsed = data.SessionUsed;
		SessionLimit = data.SessionLimit;
		SessionResetsAt = data.SessionResetsAt;
		WeeklyPercent = data.WeeklyPercent;
		WeeklyUsed = data.WeeklyUsed;
		WeeklyLimit = data.WeeklyLimit;
		WeeklyResetsAt = data.WeeklyResetsAt;
		ExtraUsageEnabled = data.ExtraUsageEnabled;
		m_ExtraMonthlyLimitEur = data.ExtraUsageMonthlyLimitEur;
		m_ExtraUsedEur = data.ExtraUsageUsedEur;
		ExtraPercent = data.ExtraUsagePercent;
		LastUpdated = DateTime.Now;
		ErrorMessage = null;
	}


	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnPropertyChanged( [CallerMemberName] string? name = null )
		=> PropertyChanged?.Invoke( this, new PropertyChangedEventArgs( name ) );
}
