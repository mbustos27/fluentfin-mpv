using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (the reverse toggle): the shared sort-direction setting: its words for Narrator, its arrow, the order mirrored,
/// and the session keep Back, Forward and R read it from.
/// </summary>
public sealed class SortDirectionTests
{
	[Theory]
	[InlineData(OrderRuns.AToZ, false, "A to Z")]
	[InlineData(OrderRuns.AToZ, true, "Z to A")]
	[InlineData(OrderRuns.OldestFirst, false, "Oldest first")]
	[InlineData(OrderRuns.OldestFirst, true, "Newest first")]
	[InlineData(OrderRuns.NewestFirst, false, "Newest first")]
	[InlineData(OrderRuns.NewestFirst, true, "Oldest first")]
	[InlineData(OrderRuns.FirstToLast, false, "First to last")]
	[InlineData(OrderRuns.FirstToLast, true, "Last to first")]
	[InlineData(OrderRuns.HighestFirst, false, "Highest first")]
	[InlineData(OrderRuns.HighestFirst, true, "Lowest first")]
	public void TheWords_SayTheDirectionTheCardsRun(OrderRuns runs, bool reversed, string words) =>
		Assert.Equal(words, SortDirections.Words(runs, reversed));

	[Fact]
	public void TheNarratorName_SaysTheDirectionFirst_ThenWhatAPressDoes()
	{
		// the brief: "Newest first", "Oldest first"; for A to Z, "Z to A" (the Library's direction button says it the same way)
		Assert.Equal("Newest first, switch to oldest first", SortDirections.ButtonName(OrderRuns.OldestFirst, reversed: true));
		Assert.Equal("Oldest first, switch to newest first", SortDirections.ButtonName(OrderRuns.OldestFirst, reversed: false));
		Assert.Equal("Z to A, switch to A to Z", SortDirections.ButtonName(OrderRuns.AToZ, reversed: true));
		Assert.Equal("A to Z, switch to Z to A", SortDirections.ButtonName(OrderRuns.AToZ, reversed: false));
		Assert.Equal("Switch to Z to A", SortDirections.ToolTip(OrderRuns.AToZ, reversed: false));
		Assert.Equal("Switch to last to first", SortDirections.ToolTip(OrderRuns.FirstToLast, reversed: false));
	}

	[Fact]
	public void TheArrow_PointsTheWayTheCardsRun_AsTheLibrarysDoes()
	{
		var up = SortDirections.Glyph(OrderRuns.AToZ, reversed: false);
		var down = SortDirections.Glyph(OrderRuns.AToZ, reversed: true);
		Assert.NotEqual(up, down);
		Assert.Equal(up, SortDirections.Glyph(OrderRuns.OldestFirst, false));
		Assert.Equal(down, SortDirections.Glyph(OrderRuns.NewestFirst, false)); // date added runs newest first: down
		Assert.Equal(up, SortDirections.Glyph(OrderRuns.NewestFirst, true));
		Assert.Equal(down, SortDirections.Glyph(OrderRuns.HighestFirst, false));
		Assert.Equal(LibraryBrowse.SortGlyph(Jellyfin.Sdk.Generated.Models.SortOrder.Ascending), up);
	}

	[Fact]
	public void Apply_MirrorsTheOrder_AndLeavesItWhenNotReversed()
	{
		IReadOnlyList<string> films = ["Wild Canyons", "Painted Forests", "Forest of Brightwater", "Undated"];

		Assert.Same(films, SortDirections.Apply(films, reversed: false));
		Assert.Equal(["Undated", "Forest of Brightwater", "Painted Forests", "Wild Canyons"], SortDirections.Apply(films, reversed: true));
		Assert.Equal(films, SortDirections.Apply(SortDirections.Apply(films, true), true));
		Assert.Empty(SortDirections.Apply(Array.Empty<string>(), true));
		Assert.Equal(["One"], SortDirections.Apply(["One"], true));
	}

	[Theory]
	[InlineData(CollectionOrder.Name, false, null, OrderRuns.AToZ)]
	[InlineData(CollectionOrder.ReleaseDate, false, null, OrderRuns.OldestFirst)]
	[InlineData(CollectionOrder.DateAdded, false, null, OrderRuns.NewestFirst)]
	[InlineData(CollectionOrder.Collection, false, null, OrderRuns.OldestFirst)] // a box set with no DisplayOrder: release order
	[InlineData(CollectionOrder.Collection, false, "PremiereDate", OrderRuns.OldestFirst)]
	[InlineData(CollectionOrder.Collection, false, "SortName", OrderRuns.AToZ)]
	[InlineData(CollectionOrder.Collection, true, null, OrderRuns.FirstToLast)] // a playlist's own order
	[InlineData(CollectionOrder.Collection, false, CollectionBrowse.TrackOrder, OrderRuns.FirstToLast)] // an album's tracks
	[InlineData(CollectionOrder.Collection, false, CollectionBrowse.ListOrder, OrderRuns.FirstToLast)] // trending's rank
	[InlineData(CollectionOrder.Name, true, null, OrderRuns.AToZ)] // a playlist by name
	public void ACollectionPagesOrder_RunsTheWayItsCardsDo(CollectionOrder order, bool playlist, string? displayOrder, OrderRuns runs) =>
		Assert.Equal(runs, SortDirections.Runs(order, playlist, displayOrder));

	[Fact]
	public void TheKeep_IsPerUserAndView_TheLatestPressWins_AndNullUntilPressed()
	{
		var keep = new SortDirectionKeep();
		var (me, other) = (Guid.NewGuid(), Guid.NewGuid());
		var set = SortDirections.ForCollection(Guid.NewGuid());
		var grid = SortDirections.ForGrid(CollectionsSection.MadeByNixie, SmartCollectionKind.Franchise);

		Assert.Null(keep.Recall(me, set));
		keep.Remember(me, set, true);
		Assert.True(keep.Recall(me, set));
		Assert.Null(keep.Recall(other, set)); // another user on this PC starts unreversed
		Assert.Null(keep.Recall(me, grid)); // another view too
		keep.Remember(me, set, false);
		Assert.False(keep.Recall(me, set)); // flipped back is kept too (Back shows it unreversed)
	}

	[Fact]
	public void TheKeep_KeepsTheNewest_AndForgetsTheOldest()
	{
		var keep = new SortDirectionKeep(kept: 3);
		var me = Guid.NewGuid();
		foreach (var view in new[] { "a", "b", "c", "d" })
		{
			keep.Remember(me, view, true);
		}

		Assert.Null(keep.Recall(me, "a"));
		Assert.True(keep.Recall(me, "d"));
		keep.Remember(me, "b", false); // a press moves its view to the newest
		keep.Remember(me, "e", true);
		Assert.False(keep.Recall(me, "b"));
		Assert.Null(keep.Recall(me, "c"));
	}

	[Fact]
	public void TheViews_AreDistinct()
	{
		var id = Guid.NewGuid();
		string[] views =
		[
			SortDirections.ForCollection(id), SortDirections.ForNixie("franchise:2344"), SortDirections.ForNixie("keyword:heist"),
			SortDirections.ForGrid(CollectionsSection.Server, null), SortDirections.ForGrid(CollectionsSection.Playlists, null),
			SortDirections.ForGrid(CollectionsSection.MadeByNixie, null), SortDirections.ForGrid(CollectionsSection.MadeByNixie, SmartCollectionKind.Franchise),
			SortDirections.ForGrid(CollectionsSection.MadeByNixie, SmartCollectionKind.Keyword),
		];
		Assert.Equal(views.Length, views.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void TheSharedKeep_ServesTheStaticCalls()
	{
		var me = Guid.NewGuid();
		var view = SortDirections.ForCollection(Guid.NewGuid());
		SortDirections.Remember(me, view, true);
		Assert.True(SortDirections.Shared.Recall(me, view));
		Assert.True(SortDirections.Recall(me, view));
	}
}
