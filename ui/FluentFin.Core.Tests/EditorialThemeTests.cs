using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.3.0, the Editorial theme: the view-model side of its Home (Up next, the hero tile, the genre strip), its cards'
/// words, and (EditorialXamlTests) its dictionary's keys and contrast.
/// </summary>
public sealed class EditorialThemeTests
{
	private static BaseItemDto Movie(string name, long runMinutes = 100, long positionMinutes = 0, string[]? genres = null, DateTimeOffset? added = null, int year = 2020) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, ProductionYear = year, OfficialRating = "PG-13",
		RunTimeTicks = runMinutes * TimeSpan.TicksPerMinute, Genres = genres?.ToList(), DateCreated = added,
		UserData = new UserItemDataDto { PlaybackPositionTicks = positionMinutes * TimeSpan.TicksPerMinute, PlayedPercentage = runMinutes > 0 ? 100.0 * positionMinutes / runMinutes : 0 },
	};

	private static BaseItemDto Episode(string series, int season, int number, string name, long runMinutes = 50, long positionMinutes = 0) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Episode, SeriesName = series, ParentIndexNumber = season, IndexNumber = number,
		RunTimeTicks = runMinutes * TimeSpan.TicksPerMinute,
		UserData = new UserItemDataDto { PlaybackPositionTicks = positionMinutes * TimeSpan.TicksPerMinute, PlayedPercentage = 100.0 * positionMinutes / runMinutes },
	};

	private static BaseItemViewModel Vm(BaseItemDto dto) => BaseItemViewModel.FromDto(dto);

	// ---- Up next and the hero tile ----------------------------------------------------------------------------------

	[Fact]
	public void UpNext_IsInProgressThenNextEpisodes_Numbered_WithoutTheHero()
	{
		var film = Vm(Movie("Burning Horizons", 118, 46));
		var lighthouses = Vm(Episode("Iron Lighthouses", 3, 4, "The Lamp Room", 50, 19));
		var circus = Vm(Episode("The Iron Circus", 1, 3, "Sawdust", 52, 8));
		var winter = Vm(Episode("A Signal in Winter", 2, 1, "Thaw", 45));

		var lines = EditorialHome.UpNext([film, lighthouses, circus], [winter], hero: film);

		Assert.Equal(["Iron Lighthouses", "The Iron Circus", "A Signal in Winter"], lines.Select(l => l.Title));
		Assert.Equal(["01", "02", "03"], lines.Select(l => l.Number));
		Assert.Equal("S3 E4 · The Lamp Room · 31 min left", lines[0].Detail);
		Assert.Equal("S2 E1 · Thaw · Next episode", lines[2].Detail);
		Assert.True(lines[0].IsResume && lines[0].HasProgress);
		Assert.False(lines[2].IsResume || lines[2].HasProgress);
	}

	[Fact]
	public void UpNext_ListsAnItemOnce_WhenAnOldServerPutsItInBothRows()
	{
		var episode = Episode("Copper Rivers", 6, 2, "Dry Creek", 45, 30);
		var lines = EditorialHome.UpNext([Vm(episode)], [Vm(episode)], hero: null);

		Assert.Single(lines);
	}

	[Fact]
	public void UpNextLine_TellsNarratorWhatItShows()
	{
		var line = EditorialHome.UpNext([Vm(Episode("The Iron Circus", 1, 3, "Sawdust", 52, 8))], [], null)[0];

		// Nixie 1.4.0: the line opens its item (as Home's next-up item does in every theme), so its name is not "Resume ..."
		Assert.Equal("The Iron Circus S1 E3 Sawdust, 44 min left", line.Name);
	}

	[Fact]
	public void TheHero_IsWhatWasWatchedLast_ElseTheSharedHero_ElseNextUp()
	{
		var resume = Vm(Movie("Burning Horizons", 118, 46));
		var next = Vm(Episode("A Signal in Winter", 2, 1, "Thaw"));
		var slideDto = Movie("Glass Canyon");
		var slide = new HeroSlide(slideDto, "Glass Canyon", "", "", false, Guid.NewGuid(), "tag", null, null);

		Assert.Same(resume, EditorialHome.HeroItem([resume], [slide], [next]));
		Assert.Equal(slideDto.Id, EditorialHome.HeroItem([], [slide], [next])!.Dto.Id);
		Assert.Same(next, EditorialHome.HeroItem([], [], [next]));
		Assert.Null(EditorialHome.HeroItem([], [], []));
	}

	[Theory]
	[InlineData(118, 46, "1 h 12 m left")]
	[InlineData(52, 8, "44 min left")]
	[InlineData(52, 52, "")]
	[InlineData(0, 0, "")]
	public void TimeLeft_InTheConceptsWords(long runMinutes, long position, string expected) =>
		Assert.Equal(expected, EditorialHome.TimeLeft(Movie("x", runMinutes, position)));

	[Fact]
	public void HeroWords_KickerAndFacts()
	{
		var film = Movie("Burning Horizons", 118, 46, ["Thriller", "Drama"], year: 1997);
		film.OfficialRating = "R";

		Assert.Equal("Continue watching · Film", EditorialHome.Kicker(film, resume: true, nextUp: false));
		Assert.Equal("Up next · S2 E1", EditorialHome.Kicker(Episode("A Signal in Winter", 2, 1, "Thaw"), resume: false, nextUp: true));
		Assert.Equal("1997 · R · 1h 58m · Thriller", EditorialHome.HeroMeta(film));
		Assert.Equal("1997 · R · 1h 58m · Thriller, Drama", EditorialHome.HeroMeta(film, genres: 2));
	}

	// ---- the genre strip --------------------------------------------------------------------------------------------

	[Fact]
	public void Genres_MostTitlesFirst_EachTitleOncePerGenre()
	{
		var titles = new[]
		{
			Movie("a", genres: ["Sci-fi", "Drama"]), Movie("b", genres: ["Sci-fi", "sci-fi"]), Movie("c", genres: ["Drama"]),
			Movie("d", genres: ["Comedy"]), Movie("e", genres: ["Sci-fi"]),
		};

		var genres = EditorialGenreStrip.Count(titles);

		Assert.Equal([("Sci-fi", 3), ("Drama", 2), ("Comedy", 1)], genres.Select(g => (g.Name, g.Count)));
		Assert.Equal("Sci-fi, 3 titles", genres[0].AutomationName);
	}

	[Fact]
	public void AGenresShelf_IsNewestFirst_AndItsCaptionSaysWhatItHolds()
	{
		var old = Movie("Old", genres: ["Sci-fi"], added: DateTimeOffset.Parse("2020-01-01"));
		var recent = Movie("Recent", genres: ["Sci-fi"], added: DateTimeOffset.Parse("2026-01-01"));
		var show = new BaseItemDto { Id = Guid.NewGuid(), Name = "Show", Type = BaseItemDto_Type.Series, Genres = ["Sci-fi"], DateCreated = DateTimeOffset.Parse("2025-01-01") };

		var shelf = EditorialGenreStrip.Shelf([old, recent, show, Movie("Other", genres: ["Drama"])], "sci-fi");

		Assert.Equal(["Recent", "Show", "Old"], shelf.Select(t => t.Name));
		Assert.Equal("3 films and shows · newest first", EditorialGenreStrip.CaptionFor(shelf));
		Assert.Equal("1 film · newest first", EditorialGenreStrip.CaptionFor([old]));
		Assert.Equal("1 show · newest first", EditorialGenreStrip.CaptionFor([show]));
	}

	[Fact]
	public void TheStrip_KeepsItsPick_ElseTheRemembered_ElseTheLargest()
	{
		var strip = new EditorialGenreStrip();
		var titles = new[] { Movie("a", genres: ["Drama"]), Movie("b", genres: ["Drama"]), Movie("c", genres: ["Comedy"]) };

		strip.SetTitles(titles);
		Assert.Equal("Drama", strip.Selected);
		Assert.Equal(2, strip.Items.Count);

		strip.Select("Comedy");
		strip.SetTitles(titles); // a refresh keeps the pick
		Assert.Equal("Comedy", strip.Selected);

		var other = new EditorialGenreStrip();
		other.SetTitles(titles, remembered: "Comedy");
		Assert.Equal("Comedy", other.Selected);

		other.SetTitles([Movie("d", genres: ["Western"])]); // its genre is gone: the largest
		Assert.Equal("Western", other.Selected);
	}

	[Fact]
	public void GenreTabs_ThatFit_ThePickedOneAlwaysAmongThem()
	{
		double[] widths = [100, 100, 100, 100, 100];

		Assert.Equal([0, 1, 2], EditorialGenreFit.Fit(widths, 30, 360, selected: 0));
		Assert.Equal([0, 1, 4], EditorialGenreFit.Fit(widths, 30, 360, selected: 4));
		Assert.Equal([0], EditorialGenreFit.Fit(widths, 30, 50, selected: 0)); // at least one
		Assert.Equal([0, 1, 2, 3, 4], EditorialGenreFit.Fit(widths, 30, 2000, selected: 3));
	}

	[Fact]
	public async Task TheGenreSource_AsksEachLibraryOnce_WhileFresh()
	{
		EditorialGenreSource.Clear();
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(c => c.BaseUrl).Returns("http://test");
		client.SetupGet(c => c.UserId).Returns(Guid.NewGuid());
		client.Setup(c => c.GetItems(library, false)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [Movie("a", genres: ["Drama"])] });

		var now = DateTimeOffset.Parse("2026-11-17T20:00:00Z");
		EditorialGenreSource.Now = () => now;
		try
		{
			Assert.Single(await EditorialGenreSource.Titles(client.Object, [library]));
			Assert.Single(await EditorialGenreSource.Titles(client.Object, [library]));
			client.Verify(c => c.GetItems(library, false), Times.Once);

			now += EditorialGenreSource.FreshFor;
			await EditorialGenreSource.Titles(client.Object, [library]);
			client.Verify(c => c.GetItems(library, false), Times.Exactly(2));
		}
		finally
		{
			EditorialGenreSource.Now = () => DateTimeOffset.UtcNow;
			EditorialGenreSource.Clear();
		}
	}

	// ---- the cards' words -------------------------------------------------------------------------------------------

	[Fact]
	public void PosterWords()
	{
		var series = new BaseItemDto { Name = "Electric Ravenmoor", Type = BaseItemDto_Type.Series, ChildCount = 2, ProductionYear = 2021 };

		Assert.Equal("2020 · 1h 40m", EditorialCards.PosterMeta(Movie("Glass Canyon")));
		Assert.Equal("2 seasons", EditorialCards.PosterMeta(series));
		Assert.Equal("S1 E3 · Sawdust", EditorialCards.PosterMeta(Episode("The Iron Circus", 1, 3, "Sawdust")));
		Assert.Equal("The Iron Circus", EditorialCards.Title(Episode("The Iron Circus", 1, 3, "Sawdust")));
	}

	[Fact]
	public void StillWords()
	{
		var watched = Episode("The Iron Circus", 1, 1, "Opening Night", 49);
		watched.UserData!.Played = true;
		watched.PremiereDate = DateTimeOffset.Parse("2022-03-14");
		var going = Episode("The Iron Circus", 1, 3, "Sawdust", 52, 8);
		going.PremiereDate = DateTimeOffset.Parse("2022-03-28");

		Assert.Equal("01", EditorialCards.StillNumber(watched));
		Assert.Equal("49 min · 14 Mar 2022 · Watched", EditorialCards.StillMeta(watched));
		Assert.Equal("52 min · 28 Mar 2022 · 44 min left", EditorialCards.StillMeta(going));
	}

	[Fact]
	public void TheDetailsPeopleLine()
	{
		var film = Movie("Glass Canyon");
		film.People =
		[
			new BaseItemPerson { Name = "Ines Calder", Type = BaseItemPerson_Type.Actor },
			new BaseItemPerson { Name = "Abe Morrow", Type = BaseItemPerson_Type.Actor },
			new BaseItemPerson { Name = "Kit Yuen", Type = BaseItemPerson_Type.Actor },
			new BaseItemPerson { Name = "Fourth", Type = BaseItemPerson_Type.Actor },
			new BaseItemPerson { Name = "Sam Okonkwo", Type = BaseItemPerson_Type.Director },
		];

		Assert.Equal("With Ines Calder, Abe Morrow, Kit Yuen · Director Sam Okonkwo", EditorialCards.People(film));
		Assert.Equal("", EditorialCards.People(Movie("Nobody")));
	}
}

/// <summary>
/// Nixie 1.3.0, the Editorial theme's dictionary: the keys every theme defines, a value of each of its own keys in the dark,
/// light and high-contrast dictionaries (high contrast on system colours only), and its text colours at 4.5:1 or more on
/// the grounds they sit on.
/// </summary>
public sealed class EditorialXamlTests
{
	private static readonly System.Xml.Linq.XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly System.Xml.Linq.XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	private static string Dir => Path.Combine(RepoRoot, "FluentFin", "Themes", "Editorial");

	// Nixie 1.3.1: Home from far down the library goes to the top first (the keys matrix: Home stayed on the last title once
	// the library sorted by sort name, its first rows long virtualized away), and asks for the focus again if it did not take
	[Fact]
	public void LibraryHome_ScrollsToTheTop_ThenFocuses_AndAsksAgain()
	{
		var page = File.ReadAllText(Path.Combine(Dir, "Views", "EditorialLibraryPage.xaml.cs")).Replace("\r\n", "\n");
		var keys = page[page.IndexOf("private void Grid_KeyDown", StringComparison.Ordinal)..];
		var home = keys.IndexOf("if (key == \"Home\")", StringComparison.Ordinal);
		Assert.True(home > 0 && home < keys.IndexOf("GridScroller.ChangeView(null, 0, null, disableAnimation: true);", StringComparison.Ordinal));
		Assert.Contains("if (!FocusIndex(target, FocusState.Keyboard))", keys);
	}

	private static System.Xml.Linq.XDocument Load(string name) => System.Xml.Linq.XDocument.Load(Path.Combine(Dir, name));

	private static IEnumerable<string> TopKeys(System.Xml.Linq.XDocument doc) =>
		doc.Root!.Elements().Where(e => e.Name.LocalName != "ResourceDictionary.ThemeDictionaries" && e.Name.LocalName != "ResourceDictionary.MergedDictionaries")
			.Select(e => (string?)e.Attribute(X + "Key")).OfType<string>();

	private static Dictionary<string, System.Xml.Linq.XElement> ThemeDictionary(System.Xml.Linq.XDocument doc, string theme) =>
		doc.Root!.Element(P + "ResourceDictionary.ThemeDictionaries")!.Elements(P + "ResourceDictionary").Single(d => (string?)d.Attribute(X + "Key") == theme)
			.Elements().Where(e => e.Attribute(X + "Key") is not null).ToDictionary(e => (string)e.Attribute(X + "Key")!);

	[Fact]
	public void TheContractsKeys_AreDefined()
	{
		var keys = TopKeys(Load("Editorial.xaml")).Concat(TopKeys(Load("Cards.xaml"))).ToHashSet();

		foreach (var key in new[] { "TopBarStyle", "RowHeaderTemplate", "PosterTemplate", "StillTemplate", "PageHeaderStyle" })
		{
			Assert.Contains(key, keys);
		}

		// keys only this theme uses carry its prefix
		Assert.All(keys.Where(k => k is not ("TopBarStyle" or "RowHeaderTemplate" or "PosterTemplate" or "StillTemplate" or "PageHeaderStyle" or "SignInBackdropTemplate"
			or "ThemeOptionsTemplate" or "PlayerTopBarStyle" or "PlayerBottomBarStyle" or "TrailerFrameTemplate"
			// Nixie 1.4.0: the page grid's contract keys (PageGridXamlTests)
			or "PageGutterNarrow" or "PageGutterWide" or "PageTitleY" or "PageTitleUppercase")),
			k => Assert.StartsWith("Editorial", k));
	}

	[Fact]
	public void EveryThemedKey_HasADarkLightAndHighContrastValue_TheLastOnSystemColours()
	{
		var doc = Load("Editorial.xaml");
		var dark = ThemeDictionary(doc, "Dark");
		var light = ThemeDictionary(doc, "Light");
		var contrast = ThemeDictionary(doc, "HighContrast");

		Assert.Equal(dark.Keys.Order(), light.Keys.Order());
		Assert.Equal(dark.Keys.Order(), contrast.Keys.Order());
		Assert.All(dark.Keys.Where(k => k != "PageGroundBrush"), k => Assert.StartsWith("Editorial", k)); // 1.4.0: the shared pages' ground
		foreach (var (key, element) in contrast)
		{
			var text = element.ToString();
			Assert.True(element.Name.LocalName == "Double" || text.Contains("SystemColor", StringComparison.Ordinal) || text.Contains("Transparent", StringComparison.Ordinal),
				$"{key}: high contrast uses the system colours ({text})");
			Assert.DoesNotContain("Gradient", element.Name.LocalName, StringComparison.Ordinal);
		}

		// no glow in high contrast
		Assert.Equal("0", contrast["EditorialGlowOpacity"].Value);
		Assert.Equal("0", contrast["EditorialSoftGlowOpacity"].Value);
	}

	[Theory]
	[InlineData("Dark", "EditorialGroundBrush")]
	[InlineData("Dark", "EditorialPanelBrush")]
	[InlineData("Light", "EditorialGroundBrush")]
	[InlineData("Light", "EditorialPanelBrush")]
	public void TheTextColours_Reach45To1_OnTheirGrounds(string theme, string ground)
	{
		var editorial = ThemeDictionary(Load("Editorial.xaml"), theme);
		var glass = ThemeDictionary(System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot, "FluentFin", "Styles", "Glass.xaml")), theme);
		string Colour(Dictionary<string, System.Xml.Linq.XElement> d, string key) => (string)d[key].Attribute("Color")!;

		var back = Colour(editorial, ground);
		foreach (var (dict, key) in new[]
		{
			(glass, "GlassTextPrimaryBrush"), (glass, "GlassTextSecondaryBrush"), (editorial, "EditorialNumberBrush"), (editorial, "EditorialKickerBrush"),
			(editorial, "EditorialHighlightTextBrush"), (editorial, "EditorialLitTextBrush"), (editorial, "EditorialChipSelectedTextBrush"),
		})
		{
			var ratio = Contrast(Colour(dict, key), back);
			Assert.True(ratio >= 4.5, $"{theme} {key} on {ground}: {ratio:0.00}:1");
		}
	}

	[Fact]
	public void APickedChipsText_Reaches45To1_OnItsWash()
	{
		foreach (var theme in new[] { "Dark", "Light" })
		{
			var d = ThemeDictionary(Load("Editorial.xaml"), theme);
			var wash = Over((string)d["EditorialChipSelectedFillBrush"].Attribute("Color")!, (string)d["EditorialGroundBrush"].Attribute("Color")!);
			var ratio = Contrast((string)d["EditorialChipSelectedTextBrush"].Attribute("Color")!, wash);
			Assert.True(ratio >= 4.5, $"{theme}: {ratio:0.00}:1");
		}
	}

	// "#AARRGGBB" over "#RRGGBB"
	private static string Over(string top, string back)
	{
		var (a, tr, tg, tb) = Parse(top);
		var (_, br, bg, bb) = Parse(back);
		int Mix(double t, double b) => (int)Math.Round(t * a + b * (1 - a));
		return $"#{Mix(tr, br):X2}{Mix(tg, bg):X2}{Mix(tb, bb):X2}";
	}

	private static (double A, double R, double G, double B) Parse(string hex)
	{
		hex = hex.TrimStart('#');
		var a = hex.Length == 8 ? Convert.ToInt32(hex[..2], 16) / 255.0 : 1.0;
		var rgb = hex.Length == 8 ? hex[2..] : hex;
		return (a, Convert.ToInt32(rgb[..2], 16), Convert.ToInt32(rgb[2..4], 16), Convert.ToInt32(rgb[4..6], 16));
	}

	private static double Contrast(string a, string b)
	{
		static double Lum(string hex)
		{
			var (_, r, g, b) = Parse(hex);
			static double L(double c) { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
			return 0.2126 * L(r) + 0.7152 * L(g) + 0.0722 * L(b);
		}

		var (x, y) = (Lum(a), Lum(b));
		return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
	}

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
}

/// <summary>Nixie 1.3.0, the Editorial theme's series and season pages: the Seasons panel, the season grid and the header's words.</summary>
public sealed class EditorialSeriesTests
{
	private static BaseItemDto Season(int number, int? year = null, DateTimeOffset? added = null) => new()
	{
		Id = Guid.NewGuid(), Name = number == 0 ? "Specials" : $"Season {number}", Type = BaseItemDto_Type.Season, IndexNumber = number, ProductionYear = year, DateCreated = added,
	};

	private static BaseItemDto Episode(BaseItemDto season, int number, bool played = false, long runMinutes = 50, long positionMinutes = 0) => new()
	{
		Id = Guid.NewGuid(), Name = $"Ep {number}", Type = BaseItemDto_Type.Episode, SeasonId = season.Id, ParentIndexNumber = season.IndexNumber, IndexNumber = number,
		RunTimeTicks = runMinutes * TimeSpan.TicksPerMinute,
		UserData = new UserItemDataDto { Played = played, PlaybackPositionTicks = positionMinutes * TimeSpan.TicksPerMinute },
	};

	private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-17T20:00:00Z");

	[Fact]
	public void TheSeasonsPanel_CountsWhatIsWatched_PerSeason_SpecialsLast()
	{
		var s1 = Season(1, 2022);
		var s2 = Season(2, 2023);
		var s3 = Season(3, 2025, added: Now.AddDays(-3));
		var specials = Season(0);
		var episodes = new[] { Episode(s1, 1, true), Episode(s1, 2, true), Episode(s1, 3), Episode(s1, 4), Episode(s2, 1, true), Episode(s2, 2, true), Episode(s3, 1) };

		var lines = EditorialSeries.Lines([specials, s2, s1, s3], episodes, current: s1.Id, Now);

		Assert.Equal(["01", "02", "03", "SP"], lines.Select(l => l.Number));
		Assert.Equal(["2 of 4 watched", "Watched", "New · Not started", ""], lines.Select(l => l.Progress));
		Assert.Equal("2022 · 4 episodes", lines[0].Meta);
		Assert.True(lines[0].IsCurrent && lines[0].HasFraction);
		Assert.False(lines[1].HasFraction); // a whole season watched says so in words; no bar
		Assert.Equal("Season 1, 2 of 4 watched", lines[0].AutomationName);
	}

	[Fact]
	public void WithoutTheEpisodes_ASeasonsOwnCountsAreUsed()
	{
		var season = Season(1, 2022);
		season.ChildCount = 8;
		season.UserData = new UserItemDataDto { UnplayedItemCount = 6 };

		Assert.Equal("2 of 8 watched", EditorialSeries.Lines([season], null, null, Now)[0].Progress);
	}

	[Fact]
	public void TheSeriesWords()
	{
		var s1 = Season(1);
		var episodes = new[] { Episode(s1, 1, true), Episode(s1, 2, positionMinutes: 6, runMinutes: 50), Episode(s1, 3) };

		Assert.Equal("1 of 3 watched", EditorialSeries.Watched(episodes));
		Assert.Equal("44 min left", EditorialSeries.PlayLeft(episodes));
		Assert.Equal("", EditorialSeries.PlayLeft([Episode(s1, 1, true), Episode(s1, 2)]));
		Assert.Equal("SERIES · 3 SEASONS · 24 EPISODES", EditorialSeries.Kicker("Series", 3, 24));
		Assert.Equal("3 episodes · 2 h 30 m", EditorialSeries.SeasonCaption(episodes));
	}

	[Fact]
	public void TheGrid_HidesWatched_TurnsOver_AndLandsOnTheEpisodeToWatch()
	{
		var s1 = Season(1);
		var e1 = Episode(s1, 1, true);
		var e2 = Episode(s1, 2);
		var e3 = Episode(s1, 3);

		Assert.Equal([e2, e3], EditorialSeries.Shown([e1, e2, e3], hideWatched: true, newestFirst: false));
		Assert.Equal([e3, e2, e1], EditorialSeries.Shown([e1, e2, e3], hideWatched: false, newestFirst: true));
		Assert.Equal(1, EditorialSeries.FocusIndex([e1, e2, e3]));
		Assert.Equal(0, EditorialSeries.FocusIndex([e1]));
	}

	[Theory]
	[InlineData(1808, 4)]
	[InlineData(1500, 4)]
	[InlineData(1020, 3)]
	[InlineData(900, 2)]
	public void TheGrid_FourAcrossAt1920(double width, int columns) => Assert.Equal(columns, EditorialEpisodeColumns.For(width).Columns);

	[Fact]
	public void TheHeadersTitle_ShrinksAsItGrows()
	{
		Assert.Equal(96, EditorialItemTitle.Size("The Iron Circus", narrow: false));
		Assert.Equal(64, EditorialItemTitle.Size("The Iron Circus", narrow: true));
		Assert.True(EditorialItemTitle.Size("The Extraordinarily Long Title of a Film", narrow: false) < 60);
	}

	[Fact]
	public void TheHeadersFacts()
	{
		var series = new BaseItemDto { Type = BaseItemDto_Type.Series, ProductionYear = 2022, EndDate = DateTimeOffset.Parse("2025-06-01"), OfficialRating = "TV-14", Genres = ["Mystery", "Drama", "Western"], CommunityRating = 8.4f };
		var running = new BaseItemDto { Type = BaseItemDto_Type.Series, ProductionYear = 1991, Status = "Continuing" };
		var episode = new BaseItemDto { Type = BaseItemDto_Type.Episode, PremiereDate = DateTimeOffset.Parse("2022-03-28"), OfficialRating = "TV-14", RunTimeTicks = 52 * TimeSpan.TicksPerMinute, Genres = ["Drama"] };

		Assert.Equal("2022 – 2025 · TV-14 · Mystery, Drama · ★ 8.4", EditorialItemTitle.Meta(series));
		Assert.Equal("1991 – present", EditorialItemTitle.Meta(running));
		Assert.Equal("28 Mar 2022 · TV-14 · 52m", EditorialItemTitle.Meta(episode));
	}
}

/// <summary>Nixie 1.3.0, the Editorial theme's Library: the grid split by letter, details in place, the keyboard and the words.</summary>
public sealed class EditorialLibraryTests
{
	private static BaseItemViewModel Title(string name, bool played = false, DateTimeOffset? added = null) =>
		BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, DateCreated = added, UserData = new UserItemDataDto { Played = played } });

	private static readonly BaseItemViewModel[] Titles =
	[
		Title("Alpha"), Title("Blue Mesa"), Title("Bluebird"), Title("Borrowed Light"), Title("Bridge"), Title("Bright"), Title("Cinder"),
	];

	[Fact]
	public void ByLetter_EachLetterIsASection_ItsRowsCut()
	{
		var lines = EditorialLibrary.Lines(Titles, columns: 3, byLetter: true, opened: null, BaseItemDto_CollectionType.Movies);

		Assert.Equal(
			["A 1 film", "row 0:1", "B 5 films", "row 1:3", "row 4:2", "C 1 film", "row 6:1"],
			lines.Select(l => l switch
			{
				EditorialSectionLine s => $"{s.Letter} {s.Caption}",
				EditorialRowLine r => $"row {r.FirstIndex}:{r.Items.Count}",
				_ => "details",
			}));
		Assert.Equal("B, 5 films", ((EditorialSectionLine)lines[2]).AutomationName);
		Assert.Equal([(2, 7), (3, 7), (4, 7)], ((EditorialRowLine)lines[3]).Cells.Select(c => (c.Position, c.Size)));
	}

	[Fact]
	public void TheOpenedTitlesDetails_FollowItsRow()
	{
		var lines = EditorialLibrary.Lines(Titles, columns: 3, byLetter: true, opened: Titles[2].Id, BaseItemDto_CollectionType.Movies);

		Assert.IsType<EditorialRowLine>(lines[3]);
		Assert.Same(Titles[2], Assert.IsType<EditorialDetailsLine>(lines[4]).Item);
	}

	[Fact]
	public void NotByLetter_JustRows()
	{
		var lines = EditorialLibrary.Lines(Titles, columns: 4, byLetter: false, opened: null, null);

		Assert.All(lines, l => Assert.IsType<EditorialRowLine>(l));
		Assert.Equal(2, lines.Count);
	}

	[Fact]
	public void TheKeys_MoveAlongTheLibrary_AndByColumnAcrossSections()
	{
		var lines = EditorialLibrary.Lines(Titles, columns: 3, byLetter: true, opened: Titles[3].Id, BaseItemDto_CollectionType.Movies);
		int? Move(int from, string key) => EditorialLibrary.Move(lines, from, Titles.Length, key);

		Assert.Equal(3, Move(2, "Right"));      // across a row's end
		Assert.Null(Move(6, "Right"));          // the library's end
		Assert.Equal(1, Move(0, "Down"));       // Alpha down to Blue Mesa, over the B heading
		Assert.Equal(5, Move(3, "Down"));       // Borrowed Light (column 2) down over its open details to Bright, the shorter row's last
		Assert.Equal(4, Move(1, "Down"));       // Blue Mesa (column 0) down to Bridge
		Assert.Equal(6, Move(5, "Down"));       // Bright (column 1) to Cinder, the shorter row's last
		Assert.Equal(4, Move(6, "Up"));         // Cinder (column 0) up to Bridge, over the C heading
		Assert.Null(Move(0, "Up"));             // out of the grid
		Assert.Equal((3, 1), EditorialLibrary.Find(lines, 2));
		Assert.Equal(2, EditorialLibrary.SectionOf(lines, "B"));
	}

	/// <summary>Nixie 1.3.1: Page Up and Page Down go a page of rows in the column, as the other themes' grids; at the ends, as far as there are rows.</summary>
	[Fact]
	public void PageUpAndPageDown_GoAPageOfRows()
	{
		var lines = EditorialLibrary.Lines(Titles, columns: 3, byLetter: true, opened: null, BaseItemDto_CollectionType.Movies);
		int? Move(int from, string key, int rows) => EditorialLibrary.Move(lines, from, Titles.Length, key, rows);

		Assert.Equal(EditorialLibrary.Move(lines, 0, Titles.Length, "Down"), Move(0, "PageDown", 1));
		Assert.Equal(Move(Move(0, "PageDown", 1)!.Value, "PageDown", 1), Move(0, "PageDown", 2));
		Assert.Equal(6, Move(0, "PageDown", 50));      // as far as there are rows: the last row
		Assert.Equal(0, Move(6, "PageUp", 50));        // and back to the first
		Assert.Null(Move(0, "PageUp", 3));             // nothing above the first row (the bar above takes Up, not Page Up)
	}

	[Fact]
	public void TheColumns_TwelveAcrossAt1920()
	{
		Assert.Equal(12, EditorialLibrary.Columns(1808));
		Assert.Equal(7, EditorialLibrary.Columns(1020, 122));
		Assert.Equal(2, EditorialLibrary.Columns(100));
	}

	[Fact]
	public void TheLibrarysWords()
	{
		var now = DateTimeOffset.Parse("2026-11-17T20:00:00Z");
		BaseItemViewModel[] titles = [Title("a", played: true), Title("b", added: now.AddDays(-3)), Title("c")];

		Assert.Equal("3 films · 2 unwatched · 1 added this month", EditorialLibrary.Summary(titles, BaseItemDto_CollectionType.Movies, now));
		Assert.Equal("1 show", EditorialLibrary.Count(1, BaseItemDto_CollectionType.Tvshows));
		// Nixie 1.4.0 (item 8): the shared words, the key and its direction
		Assert.Equal("Title · A to Z", EditorialLibrary.SortWords(ItemSortBy.Name, SortOrder.Ascending));
		Assert.Equal("Title · Z to A", EditorialLibrary.SortWords(ItemSortBy.Name, SortOrder.Descending));
		Assert.Equal("Date added · newest first", EditorialLibrary.SortWords(ItemSortBy.DateCreated, SortOrder.Descending));
	}

	// Nixie 1.3.1 (the UX review's E-M4, WCAG 2.5.3 "label in name"): the Sort button's name starts with the words it shows
	[Fact]
	public void TheSortButtonsName_StartsWithItsVisibleLabel()
	{
		var name = EditorialLibrary.SortButtonName(ItemSortBy.Name, SortOrder.Ascending, false, LibraryBrowse.SortButtonName(ItemSortBy.Name, SortOrder.Ascending));

		Assert.Equal("Title, A to Z, Sort", name);
		// the words shown ("Community rating · highest first") start the name, the dot aside
		static string Words(string text) => string.Join(' ', text.Split([' ', ',', '·'], StringSplitOptions.RemoveEmptyEntries));
		Assert.StartsWith(Words(EditorialLibrary.SortWords(ItemSortBy.CommunityRating, SortOrder.Descending)),
			Words(EditorialLibrary.SortButtonName(ItemSortBy.CommunityRating, SortOrder.Descending, false, LibraryBrowse.SortButtonName(ItemSortBy.CommunityRating, SortOrder.Descending))));
		Assert.Equal("Playlist order, Sort", EditorialLibrary.SortButtonName(ItemSortBy.Name, SortOrder.Ascending, true, "Sort: Playlist order"));
	}
}

/// <summary>Nixie 1.3.0, Editorial's Up next: the next episodes keep their places on screen.</summary>
public sealed class EditorialUpNextTests
{
	private static BaseItemViewModel Ep(string series, bool resume) => BaseItemViewModel.FromDto(new BaseItemDto
	{
		Id = Guid.NewGuid(), Name = "Ep", Type = BaseItemDto_Type.Episode, SeriesName = series, ParentIndexNumber = 1, IndexNumber = 1,
		RunTimeTicks = 50 * TimeSpan.TicksPerMinute, UserData = new UserItemDataDto { PlaybackPositionTicks = resume ? 10 * TimeSpan.TicksPerMinute : 0 },
	});

	[Fact]
	public void ALongContinueWatching_StillLeavesNextEpisodesOnScreen()
	{
		var resuming = Enumerable.Range(0, 9).Select(i => Ep($"R{i}", true)).ToList();
		var next = Enumerable.Range(0, 4).Select(i => Ep($"N{i}", false)).ToList();

		var wide = EditorialHome.UpNext(resuming, next, hero: null);
		Assert.Equal(["R0", "R1", "R2", "N0", "N1"], wide.Take(5).Select(l => l.Title));
		Assert.Equal(13, wide.Count);

		var narrow = EditorialHome.UpNext(resuming, next, hero: null, lines: 3);
		Assert.Equal(["R0", "N0", "N1"], narrow.Take(3).Select(l => l.Title));
	}
}
