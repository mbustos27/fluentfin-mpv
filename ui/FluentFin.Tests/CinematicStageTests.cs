using FluentFin.Core.ViewModels;
using FluentFin.Themes.Cinematic.Views;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.1 (the UX review's C-B1): Cinematic's library stage previews, and its Play plays, the title the user is on: the
/// still the pointer rests on, else the one the keyboard is on, else the first in view after a sort, a filter or a scroll;
/// never a title the grid no longer shows.
/// </summary>
public sealed class CinematicStageTests
{
	private static List<BaseItemViewModel> Items(params string[] names) =>
		[.. names.Select(n => BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = n, Type = BaseItemDto_Type.Movie }))];

	[Fact]
	public void ThePointer_ThenTheKeyboard_ThenTheFirstInView()
	{
		var items = Items("A Quiet Harbor", "Bright Lines", "Copper Shadow", "Delta");

		Assert.Equal("Copper Shadow", CinematicLibraryPage.StageTarget(items, hovered: items[2].Id, focused: items[1].Id, firstVisible: 0)!.Name);
		Assert.Equal("Bright Lines", CinematicLibraryPage.StageTarget(items, hovered: null, focused: items[1].Id, firstVisible: 3)!.Name);
		// scrolled: the first title in view, not the first of the list
		Assert.Equal("Delta", CinematicLibraryPage.StageTarget(items, hovered: null, focused: null, firstVisible: 3)!.Name);
		Assert.Equal("A Quiet Harbor", CinematicLibraryPage.StageTarget(items, hovered: null, focused: null, firstVisible: -1)!.Name);
	}

	[Fact]
	public void TheKeyboardOnTheStagesOwnPlay_KeepsItsTitle()
	{
		var items = Items("A", "B", "C");

		Assert.Equal("B", CinematicLibraryPage.StageTarget(items, null, null, firstVisible: 0, kept: items[1].Id)!.Name);
	}

	[Fact]
	public void AfterASortOrAFilter_ATitleNoLongerShown_IsNeverOffered()
	{
		var before = Items("A Quiet Harbor", "Bright Lines");
		var filtered = Items("Copper Shadow");

		// the still the keyboard was on, and the title the stage kept, were filtered out: the first shown takes the stage
		Assert.Equal("Copper Shadow", CinematicLibraryPage.StageTarget(filtered, hovered: before[0].Id, focused: before[1].Id, firstVisible: 0, kept: before[0].Id)!.Name);
		// nothing matches: no title, so no Play
		Assert.Null(CinematicLibraryPage.StageTarget([], null, null, 0));
	}

	[Fact]
	public void ThePage_RefreshesTheStage_OnEveryChange_AndNotOnlyOnTheFirst()
	{
		var page = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Themes", "Cinematic", "Views", "CinematicLibraryPage.xaml.cs"));

		Assert.DoesNotContain("Stage.Current is null &&", page);
		Assert.Contains("ViewModel.Items).CollectionChanged += (_, _) => QueueStage();", page);
		Assert.Contains("GridScroller.ViewChanged += (_, _) => QueueStage();", page);
		Assert.Contains("PointerEnteredEvent", page);
		Assert.Contains("Stage.ShowNothing();", page);
		Assert.Contains("MarkLetter(_markedLetter);", page);
	}
}
