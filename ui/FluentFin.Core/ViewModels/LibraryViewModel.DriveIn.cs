using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

// Nixie 1.3.0, the Drive-in layout theme: what its Library's letter board shows, over the shared view model. Additive only.
public partial class LibraryViewModel
{
	/// <summary>Drive-in: the kind of library (its count's words: "FEATURES ON THE LOT", "SHOWS ON THE LOT").</summary>
	public BaseItemDto_CollectionType? DriveInCollectionType => _library?.CollectionType;
}

/// <summary>One tile of the letter board's A to Z: its letter, and the jump it makes (null: no title starts with it; the tile rests).</summary>
public sealed record LetterTile(string Letter, LetterJump? Jump)
{
	public bool HasTitles => Jump is not null;

	/// <summary>What Narrator hears: the jump's name ("Jump to M"); a resting tile is not reachable.</summary>
	public string Name => Jump?.Name ?? $"No titles start with {Letter}";
}

/// <summary>Nixie 1.3.0, Drive-in: the Library's letter board.</summary>
public static class DriveInLibrary
{
	/// <summary>The board's tiles: # and A to Z always (a letter with no titles rests, dimmed and not a stop), then any other letter the titles start with.</summary>
	public static IReadOnlyList<LetterTile> Tiles(IReadOnlyList<LetterJump> letters)
	{
		var byLetter = letters.ToDictionary(l => l.Letter, StringComparer.OrdinalIgnoreCase);
		var tiles = new List<LetterTile>();
		foreach (var letter in "#ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString()))
		{
			tiles.Add(new LetterTile(letter, byLetter.GetValueOrDefault(letter)));
			byLetter.Remove(letter);
		}

		tiles.AddRange(letters.Where(l => byLetter.ContainsKey(l.Letter)).Select(l => new LetterTile(l.Letter, l)));
		return tiles;
	}

	/// <summary>The words beside the nixie count: "FEATURES ON THE LOT" (movies), "SHOWS ON THE LOT" (series), "TITLES ON THE LOT".</summary>
	public static string CountLabel(BaseItemDto_CollectionType? collection) => collection switch
	{
		BaseItemDto_CollectionType.Movies => "FEATURES ON THE LOT",
		BaseItemDto_CollectionType.Tvshows => "SHOWS ON THE LOT",
		_ => "TITLES ON THE LOT",
	};

	/// <summary>The count in tubes: up to five digits ("312"); a larger library shows "99999" at most (the words say the rest).</summary>
	public static string CountTubes(int count) => count <= 0 ? "" : Math.Min(count, 99999).ToString(CultureInfo.InvariantCulture);
}
