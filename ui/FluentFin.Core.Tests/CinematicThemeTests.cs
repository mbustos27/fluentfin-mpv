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
	public void ItemKicker_SaysWhatThePageIs_TheTitleArtNamesTheShow()
	{
		Assert.Equal("Series · 3 seasons", CinematicLabels.ItemKicker(new BaseItemDto { Type = BaseItemDto_Type.Series }, seasons: 3));
		Assert.Equal("Series · 1 season", CinematicLabels.ItemKicker(new BaseItemDto { Type = BaseItemDto_Type.Series }, seasons: 1));
		Assert.Equal("Film", CinematicLabels.ItemKicker(new BaseItemDto { Type = BaseItemDto_Type.Movie }));
		Assert.Equal("Season", CinematicLabels.ItemKicker(new BaseItemDto { Type = BaseItemDto_Type.Season }));
		Assert.Equal("Episode · S1 E3", CinematicLabels.ItemKicker(Episode("Sawdust", 1, 3)));
	}

	[Fact]
	public void Credits_StarringThreeAtMost_AndWhoMadeIt()
	{
		var film = new BaseItemDto
		{
			Type = BaseItemDto_Type.Movie,
			People = [new() { Name = "A", Type = BaseItemPerson_Type.Actor }, new() { Name = "B", Type = BaseItemPerson_Type.Actor }, new() { Name = "C", Type = BaseItemPerson_Type.Actor }, new() { Name = "D", Type = BaseItemPerson_Type.Actor }, new() { Name = "E", Type = BaseItemPerson_Type.Director }],
		};
		Assert.Equal([("Starring", "A, B, C"), ("Directed by", "E")], CinematicLabels.Credits(film));
		Assert.Empty(CinematicLabels.Credits(new BaseItemDto()));
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

/// <summary>
/// Nixie 1.3.0, the Cinematic theme's gates read from its XAML (Themes\Cinematic): text laid on artwork keeps 4.5:1 over the
/// worst case, a white picture, through the scrims and plates it sits on, in the dark and the light theme; every view has
/// one Primary button.
/// </summary>
public sealed class CinematicThemeXamlTests
{
	private static readonly System.Xml.Linq.XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly System.Xml.Linq.XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}

	private static string Cinematic(string file) => Path.Combine(RepoRoot, "FluentFin", "Themes", "Cinematic", file);

	private static System.Xml.Linq.XElement Theme(string theme) =>
		System.Xml.Linq.XDocument.Load(Cinematic("Cards.xaml")).Root!.Element(Presentation + "ResourceDictionary.ThemeDictionaries")!
			.Elements().Single(d => (string?)d.Attribute(Xaml + "Key") == theme);

	private static System.Xml.Linq.XElement Brush(string theme, string key) =>
		Theme(theme).Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == key);

	private static (double R, double G, double B, double A) Parse(string hex)
	{
		hex = hex.TrimStart('#');
		var a = hex.Length == 8 ? Convert.ToInt32(hex[..2], 16) / 255.0 : 1;
		var rgb = hex.Length == 8 ? hex[2..] : hex;
		return (Convert.ToInt32(rgb[..2], 16) / 255.0, Convert.ToInt32(rgb[2..4], 16) / 255.0, Convert.ToInt32(rgb[4..6], 16) / 255.0, a);
	}

	/// <summary>The brush's colour at <paramref name="offset"/> (a gradient's stops interpolated; a solid brush's colour).</summary>
	private static (double R, double G, double B, double A) At(System.Xml.Linq.XElement brush, double offset)
	{
		if (brush.Name.LocalName == "SolidColorBrush")
		{
			return Parse((string)brush.Attribute("Color")!);
		}

		var stops = brush.Elements(Presentation + "GradientStop").Select(s => (Offset: double.Parse((string)s.Attribute("Offset")!, System.Globalization.CultureInfo.InvariantCulture), Color: Parse((string)s.Attribute("Color")!))).OrderBy(s => s.Offset).ToList();
		if (offset <= stops[0].Offset)
		{
			return stops[0].Color;
		}

		for (var i = 1; i < stops.Count; i++)
		{
			if (offset <= stops[i].Offset)
			{
				var t = (offset - stops[i - 1].Offset) / (stops[i].Offset - stops[i - 1].Offset);
				var (a, b) = (stops[i - 1].Color, stops[i].Color);
				return (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
			}
		}

		return stops[^1].Color;
	}

	private static (double R, double G, double B) Over((double R, double G, double B, double A) top, (double R, double G, double B) under) =>
		(top.R * top.A + under.R * (1 - top.A), top.G * top.A + under.G * (1 - top.A), top.B * top.A + under.B * (1 - top.A));

	private static double Luminance((double R, double G, double B) c)
	{
		static double Channel(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
		return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
	}

	private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
	{
		var (l1, l2) = (Luminance(a), Luminance(b));
		return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
	}

	private static readonly (double, double, double) White = (1, 1, 1);

	private static (double R, double G, double B) Text(string theme, string key)
	{
		var c = At(Brush(theme, key), 0);
		return (c.R, c.G, c.B);
	}

	[Theory]
	[InlineData("Default", "CinematicOnArtTextBrush", 0.38)]   // the overview's longest line ends at about 38 % of the width
	[InlineData("Default", "CinematicOnArtSecondaryBrush", 0.26)] // the facts' line
	[InlineData("Default", "CinematicKickerBrush", 0.2)]
	[InlineData("Default", "CinematicNumberBrush", 0.3)]       // the time left beside Resume
	[InlineData("Light", "CinematicOnArtTextBrush", 0.38)]
	[InlineData("Light", "CinematicOnArtSecondaryBrush", 0.26)]
	[InlineData("Light", "CinematicKickerBrush", 0.2)]
	public void TheHerosWords_Keep45_OverAWhitePicture(string theme, string text, double reach)
	{
		var scrim = Brush(theme, "CinematicHeroSideScrimBrush");
		for (var x = 0.0; x <= reach + 1e-9; x += 0.02)
		{
			var ratio = Contrast(Text(theme, text), Over(At(scrim, x), White));
			Assert.True(ratio >= 4.5, $"{theme} {text} at {x:0.00} of the width: {ratio:0.00}:1");
		}
	}

	/// <summary>
	/// The Up next shelf's title and count sit on the hero's foot, where no side scrim is (the right half too): the page's
	/// secondary text (Glass.xaml's GlassTextSecondaryBrush) keeps 4.5:1 there over the worst picture: white under the dark
	/// foot, black under the light one. The shelf's header starts at about 65 % of the first screen.
	/// </summary>
	[Theory]
	[InlineData("Default", "#D3BFC1", 0.65)]
	[InlineData("Light", "#633A41", 0.65)]
	public void TheShelfHeader_Keeps45_OnTheHerosFoot(string theme, string secondary, double from)
	{
		var foot = Brush(theme, "CinematicHeroFootScrimBrush");
		var worst = theme == "Light" ? (0.0, 0.0, 0.0) : White;
		var text = Parse(secondary);
		for (var y = from; y <= 1.0 + 1e-9; y += 0.05)
		{
			var ratio = Contrast((text.R, text.G, text.B), Over(At(foot, y), worst));
			Assert.True(ratio >= 4.5, $"{theme} at {y:0.00} of the first screen: {ratio:0.00}:1");
		}
	}

	[Theory]
	[InlineData("Default")]
	[InlineData("Light")]
	public void TheTitleArtOnAStill_Keeps45_OverAWhitePicture(string theme)
	{
		// the title art sits in the band's lower part: from 60 % of the band down (Controls\CinematicStill.xaml)
		var shade = Brush(theme, "CinematicArtShadeBrush");
		for (var y = 0.6; y <= 1.0 + 1e-9; y += 0.05)
		{
			var ratio = Contrast(Text(theme, "CinematicOnArtTextBrush"), Over(At(shade, y), White));
			Assert.True(ratio >= 4.5, $"{theme}: title art at {y:0.00} of the band: {ratio:0.00}:1");
		}
	}

	[Theory]
	[InlineData("Default", "CinematicBadgeFillBrush")]
	[InlineData("Default", "CinematicCreditsBackingBrush")]
	[InlineData("Default", "CinematicPlayerPanelBrush")]
	[InlineData("Light", "CinematicBadgeFillBrush")]
	[InlineData("Light", "CinematicCreditsBackingBrush")]
	[InlineData("Light", "CinematicPlayerPanelBrush")]
	public void TextOnAPlate_Keeps45_OverAWhitePicture(string theme, string plate)
	{
		var ratio = Contrast(Text(theme, "CinematicOnArtTextBrush"), Over(At(Brush(theme, plate), 0), White));
		Assert.True(ratio >= 4.5, $"{theme} on {plate}: {ratio:0.00}:1");
	}

	[Theory]
	[InlineData("Views/CinematicHomePage.xaml", "Controls/CinematicHero.xaml")]
	[InlineData("Views/CinematicSeriesPage.xaml", "Controls/CinematicItemStage.xaml")]
	[InlineData("Views/CinematicSeasonPage.xaml", "Controls/CinematicItemStage.xaml")]
	[InlineData("Views/CinematicMoviePage.xaml", "Controls/CinematicItemStage.xaml")]
	[InlineData("Views/CinematicEpisodePage.xaml", "Controls/CinematicItemStage.xaml")]
	[InlineData("Views/CinematicLibraryPage.xaml", "Controls/CinematicPreviewStage.xaml")]
	public void EveryView_HasOnePrimary(string view, string stage)
	{
		static int Primaries(string file) =>
			System.Xml.Linq.XDocument.Load(file).Descendants().Count(e => ((string?)e.Attribute("Style"))?.Contains("NixiePrimary", StringComparison.Ordinal) == true);

		Assert.Equal(1, Primaries(Cinematic(view)) + Primaries(Cinematic(stage)));
	}

	[Fact]
	public void TheStillsAndThePlayerPanel_HaveNoPrimaryOfTheirOwn()
	{
		foreach (var control in new[] { "Controls/CinematicStill.xaml", "Controls/CinematicPlayerPanel.xaml", "Controls/CinematicItemStage.xaml" })
		{
			Assert.DoesNotContain("NixiePrimary", File.ReadAllText(Cinematic(control)));
		}
	}
}
