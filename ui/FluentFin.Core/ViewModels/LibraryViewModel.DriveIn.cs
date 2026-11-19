using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

// Nixie 1.3.0, the Drive-in layout theme: what its Library's letter board shows, over the shared view model. Additive only.
public partial class LibraryViewModel
{
	/// <summary>Drive-in: the kind of library (its count's words: "FEATURES ON THE LOT", "SHOWS ON THE LOT").</summary>
	public BaseItemDto_CollectionType? DriveInCollectionType => _library?.CollectionType;
}

/// <summary>
/// One tile of the A to Z: its letter, and the jump it makes (null: no title starts with it; the tile rests). Nixie 1.4.0:
/// every theme's (LibraryBrowse.Tiles), and it says whether its letter is the one in view (<see cref="IsCurrent"/>): set on
/// the tile, not on a button the repeater recycles (the UX review's C-M6: the mark went stale).
/// </summary>
public sealed partial class LetterTile(string letter, LetterJump? jump) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
	public string Letter { get; } = letter;

	public LetterJump? Jump { get; } = jump;

	public bool HasTitles => Jump is not null;

	/// <summary>Its place among the letters that have titles, and how many have (Narrator's "3 of 24"); 0 for a resting tile.</summary>
	public int Position { get; internal set; }

	public int Size { get; internal set; }

	/// <summary>What Narrator hears: the jump's name ("Jump to M"); a resting tile is not reachable.</summary>
	public string Name => Jump?.Name ?? $"No titles start with {Letter}";

	/// <summary>The first title in view files under this letter.</summary>
	[CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
	public partial bool IsCurrent { get; set; }
}

/// <summary>Nixie 1.3.0, Drive-in: the Library's letter board.</summary>
public static class DriveInLibrary
{
	/// <summary>The board's tiles: the shared A to Z (<see cref="LibraryBrowse.Tiles"/>).</summary>
	public static IReadOnlyList<LetterTile> Tiles(IReadOnlyList<LetterJump> letters) => LibraryBrowse.Tiles(letters);

	/// <summary>The words beside the nixie count: "FEATURES ON THE LOT" (movies), "SHOWS ON THE LOT" (series), "TITLES ON THE LOT".</summary>
	public static string CountLabel(BaseItemDto_CollectionType? collection) => collection switch
	{
		BaseItemDto_CollectionType.Movies => "FEATURES ON THE LOT",
		BaseItemDto_CollectionType.Tvshows => "SHOWS ON THE LOT",
		_ => "TITLES ON THE LOT",
	};

	/// <summary>
	/// The count in tubes: up to five digits ("312"); a larger library shows "99999" at most (the words say the rest). Nixie
	/// 1.4.0 (the UX review's DI-M3): a filter that leaves nothing reads "0" (it read nothing, pixel for pixel the loading
	/// header); only while the library loads are the tubes dark.
	/// </summary>
	public static string CountTubes(int count, bool loading = false) =>
		loading ? "" : Math.Clamp(count, 0, 99999).ToString(CultureInfo.InvariantCulture);
}
