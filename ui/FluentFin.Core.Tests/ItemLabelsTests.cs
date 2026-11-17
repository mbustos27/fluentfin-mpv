using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>fluentfin-mpv: the words Narrator reads for media cards and the item pages' play button (ux-review A1, I2).</summary>
public sealed class ItemLabelsTests
{
	[Fact]
	public void Movie_IsTitleYearAndWatched()
	{
		var card = Card(new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "Inception", ProductionYear = 2010 }, played: true);

		Assert.Equal("Inception, 2010, watched", ItemLabels.CardName(card));
	}

	[Fact]
	public void InProgress_SaysHowFar_AsTheProgressBarShows()
	{
		var card = Card(new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "Heat", ProductionYear = 1995 }, percent: 44.6);

		Assert.Equal("Heat, 1995, 45% watched", ItemLabels.CardName(card));
	}

	[Fact]
	public void ATinyProgress_IsNotReadAsZero()
	{
		var card = Card(new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "Heat" }, percent: 0.2);

		Assert.Equal("Heat, 1% watched", ItemLabels.CardName(card));
	}

	[Theory]
	[InlineData(99.5)]
	[InlineData(99.9)]
	public void AnUnwatchedItemNearTheEnd_IsNotReadAsAHundred(double percent)
	{
		// Played is the server's word for watched; a bar short of full is at most 99%
		var card = Card(new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "Heat" }, percent: percent);

		Assert.Equal("Heat, 99% watched", ItemLabels.CardName(card));
	}

	[Fact]
	public void Episode_IsSeriesNumberAndName_InWords()
	{
		var card = Card(new BaseItemDto { Type = BaseItemDto_Type.Episode, SeriesName = "The Office", ParentIndexNumber = 2, IndexNumber = 3, Name = "The Fight" }, favorite: true);

		Assert.Equal("The Office, season 2 episode 3, The Fight, favorite", ItemLabels.CardName(card));
	}

	[Fact]
	public void Episode_NamedLikeItsSeries_IsNotReadTwice()
	{
		var card = Card(new BaseItemDto { Type = BaseItemDto_Type.Episode, SeriesName = "Sunny", ParentIndexNumber = 6, IndexNumber = 4, Name = "sunny " });

		Assert.Equal("Sunny, season 6 episode 4", ItemLabels.CardName(card));
	}

	[Theory]
	[InlineData("Continuing", null, "Lost, 2004 to present, 3 unwatched episodes")]
	[InlineData("Ended", 2010, "Lost, 2004 to 2010, 3 unwatched episodes")]
	[InlineData("Ended", null, "Lost, 2004, 3 unwatched episodes")]
	public void Series_HasItsYearsAndTheUnwatchedBadge(string status, int? endYear, string expected)
	{
		var dto = new BaseItemDto
		{
			Type = BaseItemDto_Type.Series,
			Name = "Lost",
			ProductionYear = 2004,
			Status = status,
			EndDate = endYear is { } y ? new DateTimeOffset(y, 5, 23, 0, 0, 0, TimeSpan.Zero) : null,
		};

		Assert.Equal(expected, ItemLabels.CardName(Card(dto, unplayed: 3)));
	}

	[Fact]
	public void OneUnwatched_IsSingular()
	{
		Assert.Equal("Season 1, 1 unwatched episode", ItemLabels.CardName(Card(new BaseItemDto { Type = BaseItemDto_Type.Season, Name = "Season 1" }, unplayed: 1)));
	}

	[Fact]
	public void NoItem_IsAnEmptyName()
	{
		Assert.Equal("", ItemLabels.CardName(null));
	}

	[Theory]
	[InlineData(0L, "Play")]
	[InlineData(null, "Play")]
	[InlineData(7_540_000_000L, "Resume at 12:34")]
	[InlineData(37_230_000_000L, "Resume at 1:02:03")]
	[InlineData(50_000_000L, "Resume at 0:05")]
	public void PlayLabel_NamesTheResumePoint(long? ticks, string expected)
	{
		var dto = new BaseItemDto { UserData = new UserItemDataDto { PlaybackPositionTicks = ticks } };

		Assert.Equal(expected, ItemLabels.PlayLabel(dto));
		Assert.Equal(ticks > 0, ItemLabels.ResumePosition(dto) is not null);
	}

	[Fact]
	public void PlayLabel_WithoutUserData_IsPlay()
	{
		Assert.Equal("Play", ItemLabels.PlayLabel(new BaseItemDto()));
		Assert.Equal("Play", ItemLabels.PlayLabel(null));
	}

	[Theory]
	[InlineData(3, "Pilot", "Play, episode 3, Pilot")]
	[InlineData(null, "Pilot", "Play, Pilot")]
	[InlineData(null, null, "Play")]
	public void EpisodeAction_SaysWhichEpisode(int? number, string? name, string expected)
	{
		Assert.Equal(expected, ItemLabels.EpisodeAction("Play", new BaseItemDto { IndexNumber = number, Name = name }));
	}

	[Theory]
	[InlineData(1, 4, null, "Pilot", true, null, false, "S1E4 Pilot, watched")]
	[InlineData(1, 4, null, "Pilot", false, null, false, "S1E4 Pilot")]
	[InlineData(2, 5, 6, "Two Parter", false, 45.4, true, "S2E5-E6 Two Parter, 45% watched, favorite")]
	[InlineData(null, 7, null, "Special", false, 0.2, false, "E7 Special, 1% watched")]  // a sliver of progress still shows
	[InlineData(1, 2, null, "Almost", false, 99.5, false, "S1E2 Almost, 99% watched")]   // not watched: never "100%"
	[InlineData(1, 2, null, "Almost", false, 99.9, false, "S1E2 Almost, 99% watched")]
	[InlineData(null, null, null, "Behind the Scenes", false, null, false, "Behind the Scenes")]
	[InlineData(3, 1, null, null, true, 50.0, false, "S3E1, watched")]                 // watched wins over a stale percentage
	[InlineData(3, 1, 1, " Trimmed ", false, null, false, "S3E1 Trimmed")]             // an end equal to the start is one episode
	public void EpisodeRow_IsTheCodeTheTitleAndWhatTheRowShows(int? season, int? number, int? end, string? name, bool played, double? percent, bool favorite, string expected)
	{
		var episode = new BaseItemDto
		{
			Type = BaseItemDto_Type.Episode,
			ParentIndexNumber = season,
			IndexNumber = number,
			IndexNumberEnd = end,
			Name = name,
			UserData = new UserItemDataDto { Played = played, PlayedPercentage = percent, IsFavorite = favorite },
		};

		Assert.Equal(expected, ItemLabels.EpisodeRowName(episode));
	}

	[Fact]
	public void EpisodeRow_WithoutAnEpisode_IsEmpty()
	{
		Assert.Equal("", ItemLabels.EpisodeRowName(null));
		Assert.Equal("", ItemLabels.EpisodeRowName(new BaseItemDto()));
	}

	[Theory]
	[InlineData("alice", "Account, alice")]
	[InlineData(null, "Account")]
	[InlineData(" ", "Account")]
	public void AccountButton_IsNamedForTheUser(string? user, string expected)
	{
		Assert.Equal(expected, ItemLabels.AccountButtonName(user));
	}

	private static BaseItemViewModel Card(BaseItemDto dto, bool played = false, bool favorite = false, double? percent = null, int? unplayed = null)
	{
		dto.UserData = new UserItemDataDto { Played = played, IsFavorite = favorite, PlayedPercentage = percent, UnplayedItemCount = unplayed };
		return BaseItemViewModel.FromDto(dto);
	}
}
