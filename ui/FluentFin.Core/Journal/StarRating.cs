using System.Globalization;

namespace FluentFin.Core.Journal;

/// <summary>How a star is drawn.</summary>
public enum StarFill
{
	Empty,
	Half,
	Full,
}

/// <summary>
/// Half-star ratings stored as 1-10 (decision 5): 1 = half a star, 10 = five stars, 0 = not rated. The same number is
/// Jellyfin's 0-10 per-user <c>Rating</c> when mirroring is on. Pure functions, used by the rating control and the tests.
/// </summary>
public static class StarRating
{
	public const int Stars = 5;
	public const int Max = Stars * 2;

	/// <summary>Null, or a value outside 1-10, is "not rated" (0).</summary>
	public static int Normalize(int? value) => value is { } v and >= 1 and <= Max ? v : 0;

	public static bool IsValid(int value) => value is >= 1 and <= Max;

	/// <summary>How each of the five stars looks for this value.</summary>
	public static StarFill[] Fills(int value)
	{
		value = Normalize(value);
		var fills = new StarFill[Stars];
		for (var i = 0; i < Stars; i++)
		{
			var full = (i + 1) * 2;
			fills[i] = value >= full ? StarFill.Full : value == full - 1 ? StarFill.Half : StarFill.Empty;
		}

		return fills;
	}

	/// <summary>
	/// The value under the pointer: <paramref name="x"/> from the left edge of a control <paramref name="width"/> wide holding
	/// five stars of equal width. The left half of a star is a half star. Always 1-10 (left of the first star is half a star).
	/// </summary>
	public static int FromPointer(double x, double width)
	{
		if (!(width > 0) || double.IsNaN(x))
		{
			return 0;
		}

		var halfStarWidth = width / Max;
		var value = (int)Math.Floor(x / halfStarWidth) + 1;
		return Math.Clamp(value, 1, Max);
	}

	/// <summary>A click on <paramref name="clicked"/>: the same value again clears the rating, anything else sets it.</summary>
	public static int Click(int current, int clicked) => Normalize(clicked) == Normalize(current) ? 0 : Normalize(clicked);

	/// <summary>Arrow keys move by half a star; down from half a star clears, up from "not rated" is half a star.</summary>
	public static int Step(int current, int halfStars) => Math.Clamp(Normalize(current) + halfStars, 0, Max);

	/// <summary>"3.5" for 7; empty when not rated.</summary>
	public static string ToStars(int value)
	{
		value = Normalize(value);
		return value == 0 ? "" : (value / 2.0).ToString("0.#", CultureInfo.InvariantCulture);
	}

	/// <summary>For screen readers and tooltips: "3.5 of 5 stars", "Not rated".</summary>
	public static string Describe(int value)
	{
		value = Normalize(value);
		return value == 0 ? "Not rated" : $"{ToStars(value)} of {Stars} stars";
	}

	/// <summary>Jellyfin's per-user rating (0-10, may be fractional) as a journal value; null when it has none.</summary>
	public static int? FromJellyfin(double? rating) =>
		rating is { } r && r > 0 && Normalize((int)Math.Round(Math.Min(r, Max), MidpointRounding.AwayFromZero)) is var value and > 0 ? value : null;
}
