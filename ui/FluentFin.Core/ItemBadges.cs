using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>
/// Nixie 1.4.0 (the library UX review, X10): what a card's two marks show, in every theme. One pink capsule used to mean both
/// "watched" (a check) and "N still to watch" (a number) in the same corner. Now: watched is a dim neutral check, top right,
/// only when nothing is left to watch; "N to watch" a neutral square count, top left, for a series, season or collection with
/// episodes or films left. Never both at once. The card's name says the same to Narrator (<see cref="ItemLabels.CardName"/>).
/// </summary>
public static class ItemBadges
{
	/// <summary>The most a count chip spells out; more reads "99+" (the chip stays a small square).</summary>
	public const int MaxShown = 99;

	/// <summary>Watched: played, with nothing left to watch under it.</summary>
	public static bool Watched(bool? played, int? unplayed) => played == true && (unplayed ?? 0) <= 0;

	public static bool Watched(BaseItemDto? dto) => Watched(dto?.UserData?.Played, dto?.UserData?.UnplayedItemCount);

	/// <summary>How many are left to watch (0: no chip).</summary>
	public static int ToWatch(int? unplayed) => unplayed is > 0 and var n ? n : 0;

	public static int ToWatch(BaseItemDto? dto) => ToWatch(dto?.UserData?.UnplayedItemCount);

	/// <summary>The chip's words: "5", "99+", or "" (no chip).</summary>
	public static string ToWatchText(int? unplayed) => ToWatch(unplayed) switch
	{
		0 => "",
		> MaxShown => $"{MaxShown}+",
		var n => n.ToString(CultureInfo.InvariantCulture),
	};

	public static string ToWatchText(BaseItemDto? dto) => ToWatchText(dto?.UserData?.UnplayedItemCount);
}
