using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, layout items 8, 9 and 21): the item hero's facts as one plain line, the hero's height as a
/// share of the window's, and the series page's episode list with its season picker.
/// </summary>
public sealed class ItemLayoutTests
{
	private static readonly Guid SeriesId = Guid.NewGuid();

	[Fact]
	public void TheFacts_AreOnePlainLineWithDots_NotChips()
	{
		var movie = new BaseItemDto
		{
			Type = BaseItemDto_Type.Movie,
			ProductionYear = 2008,
			OfficialRating = "R",
			CommunityRating = 8.3f,
			RunTimeTicks = TimeSpan.FromMinutes(112).Ticks,
			MediaStreams =
			[
				new MediaStream { Type = MediaStream_Type.Video, Width = 3840, Height = 2160 },
				new MediaStream { Type = MediaStream_Type.Audio, Codec = "aac", Channels = 2, IsDefault = true },
			],
		};

		Assert.Equal("2008 · R · ★ 8.3 · 1h 52m · 4K", ItemChips.MetaLine(movie));
		Assert.Equal("Year 2008, Rated R, Community rating 8.3 out of 10, Runtime 1h 52m, Resolution 4K", ItemChips.MetaName(movie));
	}

	[Fact]
	public void SurroundSound_StaysInTheLine_PlainStereoDoesNot()
	{
		var movie = new BaseItemDto
		{
			Type = BaseItemDto_Type.Movie,
			MediaStreams = [new MediaStream { Type = MediaStream_Type.Audio, Codec = "eac3", Channels = 6 }],
		};

		Assert.EndsWith("5.1", ItemChips.MetaLine(movie));
	}

	[Fact]
	public void AnEpisodesLine_StartsWithItsNumber_InTheOneFormat()
	{
		var episode = new BaseItemDto
		{
			Type = BaseItemDto_Type.Episode,
			ParentIndexNumber = 1,
			IndexNumber = 1,
			PremiereDate = new DateTimeOffset(1991, 9, 1, 0, 0, 0, TimeSpan.Zero),
			OfficialRating = "TV-Y7",
			RunTimeTicks = TimeSpan.FromMinutes(10).Ticks,
		};

		Assert.Equal("S1 E1 · Sep 1, 1991 · TV-Y7 · 10m", ItemChips.MetaLine(episode));
		Assert.StartsWith("Season 1, episode 1, Aired Sep 1, 1991", ItemChips.MetaName(episode));
		Assert.Equal("", ItemChips.MetaLine(null));
	}

	[Theory]
	[InlineData(900, HeroLayout.ItemShare, 540)]       // 1100x900: the first content shows
	[InlineData(900, HeroLayout.EpisodesShare, 405)]   // the series and season pages leave room for the episodes
	[InlineData(1440, HeroLayout.ItemShare, 640)]      // never taller than before
	[InlineData(500, HeroLayout.ItemShare, 360)]       // never too short for the words
	[InlineData(0, HeroLayout.ItemShare, 640)]         // before the window is known
	public void TheHero_IsAShareOfTheWindow_Between360And640(double window, double share, double expected)
	{
		Assert.Equal(expected, HeroLayout.Height(window, share));
	}

	[Theory]
	[InlineData(432, 380, 48, 432)]   // the words fit: the share of the window, as before
	[InlineData(432, 520, 48, 568)]   // 1280 x 720, an episode's long title, logo and overview: grown to hold them below the bar
	[InlineData(640, 600, 48, 648)]
	[InlineData(432, 384, 48, 432)]   // exactly fitting
	[InlineData(432, 384.2, 48, 433)]
	public void TheHero_GrowsToHoldItsWords_BelowTheBar(double height, double words, double top, double expected)
	{
		// Nixie 1.7.5 (the gate's g3): the words, held at the hero's bottom, ran up under the top bar: the crumbs out of view
		Assert.Equal(expected, HeroLayout.Fit(height, words, top));
	}

	[Fact]
	public void AShortHero_IsCompact()
	{
		Assert.True(HeroLayout.IsCompact(HeroLayout.Height(900, HeroLayout.EpisodesShare)));
		Assert.False(HeroLayout.IsCompact(HeroLayout.Height(1080, HeroLayout.ItemShare)));
	}

	private static BaseItemDto Season(int n) => new() { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = $"Season {n}", IndexNumber = n, SeriesId = SeriesId };

	private static BaseItemDto Episode(BaseItemDto season, int n) => new()
	{
		Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, SeriesId = SeriesId, SeasonId = season.Id, ParentIndexNumber = season.IndexNumber, IndexNumber = n, Name = $"Episode {n}",
	};

	private static (SeriesViewModel Vm, Mock<IJellyfinClient> Client, List<BaseItemDto> Seasons, Dictionary<Guid, List<BaseItemDto>> Episodes) Series(BaseItemDto? nextUp, int seasons = 3)
	{
		var series = new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series, Name = "Lost" };
		var list = Enumerable.Range(1, seasons).Select(Season).ToList();
		var episodes = list.ToDictionary(s => s.Id!.Value, s => Enumerable.Range(1, 4).Select(n => Episode(s, n)).ToList());
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(SeriesId)).ReturnsAsync(series);
		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == SeriesId), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = list });
		foreach (var season in list)
		{
			client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == season.Id), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = episodes[season.Id!.Value] });
		}

		client.Setup(x => x.GetNextUp(It.IsAny<BaseItemDto>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = nextUp is null ? [] : [nextUp] });
		return (new SeriesViewModel(client.Object), client, list, episodes);
	}

	[Fact]
	public async Task TheSeriesPage_ListsTheSeasonOfTheNextEpisode_WithThePicker()
	{
		var (vm, client, seasons, episodes) = Series(nextUp: null);
		// the server's next episode is the third of season 2
		client.Setup(x => x.GetNextUp(It.IsAny<BaseItemDto>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [episodes[seasons[1].Id!.Value][2]] });

		await vm.OnNavigatedTo(new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series });

		Assert.Equal(seasons[1].Id, vm.SelectedSeason!.Id);
		Assert.Equal(episodes[seasons[1].Id!.Value], vm.Episodes);
		Assert.Equal(["Season 1", "Season 2", "Season 3"], vm.SeasonPills.Select(p => p.Name));
		Assert.Equal([false, true, false], vm.SeasonPills.Select(p => p.IsCurrent));
	}

	[Fact]
	public async Task WithoutANextEpisode_ItIsTheFirstSeason_AndAPillSwitchesTheListInPlace()
	{
		var (vm, _, seasons, episodes) = Series(nextUp: null);
		await vm.OnNavigatedTo(new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series });
		Assert.Equal(seasons[0].Id, vm.SelectedSeason!.Id);

		await vm.SelectSeasonCommand.ExecuteAsync(seasons[2]);

		Assert.Equal(seasons[2].Id, vm.SelectedSeason!.Id);
		Assert.Equal(episodes[seasons[2].Id!.Value], vm.Episodes);
		Assert.Equal([false, false, true], vm.SeasonPills.Select(p => p.IsCurrent));
	}

	[Fact]
	public async Task TheLinkToTheSeasonsPage_NamesTheSeasonShown_AndFollowsThePicker()
	{
		var (vm, _, seasons, _) = Series(nextUp: null);
		Assert.Equal("Season details", vm.SeasonDetailsText);
		await vm.OnNavigatedTo(new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series });
		Assert.Equal("Season 1 details", vm.SeasonDetailsText);

		var changed = new List<string?>();
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		await vm.SelectSeasonCommand.ExecuteAsync(seasons[1]);

		Assert.Equal("Season 2 details", vm.SeasonDetailsText);
		Assert.Contains(nameof(SeriesViewModel.SeasonDetailsText), changed);
	}

	[Fact]
	public async Task OneSeason_NeedsNoPicker_ButItsEpisodesAreListed()
	{
		var (vm, _, seasons, episodes) = Series(nextUp: null, seasons: 1);
		await vm.OnNavigatedTo(new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series });

		Assert.Empty(vm.SeasonPills);
		Assert.Equal(episodes[seasons[0].Id!.Value], vm.Episodes);
	}
}
