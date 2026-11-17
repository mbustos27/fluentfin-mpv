using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4: the names and tooltips of a card's hover buttons and an item page's icon buttons say which item they act on
/// and what a click does now (Seaglass UX review, fix 4). The XAML side (bound to the item and the toggle's state) is in
/// AccessibilityXamlTests.
/// </summary>
public sealed class ItemActionLabelsTests
{
	private static readonly BaseItemDto Movie = new() { Type = BaseItemDto_Type.Movie, Name = "Electric Ravenmoor" };

	[Theory]
	[InlineData(false, "Mark Electric Ravenmoor as watched")]
	[InlineData(null, "Mark Electric Ravenmoor as watched")]
	[InlineData(true, "Mark Electric Ravenmoor as unwatched")]
	public void Watched_NamesTheItem_AndWhatAClickDoesNow(bool? shown, string expected)
	{
		Assert.Equal(expected, ItemLabels.WatchedAction(Movie, shown));
	}

	[Theory]
	[InlineData(false, "Add Electric Ravenmoor to favorites")]
	[InlineData(null, "Add Electric Ravenmoor to favorites")]
	[InlineData(true, "Remove Electric Ravenmoor from favorites")]
	public void Favorite_NamesTheItem_AndWhatAClickDoesNow(bool? shown, string expected)
	{
		Assert.Equal(expected, ItemLabels.FavoriteAction(Movie, shown));
	}

	[Fact]
	public void PlayMoreAndTheBeginning_NameTheItem()
	{
		Assert.Equal("Play Electric Ravenmoor", ItemLabels.PlayAction(Movie));
		Assert.Equal("More options for Electric Ravenmoor", ItemLabels.MoreOptions(Movie));
		Assert.Equal("Play Electric Ravenmoor from the beginning", ItemLabels.PlayFromBeginningAction(Movie));
	}

	[Fact]
	public void WithoutAnItem_TheWordsStillReadAsAButton()
	{
		Assert.Equal("Play", ItemLabels.PlayAction(null));
		Assert.Equal("Mark as watched", ItemLabels.WatchedAction(null, false));
		Assert.Equal("Mark as unwatched", ItemLabels.WatchedAction(new BaseItemDto(), true));
		Assert.Equal("Add to favorites", ItemLabels.FavoriteAction(null, null));
		Assert.Equal("Remove from favorites", ItemLabels.FavoriteAction(null, true));
		Assert.Equal("More options", ItemLabels.MoreOptions(null));
		Assert.Equal("Play from the beginning", ItemLabels.PlayFromBeginningAction(null));
	}

	[Fact]
	public void TheWords_AreTheItemMenus()
	{
		// the More menu says the same for the same state (ItemMenu): one wording for one action
		var watched = new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "Heat", UserData = new UserItemDataDto { Played = true, IsFavorite = true } };
		var menu = ItemMenu.For(watched, user: null, includePlay: false, canEditSegments: false).SelectMany(g => g).Select(e => e.Text).ToList();

		Assert.Contains("Mark as unwatched", menu);
		Assert.Contains("Remove from favorites", menu);
		Assert.Equal("Mark as unwatched", ItemLabels.WatchedAction(null, true));
		Assert.Equal("Remove from favorites", ItemLabels.FavoriteAction(null, true));
	}

	[Theory]
	[InlineData(BaseItemDto_Type.Movie, "Inception", null, null, null, "Inception")]
	[InlineData(BaseItemDto_Type.Series, " Lost ", null, null, null, "Lost")]
	[InlineData(BaseItemDto_Type.Season, "Season 2", "Lost", 2, null, "Season 2 of Lost")]
	[InlineData(BaseItemDto_Type.Season, "Season 2", null, 2, null, "Season 2")]
	[InlineData(BaseItemDto_Type.Episode, "The Fight", "The Office", 2, 3, "The Office S2E3 The Fight")]
	[InlineData(BaseItemDto_Type.Episode, "sunny", "Sunny", 6, 4, "Sunny S6E4")]          // a name that only repeats the series
	[InlineData(BaseItemDto_Type.Episode, "Special", "Lost", null, null, "Lost Special")]  // no number
	public void TheTarget_SaysWhichItem_AnEpisodeWithItsSeries(BaseItemDto_Type type, string name, string? series, int? season, int? episode, string expected)
	{
		var dto = new BaseItemDto { Type = type, Name = name, SeriesName = series };
		if (type == BaseItemDto_Type.Episode)
		{
			dto.ParentIndexNumber = season;
			dto.IndexNumber = episode;
		}
		else if (type == BaseItemDto_Type.Season)
		{
			dto.IndexNumber = season;
		}

		Assert.Equal(expected, ItemLabels.ActionTarget(dto));
		Assert.Equal($"Play {expected}", ItemLabels.PlayAction(dto));
	}

	[Theory]
	[InlineData(false, "Mark as watched, episode 3, Pilot")]
	[InlineData(true, "Mark as unwatched, episode 3, Pilot")]
	public void EpisodeRowWatched_IsNamedAsItsPlay_AndSaysWhatAClickDoes(bool shown, string expected)
	{
		var pilot = new BaseItemDto { Type = BaseItemDto_Type.Episode, IndexNumber = 3, Name = "Pilot" };

		Assert.Equal(expected, ItemLabels.EpisodeWatchedAction(pilot, shown));
		Assert.Equal("Play, episode 3, Pilot", ItemLabels.EpisodeAction("Play", pilot));
	}

	[Theory]
	[InlineData(1, 4, null, "S1E4")]
	[InlineData(2, 5, 6, "S2E5-E6")]
	[InlineData(null, 7, null, "E7")]
	[InlineData(1, null, null, "")]
	public void EpisodeCode_IsTheRowsCode(int? season, int? number, int? end, string expected)
	{
		Assert.Equal(expected, ItemLabels.EpisodeCode(new BaseItemDto { ParentIndexNumber = season, IndexNumber = number, IndexNumberEnd = end }));
	}
}
