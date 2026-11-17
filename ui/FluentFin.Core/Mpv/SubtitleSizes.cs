namespace FluentFin.Core.Mpv;

/// <summary>
/// fluentfin-mpv: the subtitle text size the player's subtitles menu offers, in percent of the size the item has: 50 to 200 in
/// steps of 10, 100 by default. mpv's <c>sub-scale</c> is the percent / 100 (it scales text and ASS subtitles); the backend
/// writes it (<c>set_subtitle_size</c>, docs/BRIDGE.md §4).
/// </summary>
public static class SubtitleSizes
{
	public const int Minimum = 50;
	public const int Maximum = 200;
	public const int Step = 10;
	public const int Default = 100;

	/// <summary>Within the range and on a step (a stored 133 is 130).</summary>
	public static int Clamp(int percent) => Math.Clamp((int)Math.Round(percent / (double)Step, MidpointRounding.AwayFromZero) * Step, Minimum, Maximum);

	/// <summary>mpv's <c>sub-scale</c> for a size.</summary>
	public static double ToScale(int percent) => Clamp(percent) / 100.0;

	/// <summary>"120%".</summary>
	public static string Text(int percent) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Clamp(percent)}%");
}
