using System.Globalization;

namespace FluentFin.Core.Mpv;

/// <summary>
/// 1.2.0: the playback speeds the player offers, 0.5x to 2x in quarter steps (the steps Jellyfin's web player
/// offers), 1 normal. mpv's <c>speed</c> is the value itself; the backend writes it (<c>set_speed</c>, docs/BRIDGE.md §4).
/// </summary>
public static class PlaybackSpeeds
{
	public const double Normal = 1.0;
	public const double Minimum = 0.5;
	public const double Maximum = 2.0;

	/// <summary>Every speed offered, slowest first.</summary>
	public static IReadOnlyList<double> Steps { get; } = [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

	/// <summary>The offered speed nearest <paramref name="speed"/> (anything not a number is normal).</summary>
	public static double Clamp(double speed)
	{
		if (!double.IsFinite(speed))
		{
			return Normal;
		}

		var nearest = Steps[0];
		foreach (var step in Steps)
		{
			if (Math.Abs(step - speed) < Math.Abs(nearest - speed))
			{
				nearest = step;
			}
		}

		return nearest;
	}

	/// <summary>The next speed up from <paramref name="speed"/>; the fastest stays.</summary>
	public static double Faster(double speed) => Steps.FirstOrDefault(x => x > Clamp(speed) + 1e-9, Maximum);

	/// <summary>The next speed down from <paramref name="speed"/>; the slowest stays.</summary>
	public static double Slower(double speed) => Steps.LastOrDefault(x => x < Clamp(speed) - 1e-9, Minimum);

	/// <summary>"1.5×", "0.75×", "1×".</summary>
	public static string Text(double speed) => string.Create(CultureInfo.InvariantCulture, $"{Clamp(speed):0.##}×");

	/// <summary>A menu entry: "Normal" for 1x, else <see cref="Text"/>.</summary>
	public static string Label(double speed) => IsNormal(speed) ? "Normal" : Text(speed);

	public static bool IsNormal(double speed) => Clamp(speed) == Normal;
}
