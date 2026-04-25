namespace CopilotUsage.Models;

public sealed class GeminiQuotaBucket
{
	public string ModelId { get; set; } = string.Empty;

	/// <summary>Fraction of quota remaining (0.0–1.0). 1.0 = fully available, 0.0 = exhausted.</summary>
	public double RemainingFraction { get; set; }

	public DateTimeOffset ResetTime { get; set; }

	public double RemainingPercent => Math.Round( RemainingFraction * 100, 1 );
	public double UsedPercent      => Math.Round( ( 1.0 - RemainingFraction ) * 100, 1 );

	public string RemainingPercentSummary => $"{RemainingPercent:0.#}% remaining";
	public string ResetTimeSummary        => "resets " + ResetTime.ToLocalTime().ToString( "HH:mm" );

	public string ColorZone => UsedPercent switch
	{
		>= 80 => "Red",
		>= 60 => "Yellow",
		_     => "Green",
	};

	public string DisplayName => ModelId switch
	{
		"gemini-2.5-pro"        => "Gemini 2.5 Pro",
		"gemini-2.5-flash"      => "Gemini 2.5 Flash",
		"gemini-2.5-flash-lite" => "Gemini 2.5 Flash Lite",
		_                       => ModelId,
	};
}
