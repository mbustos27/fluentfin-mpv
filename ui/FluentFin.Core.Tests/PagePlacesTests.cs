namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, interaction item 5): Back to an item page gives its scroll position and focus back; a new
/// visit starts at the top. The pages' side (saving on leave, restoring once the content is in): ItemButtonsXamlTests.
/// </summary>
public sealed class PagePlacesTests
{
	private static readonly Guid Item = Guid.NewGuid();

	[Fact]
	public void BackOrForward_GetsThePlaceBack_ANewVisitDoesNot()
	{
		var key = PagePlaces.Key("SeriesPage", Item);
		var place = new PagePlace(840, Guid.NewGuid(), null);

		PagePlaces.Save(key, place);

		Assert.Same(place, PagePlaces.ForReturn(key, returning: true));
		Assert.Null(PagePlaces.ForReturn(key, returning: false));
		Assert.Null(PagePlaces.ForReturn(PagePlaces.Key("SeasonPage", Item), returning: true));
	}

	[Fact]
	public void TheLatestPlace_OfAPageAndItem_Wins()
	{
		var key = PagePlaces.Key("MoviePage", Guid.NewGuid());

		PagePlaces.Save(key, new PagePlace(100, null, [0, 1]));
		PagePlaces.Save(key, new PagePlace(300, null, [0, 2]));

		Assert.Equal(300, PagePlaces.ForReturn(key, true)!.VerticalOffset);
	}

	[Fact]
	public void OnlyTheNewestPlacesAreKept()
	{
		var first = PagePlaces.Key("EpisodePage", Guid.NewGuid());
		PagePlaces.Save(first, new PagePlace(1, null, null));
		for (var i = 0; i < PagePlaces.Capacity; i++)
		{
			PagePlaces.Save(PagePlaces.Key("EpisodePage", Guid.NewGuid()), new PagePlace(i, null, null));
		}

		Assert.Null(PagePlaces.ForReturn(first, true));
	}

	[Theory]
	[InlineData(0, 0, false, 0.0)]          // the top: nothing to wait for
	[InlineData(800, 2000, false, 800.0)]   // tall enough already
	[InlineData(800, 300, false, null)]     // still arriving: wait
	[InlineData(800, 300, true, 300.0)]     // loaded and shorter than it was: as far as it goes
	[InlineData(800, 799.7, false, 800.0)]  // rounding
	public void TheOffset_WaitsForTheContent(double saved, double scrollable, bool loaded, double? expected)
	{
		Assert.Equal(expected, PagePlaces.OffsetToRestore(saved, scrollable, loaded));
	}
}
