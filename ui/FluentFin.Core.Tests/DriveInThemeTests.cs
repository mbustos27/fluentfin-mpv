using System.Collections.ObjectModel;
using System.Collections.Specialized;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.3.0, the Drive-in layout theme: what its views show over the shared view models (HomeViewModel.DriveIn.cs).</summary>
public sealed class DriveInThemeTests
{
	private static BaseItemDto Movie(string name, long positionMinutes = 0, long runMinutes = 120, bool backdrop = true) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, ProductionYear = 1997,
		RunTimeTicks = runMinutes * TimeSpan.TicksPerMinute,
		BackdropImageTags = backdrop ? [$"bd-{name}"] : [],
		ImageTags = new BaseItemDto_ImageTags(),
		UserData = new UserItemDataDto { PlaybackPositionTicks = positionMinutes * TimeSpan.TicksPerMinute },
	};

	private static BaseItemDto Episode(string series, int season, int number, string name = "Static") => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Episode, SeriesName = series, SeriesId = Guid.NewGuid(),
		ParentIndexNumber = season, IndexNumber = number, ImageTags = new BaseItemDto_ImageTags(),
	};

	// ---- the outdoor screen's features --------------------------------------------------------------------------------

	[Fact]
	public void TheScreen_FeaturesTheLastThingWatchedFirst_ThenTheHero()
	{
		var last = Movie("Burning Horizons", positionMinutes: 46, runMinutes: 118);
		var hero = HeroSlides.Pick([last], [[Movie("Bridge of Avalon"), Movie("Blue Mesa")]], []);

		var features = DriveInHome.Features([last, Movie("Older", positionMinutes: 3)], hero);

		Assert.Equal(["Burning Horizons", "Bridge of Avalon", "Blue Mesa"], features.Select(f => f.Title));
		Assert.True(features[0].IsResume);
		Assert.Equal("Resume", features[0].PlayLabel);
	}

	[Fact]
	public void TheScreen_KeepsTheHerosLimit_AndShowsNoItemTwice()
	{
		var last = Movie("Resumed", positionMinutes: 10);
		var hero = Enumerable.Range(0, 5).Select(i => HeroSlides.Make(Movie($"New {i}"))!).ToList();

		var features = DriveInHome.Features([last], [HeroSlides.Make(last)!, .. hero]);

		Assert.Equal(HeroSlides.MaxSlides, features.Count);
		Assert.Single(features, f => f.Title == "Resumed");
	}

	[Fact]
	public void TheScreen_WithNothingInProgress_OrNoBackdrop_IsTheHero()
	{
		var hero = new List<HeroSlide> { HeroSlides.Make(Movie("New"))! };

		Assert.Equal(["New"], DriveInHome.Features([], hero).Select(f => f.Title));
		Assert.Equal(["New"], DriveInHome.Features([Movie("No art", positionMinutes: 4, backdrop: false)], hero).Select(f => f.Title));
	}

	[Fact]
	public void TheContinueRow_LeavesOutWhatTheScreenFeatures()
	{
		var last = Movie("Burning Horizons", positionMinutes: 46);
		var other = Movie("Iron Lighthouses", positionMinutes: 9);
		var features = DriveInHome.Features([last, other], []);

		var row = DriveInHome.ContinueRow([last, other], x => x.Id, features);

		Assert.Equal([other], row);
		Assert.Equal([last, other], DriveInHome.ContinueRow([last, other], x => x.Id, []));
	}

	[Fact]
	public void Sync_RemovesAsARemoval_AndKeepsTheCardsItStillShows()
	{
		string a = "a", b = "b", c = "c", d = "d";
		var shown = new ObservableCollection<string>([a, b, c]);
		var actions = new List<NotifyCollectionChangedAction>();
		shown.CollectionChanged += (_, e) => actions.Add(e.Action);

		DriveInHome.Sync(shown, [a, c]);
		Assert.Equal([a, c], shown);
		Assert.Equal([NotifyCollectionChangedAction.Remove], actions);

		actions.Clear();
		DriveInHome.Sync(shown, [d, c, a]);
		Assert.Equal([d, c, a], shown);
		Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
		Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, actions);
	}

	// ---- the Coming up sign ---------------------------------------------------------------------------------------------

	[Fact]
	public void ASignEntry_IsNamedForWhatItPlays()
	{
		Assert.Equal("Play A Signal in Winter, season 2, episode 1", DriveInHome.ComingUpName(Episode("A Signal in Winter", 2, 1)));
		Assert.Equal("Play Blue Mesa", DriveInHome.ComingUpName(Movie("Blue Mesa")));
		Assert.Equal("A Signal in Winter", DriveInHome.EntryTitle(Episode("A Signal in Winter", 2, 1)));
		Assert.Equal("Static", DriveInHome.EntryDetail(Episode("A Signal in Winter", 2, 1)));
		Assert.Equal("", DriveInHome.EntryDetail(Movie("Blue Mesa")));
	}

	[Fact]
	public void TheSign_ListsFourWide_ThreeNarrow_AndCountsAllOfUpNext()
	{
		Assert.Equal(4, DriveInHome.SignEntries(wide: true));
		Assert.Equal(3, DriveInHome.SignEntries(wide: false));
		Assert.Equal("All of Up next (9)", DriveInHome.AllUpNextText(9));
		Assert.Equal("5 in progress", DriveInHome.InProgressText(5));
		Assert.Equal("", DriveInHome.InProgressText(0));
	}

	// ---- the numbers in tubes ---------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(1, 3, null, "S1·E03")]
	[InlineData(12, 104, null, "S12·E104")]
	[InlineData(2, 5, 6, "S2·E05-06")]
	[InlineData(null, 7, null, "E07")]
	[InlineData(3, null, null, "")]
	public void AnEpisodeCode_InTubes(int? season, int? episode, int? end, string expected) =>
		Assert.Equal(expected, DriveInText.NixieCode(season, episode, end));

	[Fact]
	public void TheTimeLeft_InMinutes_ThenHoursAndMinutes_SpokenInWords()
	{
		Assert.Equal(("44", "MIN LEFT", "44 minutes left"), DriveInText.TimeLeft(TimeSpan.FromMinutes(44))!.Value);
		Assert.Equal(("1", "MIN LEFT", "1 minute left"), DriveInText.TimeLeft(TimeSpan.FromSeconds(20))!.Value);
		Assert.Equal(("1:12", "LEFT", "1 hour 12 minutes left"), DriveInText.TimeLeft(TimeSpan.FromMinutes(72))!.Value);
		Assert.Equal(("2:00", "LEFT", "2 hours left"), DriveInText.TimeLeft(TimeSpan.FromMinutes(120))!.Value);
		Assert.Null(DriveInText.TimeLeft(TimeSpan.Zero));
		Assert.Null(DriveInText.TimeLeft(null));
	}

	[Fact]
	public void TheTimeLeft_IsTheRuntimeLessTheResumePoint_OnlyWhenThereIsOne()
	{
		Assert.Equal(TimeSpan.FromMinutes(72), DriveInText.Left(Movie("Burning Horizons", positionMinutes: 46, runMinutes: 118)));
		Assert.Null(DriveInText.Left(Movie("Not started")));
		Assert.Null(DriveInText.Left(Movie("No runtime", positionMinutes: 4, runMinutes: 0)));
	}

	[Fact]
	public void BoardsAndConsole_PrintTheirNumbersAsASignDoes()
	{
		Assert.Equal("52 MIN", DriveInText.Runtime(52 * TimeSpan.TicksPerMinute));
		Assert.Equal("1 H 58 MIN", DriveInText.Runtime(118 * TimeSpan.TicksPerMinute));
		Assert.Equal("", DriveInText.Runtime(null));
		Assert.Equal("14 MAR 2022", DriveInText.ShowDate(new DateTimeOffset(2022, 3, 14, 0, 0, 0, TimeSpan.Zero)));
		Assert.Equal(("10:42", "PM"), DriveInText.Clock(new DateTime(2026, 11, 17, 22, 42, 0)));
		Assert.Equal("0:50:52", DriveInText.Counter(new TimeSpan(0, 50, 52)));
		Assert.Equal("50 minutes 52 seconds", DriveInText.CounterSpoken(new TimeSpan(0, 50, 52)));
		Assert.Equal("1 hour 1 second", DriveInText.CounterSpoken(new TimeSpan(1, 0, 1)));
		Assert.Equal("03", DriveInText.EpisodeNumber(3));
	}

	// ---- the item pages: the marquee board and the SHOWTIMES board ------------------------------------------------------

	private static BaseItemDto Ep(int number, bool played = false, long position = 0, long runMinutes = 50) => new()
	{
		Id = Guid.NewGuid(), Name = $"Ep {number}", Type = BaseItemDto_Type.Episode, ParentIndexNumber = 1, IndexNumber = number,
		RunTimeTicks = runMinutes * TimeSpan.TicksPerMinute,
		UserData = new UserItemDataDto { Played = played, PlaybackPositionTicks = position },
	};

	[Fact]
	public void TheNextShowtime_IsTheOneInProgress_ThenNextUp_ThenTheFirstNotWatched()
	{
		var e1 = Ep(1, played: true);
		var e2 = Ep(2);
		var e3 = Ep(3, position: 5 * TimeSpan.TicksPerMinute);
		Assert.Same(e3, DriveInItems.NextEpisode([e1, e2, e3], null));
		Assert.Same(e2, DriveInItems.NextEpisode([e1, e2, Ep(3)], null));
		var e4 = Ep(4);
		Assert.Same(e4, DriveInItems.NextEpisode([e1, e2, e4], e4));
		Assert.Null(DriveInItems.NextEpisode([e1], null));
		Assert.Null(DriveInItems.NextEpisode([], null));
	}

	[Fact]
	public void TheSeasonSummary_CountsEpisodesTimeAndWatched()
	{
		var episodes = new[] { Ep(1, played: true, runMinutes: 49), Ep(2, played: true, runMinutes: 49), Ep(3, runMinutes: 52), Ep(4, runMinutes: 51),
			Ep(5, runMinutes: 51), Ep(6, runMinutes: 53), Ep(7, runMinutes: 50), Ep(8, runMinutes: 57) };
		Assert.Equal("8 episodes · 6 h 52 m · 2 watched", DriveInItems.SeasonSummary(episodes));
		Assert.Equal("1 episode · 50 m", DriveInItems.SeasonSummary([Ep(1)]));
		Assert.Equal("", DriveInItems.SeasonSummary([]));
	}

	[Fact]
	public void ASeasonsTube_IsItsNumber_SpecialsAreExtras()
	{
		Assert.Equal("2", DriveInItems.SeasonTube(new BaseItemDto { IndexNumber = 2, Name = "Season 2" }));
		Assert.Null(DriveInItems.SeasonTube(new BaseItemDto { IndexNumber = 0, Name = "Specials" }));
		Assert.Equal("Extras", DriveInItems.SeasonLabel(new BaseItemDto { IndexNumber = 0, Name = "Specials" }));
		Assert.Equal("Bonus", DriveInItems.SeasonLabel(new BaseItemDto { Name = "Bonus" }));
	}

	[Fact]
	public void TheBoard_NamesItsCast_AndWhoMadeIt()
	{
		var series = new BaseItemDto
		{
			Type = BaseItemDto_Type.Series, Name = "The Iron Circus",
			People =
			[
				new BaseItemPerson { Name = "Odile Marchetti", Type = BaseItemPerson_Type.Actor },
				new BaseItemPerson { Name = "Teo Vance", Type = BaseItemPerson_Type.Actor },
				new BaseItemPerson { Name = "Ruth Okafor", Type = BaseItemPerson_Type.GuestStar },
				new BaseItemPerson { Name = "Fourth Actor", Type = BaseItemPerson_Type.Actor },
				new BaseItemPerson { Name = "Lena Duarte", Type = BaseItemPerson_Type.Creator },
			],
		};
		Assert.Equal("Odile Marchetti, Teo Vance, Ruth Okafor", DriveInItems.Starring(series));
		Assert.Equal(("Created by", "Lena Duarte"), DriveInItems.MadeBy(series));
		Assert.Equal(("", ""), DriveInItems.MadeBy(new BaseItemDto { Type = BaseItemDto_Type.Movie }));
		Assert.Equal("NOW SHOWING", DriveInItems.Kicker(series));
		Assert.Equal("THE IRON CIRCUS", DriveInItems.Kicker(new BaseItemDto { Type = BaseItemDto_Type.Season, SeriesName = "The Iron Circus" }));
	}

	[Fact]
	public void AShowtimesFacts_AreItsRuntimeAndAirDate()
	{
		var episode = Ep(3, runMinutes: 52);
		episode.PremiereDate = new DateTimeOffset(2022, 3, 28, 0, 0, 0, TimeSpan.Zero);
		Assert.Equal("52 MIN · 28 MAR 2022", DriveInItems.ShowtimeFacts(episode));
		Assert.Equal("", DriveInItems.ShowtimeFacts(null));
	}
}
