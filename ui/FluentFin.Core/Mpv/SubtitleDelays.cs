using System.Globalization;

namespace FluentFin.Core.Mpv;

/// <summary>
/// 1.2.0: the subtitle delay, in seconds: steps of 0.1 s, -30 to 30 (positive shows the subtitles later). mpv's
/// <c>sub-delay</c>; the backend writes it (<c>set_subtitle_delay</c>, docs/BRIDGE.md §4).
/// </summary>
public static class SubtitleDelays
{
	public const double Step = 0.1;
	public const double Limit = 30.0;

	/// <summary>Within ±<see cref="Limit"/>, on a tenth of a second (anything not a number is 0).</summary>
	public static double Clamp(double seconds) =>
		double.IsFinite(seconds) ? Math.Round(Math.Clamp(seconds, -Limit, Limit), 1, MidpointRounding.AwayFromZero) : 0;

	/// <summary>"+0.3 s", "−1.2 s" (a minus sign), "0.0 s".</summary>
	public static string Text(double seconds)
	{
		var value = Clamp(seconds);
		var magnitude = Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture);
		return value > 0 ? $"+{magnitude} s" : value < 0 ? $"−{magnitude} s" : $"{magnitude} s";
	}

	/// <summary>The words for Narrator and the indicator: "0.3 s later", "1.2 s earlier", "none".</summary>
	public static string Describe(double seconds)
	{
		var value = Clamp(seconds);
		var magnitude = Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture);
		return value > 0 ? $"{magnitude} s later" : value < 0 ? $"{magnitude} s earlier" : "none";
	}
}
