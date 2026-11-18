using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.3.0, the Cinematic theme's additions to FluentFin.Core (HomeViewModel.Cinematic.cs): the one Up next shelf
/// (Continue Watching, then Next Up, kept in step with both rows), the words its views show, the typeset title art's face,
/// and which picture a 16:9 still shows.
/// </summary>
public sealed class CinematicThemeTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-cinematic-test-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _socket = new();
	private readonly HomeCache _cache;

	public CinematicThemeTests() => _cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), new ManualClock());

	public void Dispose()
	{
		_cache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private HomeViewModel NewHome()
	{
		var client = Mock.Of<IJellyfinClient>();
		return new HomeViewModel(client, _socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), client, Mock.Of<INavigationViewServiceCore>(), Mock.Of<Settings.ISettings>()), _cache);
	}

	private static BaseItemViewModel Item(string name) => BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie });

	private static BaseItemDto Episode(string name, int season, int number, long runMinutes = 50, long positionMinutes = 0, bool played = false) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Episode, SeriesName = "The Iron Circus", SeriesId = Guid.NewGuid(),
		ParentIndexNumber = season, IndexNumber = number, RunTimeTicks = runMinutes * TimeSpan.TicksPerMinute,
		UserData = new UserItemDataDto { PlaybackPositionTicks = positionMinutes * TimeSpan.TicksPerMinute, Played = played },
	};

	// ---- the Up next shelf ---------------------------------------------------------------------------------------------

	[Fact]
	public void UpNext_IsContinueWatchingThenNextUp_TheSameItemModels()
	{
		var home = NewHome();
		var resume = Item("Half watched");
		var next = Item("Next episode");
		home.ContinueItems = [resume];
		home.NextUpItems = [next];

		Assert.Equal([resume, next], home.UpNext.Items);
		Assert.Same(resume, home.UpNext.Items[0]);
		Assert.False(home.UpNext.IsNextUp(resume));
		Assert.True(home.UpNext.IsNextUp(next));
	}

	[Fact]
	public void UpNext_FollowsBothRows_ItemsAddedRemovedAndReplaced()
	{
		var home = NewHome();
		var shelf = home.UpNext;
		var a = Item("A");
		var b = Item("B");
		var c = Item("C");
		home.ContinueItems.Add(a);
		home.NextUpItems.Add(b);
		Assert.Equal([a, b], shelf.Items);

		home.ContinueItems.Remove(a);
		Assert.Equal([b], shelf.Items);

		home.NextUpItems = [c, b];
		Assert.Equal([c, b], shelf.Items);
		Assert.True(shelf.HasItems);
		Assert.Equal("Continue watching and new episodes · 2", shelf.Summary);
	}

	[Fact]
	public void UpNext_LoadsUntilBothRowsHaveAnswered()
	{
		var home = NewHome();
		Assert.True(home.UpNext.IsLoading);

		home.IsContinueLoading = false;
		Assert.True(home.UpNext.IsLoading);

		home.IsNextUpLoading = false;
		Assert.False(home.UpNext.IsLoading);
		Assert.False(home.UpNext.HasItems);
		Assert.Equal("", home.UpNext.Summary);
	}

	[Fact]
	public void UpNext_AnItemInBothRows_IsShownOnce()
	{
		var id = Guid.NewGuid();
		var inProgress = BaseItemViewModel.FromDto(new BaseItemDto { Id = id, Name = "Twice" });
		var again = BaseItemViewModel.FromDto(new BaseItemDto { Id = id, Name = "Twice" });

		Assert.Equal([inProgress], UpNextShelf.Merge([inProgress], [again]));
	}

	[Fact]
	public void Apply_RemovesWhatLeft_AsARemove_SoTheShelfKeepsTheKeyboardWhereItWas()
	{
		var a = Item("A");
		var b = Item("B");
		var c = Item("C");
		var shown = new ObservableCollection<BaseItemViewModel>([a, b, c]);
		var actions = new List<NotifyCollectionChangedAction>();
		shown.CollectionChanged += (_, e) => actions.Add(e.Action);

		UpNextShelf.Apply(shown, [a, c]);

		Assert.Equal([a, c], shown);
		Assert.Equal([NotifyCollectionChangedAction.Remove], actions);
	}

	[Fact]
	public void Apply_AnUnchangedList_ChangesNothing_AndAMoveIsAMove()
	{
		var a = Item("A");
		var b = Item("B");
		var shown = new ObservableCollection<BaseItemViewModel>([a, b]);
		var actions = new List<NotifyCollectionChangedAction>();
		shown.CollectionChanged += (_, e) => actions.Add(e.Action);

		UpNextShelf.Apply(shown, [a, b]);
		Assert.Empty(actions);

		UpNextShelf.Apply(shown, [b, a]);
		Assert.Equal([b, a], shown);
		Assert.Equal([NotifyCollectionChangedAction.Move], actions);
	}

	// ---- the words -----------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(52, 8, "44 min left")]
	[InlineData(118, 46, "1 h 12 min left")]
	[InlineData(150, 30, "2 h left")]
	[InlineData(50, 49.9, "1 min left")]
	public void TimeLeft_IsMinutesOrHoursAndMinutes(double runMinutes, double positionMinutes, string expected)
	{
		var dto = new BaseItemDto
		{
			RunTimeTicks = (long)(runMinutes * TimeSpan.TicksPerMinute),
			UserData = new UserItemDataDto { PlaybackPositionTicks = (long)(positionMinutes * TimeSpan.TicksPerMinute) },
		};

		Assert.Equal(expected, CinematicLabels.TimeLeft(dto));
	}

	[Fact]
	public void TimeLeft_IsNull_WithNoResumePointOrNoLength()
	{
		Assert.Null(CinematicLabels.TimeLeft(new BaseItemDto { RunTimeTicks = TimeSpan.TicksPerHour }));
		Assert.Null(CinematicLabels.TimeLeft(new BaseItemDto { UserData = new UserItemDataDto { PlaybackPositionTicks = 10 } }));
		Assert.Null(CinematicLabels.TimeLeft(null));
	}

	[Fact]
	public void UpNextLine_SaysWhereAnEpisodeIs_AndHowMuchIsLeft()
	{
		Assert.Equal("S1 E3 · Sawdust · 44 min left", CinematicLabels.UpNextLine(Episode("Sawdust", 1, 3, 52, 8)));
		Assert.Equal("S2 E1 · The Thaw · Next episode", CinematicLabels.UpNextLine(Episode("The Thaw", 2, 1)));

		var film = new BaseItemDto
		{
			Type = BaseItemDto_Type.Movie, ProductionYear = 1997, RunTimeTicks = 118 * TimeSpan.TicksPerMinute,
			UserData = new UserItemDataDto { PlaybackPositionTicks = 46 * TimeSpan.TicksPerMinute },
		};
		Assert.Equal("1997 · 1 h 12 min left", CinematicLabels.UpNextLine(film));
	}

	[Fact]
	public void Kicker_SaysWhyTheHeroShowsIt_AndWhatItIs()
	{
		Assert.Equal("Continue watching · Film", CinematicLabels.Kicker(new BaseItemDto { Type = BaseItemDto_Type.Movie }, isResume: true));
		Assert.Equal("New · Series", CinematicLabels.Kicker(new BaseItemDto { Type = BaseItemDto_Type.Series }, isResume: false));
		Assert.Equal("Next up · Series", CinematicLabels.Kicker(new BaseItemDto { Type = BaseItemDto_Type.Episode }, isResume: false));
	}

	[Fact]
	public void TitleArtText_IsTheShowsNameForAnEpisode()
	{
		Assert.Equal("The Iron Circus", CinematicLabels.TitleArtText(Episode("Sawdust", 1, 3)));
		Assert.Equal("Burning Horizons", CinematicLabels.TitleArtText(new BaseItemDto { Name = " Burning Horizons ", Type = BaseItemDto_Type.Movie }));
	}

	[Fact]
	public void EpisodeStill_TitleBadgeAndLine()
	{
		var watched = Episode("Opening Night", 1, 1, 49, played: true);
		var under = Episode("Sawdust", 1, 3, 52, 8);
		under.PremiereDate = new DateTimeOffset(2022, 3, 21, 0, 0, 0, TimeSpan.Zero);

		Assert.Equal("1. Opening Night", CinematicLabels.EpisodeTitle(watched));
		Assert.Equal("E1", CinematicLabels.EpisodeBadge(watched));
		Assert.Equal("49 min · Watched", CinematicLabels.EpisodeMeta(watched));
		Assert.Equal("52 min · 44 min left · Aired 21 Mar 2022", CinematicLabels.EpisodeMeta(under));

		var twoParter = Episode("Finale", 2, 9);
		twoParter.IndexNumberEnd = 10;
		Assert.Equal("E9–E10", CinematicLabels.EpisodeBadge(twoParter));
		Assert.Equal("", CinematicLabels.EpisodeBadge(new BaseItemDto { Name = "Special" }));
	}

	[Fact]
	public void SeasonSummary_CountsEpisodesTimeAndWatched()
	{
		List<BaseItemDto> season = [Episode("A", 1, 1, 49, played: true), Episode("B", 1, 2, 49, played: true), Episode("C", 1, 3, 52), Episode("D", 1, 4, 51)];

		Assert.Equal("4 episodes · 3 h 21 min · 2 watched", CinematicLabels.SeasonSummary(season));
		Assert.Equal("1 episode · 49 min", CinematicLabels.SeasonSummary([Episode("A", 1, 1, 49)]));
		Assert.Equal("", CinematicLabels.SeasonSummary([]));
	}

	[Fact]
	public void MetaFacts_PlainText_TheBadgesApart()
	{
		var film = new BaseItemDto
		{
			Type = BaseItemDto_Type.Movie, ProductionYear = 1997, OfficialRating = "R", RunTimeTicks = 118 * TimeSpan.TicksPerMinute, Genres = ["Thriller"],
			MediaStreams = [new MediaStream { Type = MediaStream_Type.Video, Width = 3840, Height = 2160 }],
		};

		Assert.Equal("1997 · R · 1h 58m · Thriller", CinematicLabels.MetaFacts(film));
		Assert.Equal(["4K"], CinematicLabels.MetaBadges(film));
	}

	// ---- the title art's face ------------------------------------------------------------------------------------------

	[Fact]
	public void StyleFor_IsTheSameEveryTime_ForTheSameTitle_AndUsesEveryFace()
	{
		Assert.Equal(CinematicLabels.StyleFor("The Iron Circus"), CinematicLabels.StyleFor("The Iron Circus"));
		Assert.Equal(CinematicLabels.StyleFor("the iron circus"), CinematicLabels.StyleFor("THE IRON CIRCUS"));

		var titles = Enumerable.Range(0, 64).Select(i => $"Title {i}");
		Assert.Equal(4, titles.Select(CinematicLabels.StyleFor).Distinct().Count());
	}

	// ---- which picture a still shows -----------------------------------------------------------------------------------

	[Fact]
	public void StillPath_AFilm_ThumbThenBackdropThenPrimary()
	{
		var id = Guid.NewGuid();
		var film = new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie, ImageTags = new BaseItemDto_ImageTags(), BackdropImageTags = ["bd"] };
		film.ImageTags.AdditionalData["Primary"] = "poster";
		Assert.Equal($"/Items/{id:D}/Images/Backdrop?tag=bd&maxWidth=640", CinematicArtwork.StillPath(film, 640));

		film.ImageTags.AdditionalData["Thumb"] = "th";
		Assert.Equal($"/Items/{id:D}/Images/Thumb?tag=th&maxWidth=640", CinematicArtwork.StillPath(film, 640));

		film.ImageTags.AdditionalData.Remove("Thumb");
		film.BackdropImageTags = [];
		Assert.Equal($"/Items/{id:D}/Images/Primary?tag=poster&maxWidth=640", CinematicArtwork.StillPath(film, 640));
	}

	[Fact]
	public void StillPath_AnEpisode_ItsShowsPictureOnAShelf_ItsOwnOnItsSeason()
	{
		var series = Guid.NewGuid();
		var episode = Episode("Sawdust", 1, 3);
		episode.ImageTags = new BaseItemDto_ImageTags();
		episode.ImageTags.AdditionalData["Primary"] = "frame";
		episode.ParentThumbItemId = series;
		episode.ParentThumbImageTag = "series-thumb";

		Assert.Equal($"/Items/{series:D}/Images/Thumb?tag=series-thumb&maxWidth=520", CinematicArtwork.StillPath(episode, 520));
		Assert.Equal($"/Items/{episode.Id:D}/Images/Primary?tag=frame&maxWidth=520", CinematicArtwork.StillPath(episode, 520, episodeOwnFirst: true));
		Assert.Null(CinematicArtwork.StillPath(new BaseItemDto { Id = Guid.NewGuid() }, 520));
	}
}
