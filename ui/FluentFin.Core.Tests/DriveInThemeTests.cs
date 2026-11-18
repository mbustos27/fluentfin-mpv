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
}
