using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0: the names and tooltips of a card's hover buttons and an item page's icon buttons say which item they act on
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
		// 1.2.0 (Nixie button review, I37): the button reads "Play from start"
		Assert.Equal("Play Electric Ravenmoor from start", ItemLabels.PlayFromBeginningAction(Movie));
	}

	[Fact]
	public void Play_IsNamedResume_WhenTheClickResumes()
	{
		// 1.2.0 (Nixie button review, I3): a card's Play resumes an item with a resume point, and says so
		var started = new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "Heat", UserData = new UserItemDataDto { PlaybackPositionTicks = 6_000_000_000 } };
		Assert.Equal("Resume Heat", ItemLabels.PlayAction(started));

		// a series or season starts its next episode, whatever its own record holds
		var series = new BaseItemDto { Type = BaseItemDto_Type.Series, Name = "Lost", UserData = new UserItemDataDto { PlaybackPositionTicks = 6_000_000_000 } };
		Assert.Equal("Play Lost", ItemLabels.PlayAction(series));
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
		Assert.Equal("Play from start", ItemLabels.PlayFromBeginningAction(null));
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
	[InlineData(BaseItemDto_Type.Episode, "The Fight", "The Office", 2, 3, "The Office S2 E3 The Fight")]
	[InlineData(BaseItemDto_Type.Episode, "sunny", "Sunny", 6, 4, "Sunny S6 E4")]          // a name that only repeats the series
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
	[InlineData(1, 4, null, "S1 E4")]
	[InlineData(2, 5, 6, "S2 E5–E6")]
	[InlineData(null, 7, null, "E7")]
	[InlineData(1, null, null, "")]
	public void EpisodeCode_IsTheOneFormat(int? season, int? number, int? end, string expected)
	{
		// 1.2.0 (layout audit item 7): "S1 E4" everywhere; "S1 E4 · Title" where the title follows
		Assert.Equal(expected, ItemLabels.EpisodeCode(new BaseItemDto { ParentIndexNumber = season, IndexNumber = number, IndexNumberEnd = end }));
		Assert.Equal(expected, ItemLabels.EpisodeCode(season, number, end));
	}

	[Theory]
	[InlineData(1, 4, "Pilot", "S1 E4 · Pilot")]
	[InlineData(1, 4, null, "S1 E4")]
	[InlineData(null, null, "Pilot", "Pilot")]
	[InlineData(null, 7, " Special ", "E7 · Special")]
	public void EpisodeTitle_IsTheCodeADotAndTheTitle(int? season, int? number, string? name, string expected)
	{
		Assert.Equal(expected, ItemLabels.EpisodeTitle(new BaseItemDto { ParentIndexNumber = season, IndexNumber = number, Name = name }));
	}
}
