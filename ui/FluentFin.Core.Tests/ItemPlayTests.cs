using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0: Play on a series or a season says what it starts (Seaglass UX review, fix 12): "Resume S1 E4" with its
/// progress, "Play S2 E1", "Play from the start"; a movie keeps "Resume at 12:34" / "Play". The label must name the episode the
/// players start (MpvQueue, PlaylistViewModel.AutoSelect), so the two are checked against each other.
/// </summary>
public sealed class ItemPlayTests
{
	private const long Minute = 600_000_000;
	private static readonly Guid SeriesId = Guid.NewGuid();

	private static BaseItemDto Episode(int season, int number, bool played = false, long position = 0, double? percent = null, string? name = null) => new()
	{
		Id = Guid.NewGuid(),
		Type = BaseItemDto_Type.Episode,
		SeriesId = SeriesId,
		SeriesName = "Electric Ravenmoor",
		ParentIndexNumber = season,
		IndexNumber = number,
		Name = name ?? $"Episode {number}",
		RunTimeTicks = 40 * Minute,
		UserData = new UserItemDataDto { Played = played, PlaybackPositionTicks = position, PlayedPercentage = percent },
	};

	private static BaseItemDto Series => new() { Id = SeriesId, Type = BaseItemDto_Type.Series, Name = "Electric Ravenmoor" };

	private static BaseItemDto Season(int n) => new() { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = $"Season {n}", IndexNumber = n, SeriesId = SeriesId, SeriesName = "Electric Ravenmoor" };

	[Fact]
	public void AResumePoint_IsResumeWithTheCode_AndItsProgress()
	{
		List<BaseItemDto> episodes = [Episode(1, 1, played: true), Episode(1, 2, played: true), Episode(1, 3, played: true), Episode(1, 4, position: 18 * Minute, percent: 45.2, name: "The Archive Problem"), Episode(1, 5)];

		var target = ItemPlay.For(Series, episodes);

		Assert.Equal("Resume S1 E4", target.Label);
		Assert.Equal("Resume S1 E4 The Archive Problem, 45% watched", target.Name);
		Assert.Equal(45.2, target.Progress);
		Assert.True(target.HasProgress);
	}

	[Fact]
	public void AResumePointWithoutAPercentage_TakesItFromTheTicks()
	{
		var target = ItemPlay.For(Series, [Episode(1, 1, position: 10 * Minute)]);

		Assert.Equal("Resume S1 E1", target.Label);
		Assert.Equal(25.0, target.Progress);
	}

	[Fact]
	public void TheNextUnwatched_IsPlayWithItsCode_EvenInAnotherSeason()
	{
		List<BaseItemDto> episodes = [Episode(1, 1, played: true), Episode(1, 2, played: true), Episode(2, 1, name: "Homecoming"), Episode(2, 2)];

		var target = ItemPlay.For(Season(1), episodes);

		Assert.Equal("Play S2 E1", target.Label);
		Assert.Equal("Play S2 E1 Homecoming", target.Name);
		Assert.Null(target.Progress);
		Assert.False(target.HasProgress);
	}

	[Fact]
	public void AllWatched_IsPlayFromTheStart_TheFirstEpisode()
	{
		List<BaseItemDto> episodes = [Episode(1, 1, played: true, name: "Pilot"), Episode(1, 2, played: true)];

		var target = ItemPlay.For(Series, episodes);

		Assert.Equal("Play from the start", target.Label);
		Assert.Equal("Play from the start, S1 E1 Pilot", target.Name);
		Assert.Null(target.Progress);
	}

	[Theory]
	[InlineData(0L, "Play", null)]
	[InlineData(7_540_000_000L, "Resume at 12:34", 31.4)]
	public void AMovie_IsResumeAtItsPoint_OrPlay(long ticks, string expected, double? progress)
	{
		var movie = new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "Heat", UserData = new UserItemDataDto { PlaybackPositionTicks = ticks, PlayedPercentage = ticks > 0 ? 31.4 : null } };

		var target = ItemPlay.For(movie, null);

		Assert.Equal(expected, target.Label);
		Assert.Equal(expected, target.Name);
		Assert.Equal(progress, target.Progress);
		Assert.Equal(ItemLabels.PlayLabel(movie), target.Label);
	}

	[Fact]
	public void BeforeTheEpisodesAreKnown_OrWithoutAny_ItIsPlay()
	{
		Assert.Same(PlayTarget.Plain, ItemPlay.For(Series, null));
		Assert.Same(PlayTarget.Plain, ItemPlay.For(Season(1), []));
		Assert.Equal("Play", PlayTarget.Plain.Label);
		Assert.Equal("Play", PlayTarget.Plain.Name);
	}

	[Fact]
	public void AnEpisodeWithoutANumber_IsNamedByItsTitle()
	{
		var special = Episode(1, 1, name: "Behind the Scenes");
		special.ParentIndexNumber = null;
		special.IndexNumber = null;

		Assert.Equal("Play Behind the Scenes", ItemPlay.For(Series, [special]).Label);
	}

	public static TheoryData<string> Shapes => ["resume", "next", "all", "none", "gap", "unknown"];

	[Theory]
	[MemberData(nameof(Shapes))]
	public async Task TheLabel_NamesTheEpisodeTheMpvQueueStarts(string shape)
	{
		List<BaseItemDto> episodes = shape switch
		{
			"resume" => [Episode(1, 1, played: true), Episode(1, 2, position: 5 * Minute), Episode(1, 3)],
			"next" => [Episode(1, 1, played: true), Episode(2, 1), Episode(2, 2)],
			"all" => [Episode(1, 1, played: true), Episode(1, 2, played: true)],
			"none" => [Episode(1, 1), Episode(1, 2)],
			"gap" => [Episode(1, 1), Episode(1, 2, played: true), Episode(1, 3)],   // the first not played, not the one after the last played
			_ => [Episode(1, 1, played: true), Episode(1, 2), Episode(1, 3)],
		};
		if (shape == "unknown")
		{
			episodes[1].UserData = null;
		}

		var client = new Mock<IJellyfinClient>(MockBehavior.Strict);
		client.Setup(x => x.GetSeriesEpisodes(SeriesId)).ReturnsAsync(new BaseItemDtoQueryResult { Items = episodes });

		foreach (var item in new[] { Series, Season(2) })
		{
			var queue = await MpvQueue.BuildAsync(client.Object, item, null);
			var started = episodes[queue!.StartIndex];

			Assert.Equal(queue.StartIndex, ItemPlay.StartIndex(episodes));
			Assert.Contains(ItemLabels.EpisodeCode(started), ItemPlay.For(item, episodes).Name);
		}
	}

	[Theory]
	[InlineData("series")]
	[InlineData("season")]
	public async Task ThePages_AskForTheSeriesEpisodes_AndShowWhatPlayStarts(string page)
	{
		var season = Season(1);
		List<BaseItemDto> episodes = [Episode(1, 1, played: true), Episode(1, 2, position: 18 * Minute, percent: 45)];
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(SeriesId)).ReturnsAsync(Series);
		client.Setup(x => x.GetItem(season.Id!.Value)).ReturnsAsync(season);
		client.Setup(x => x.GetSeriesEpisodes(SeriesId)).ReturnsAsync(new BaseItemDtoQueryResult { Items = episodes });

		var (vm, target) = await Open(page, client, season);

		Assert.Equal("Resume S1 E2", target().Label);
		Assert.Equal(45, target().Progress);
		client.Verify(x => x.GetSeriesEpisodes(SeriesId), Times.Once);
		Assert.NotNull(vm);
	}

	[Theory]
	[InlineData("series")]
	[InlineData("season")]
	public async Task WhenTheEpisodesCannotBeLoaded_ThePageSaysPlay(string page)
	{
		var season = Season(1);
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(SeriesId)).ReturnsAsync(Series);
		client.Setup(x => x.GetItem(season.Id!.Value)).ReturnsAsync(season);
		client.Setup(x => x.GetSeriesEpisodes(SeriesId)).ReturnsAsync((BaseItemDtoQueryResult?)null);

		var (_, target) = await Open(page, client, season);

		Assert.Same(PlayTarget.Plain, target());
	}

	private static async Task<(INavigationAware Page, Func<PlayTarget> Target)> Open(string page, Mock<IJellyfinClient> client, BaseItemDto season)
	{
		if (page == "series")
		{
			var series = new SeriesViewModel(client.Object);
			await series.OnNavigatedTo(Series);
			return (series, () => series.PlayTarget);
		}

		var vm = new SeasonViewModel(client.Object);
		await vm.OnNavigatedTo(season);
		return (vm, () => vm.PlayTarget);
	}
}
