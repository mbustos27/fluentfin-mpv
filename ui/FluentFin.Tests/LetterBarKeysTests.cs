using FluentFin.Controls;
using FluentFin.Core.ViewModels;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (R in a grid is Refresh, so the titles that start with R are on the A to Z): on the A to Z a letter key presses
/// that letter, R included, and a digit presses #; the window's keys leave the A to Z's letters to it and to a grid whose
/// title is being typed (WindowShortcuts).
/// </summary>
public sealed class LetterBarKeysTests
{
	private static readonly List<LetterTile> Tiles =
	[
		new("#", new LetterJump("#", 0)),
		new("A", new LetterJump("A", 3)),
		new("M", new LetterJump("M", 40)),
		new("Q", null),
		new("R", new LetterJump("R", 61)),
	];

	[Theory]
	[InlineData('r', "R")]
	[InlineData('R', "R")]
	[InlineData('m', "M")]
	[InlineData('7', "#")]
	[InlineData('q', "Q")]   // a resting letter is found, and has no jump (nothing happens)
	public void ALetterKeyOnTheAToZ_PressesThatLetter(char typed, string letter)
	{
		var tile = LetterBar.TileFor(Tiles, typed);
		Assert.NotNull(tile);
		Assert.Equal(letter, tile!.Letter);
		Assert.Equal(letter != "Q", tile.HasTitles);
	}

	[Theory]
	[InlineData(' ')]
	[InlineData('-')]
	[InlineData('é')]
	[InlineData('z')]   // no Z tile in this library
	public void AnyOtherKey_PressesNothing(char typed) => Assert.Null(LetterBar.TileFor(Tiles, typed));

	[Fact]
	public void TheWindowsKeys_LeaveTheLettersToTheAToZAndToATitleBeingTyped()
	{
		var source = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Services", "WindowShortcuts.cs"));
		Assert.Contains("|| Controls.GridTypeAhead.Continues(e.OriginalSource) || Controls.LetterBar.OwnsLetters(e.OriginalSource));", source);
		var grid = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Controls", "CardGrid.cs"));
		Assert.Contains("GridTypeAhead.Typed(sender);", grid);
		var editorial = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Themes", "Editorial", "Views", "EditorialLibraryPage.xaml.cs"));
		Assert.Contains("FluentFin.Controls.GridTypeAhead.Typed(sender);", editorial);
	}
}
