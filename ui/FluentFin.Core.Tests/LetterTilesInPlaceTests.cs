using FluentFin.Core.ViewModels;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.0 integration (the big-library stream with the A to Z): while a big library's pages arrive, the A to Z keeps its
/// tiles and sets their jumps again in place, so the buttons (and the keyboard on one) stay; seen in then-140-paging: every
/// page made the tiles anew and the keyboard on "A" fell onto a poster, where the next Enter opened its details.
/// </summary>
public sealed class LetterTilesInPlaceTests
{
	private static LetterJump Jump(string letter, int index) => new(letter, index);

	[Fact]
	public void TheSameLetters_KeepTheirTiles_TheJumpsMove()
	{
		var tiles = LibraryBrowse.Tiles([Jump("A", 0), Jump("B", 40)]);
		var a = tiles.Single(t => t.Letter == "A");
		var changed = new List<string?>();
		a.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		Assert.True(LibraryBrowse.UpdateTiles(tiles, [Jump("A", 0), Jump("B", 90)]));
		Assert.Same(a, tiles.Single(t => t.Letter == "A"));
		Assert.Equal(90, tiles.Single(t => t.Letter == "B").Jump!.Index);
		Assert.Empty(changed); // A's own jump did not change

		// a resting letter gets its first titles: its tile wakes up in place, and says so
		var c = tiles.Single(t => t.Letter == "C");
		var woke = new List<string?>();
		c.PropertyChanged += (_, e) => woke.Add(e.PropertyName);
		Assert.False(c.HasTitles);
		Assert.True(LibraryBrowse.UpdateTiles(tiles, [Jump("A", 0), Jump("B", 90), Jump("C", 140)]));
		Assert.True(c.HasTitles);
		Assert.Equal("Jump to C", c.Name);
		Assert.Contains(nameof(LetterTile.HasTitles), woke);
		Assert.Contains(nameof(LetterTile.Name), woke);
		Assert.Equal((3, 3), (c.Position, c.Size));
	}

	[Fact]
	public void ALetterOutsideTheAToZ_MakesTheTilesAnew()
	{
		var tiles = LibraryBrowse.Tiles([Jump("A", 0)]);
		Assert.False(LibraryBrowse.UpdateTiles(tiles, [Jump("A", 0), Jump("Б", 12)]));
	}
}
