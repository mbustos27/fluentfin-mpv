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
	public void UpNextLine_TellsNarratorWhatItPlays()
	{
		var line = EditorialHome.UpNext([Vm(Episode("The Iron Circus", 1, 3, "Sawdust", 52, 8))], [], null)[0];

		Assert.Equal("Resume The Iron Circus S1 E3 Sawdust, 44 min left", line.Name);
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
		Assert.Equal("Next up · S2 E1", EditorialHome.Kicker(Episode("A Signal in Winter", 2, 1, "Thaw"), resume: false, nextUp: true));
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
		Assert.All(keys.Where(k => k is not ("TopBarStyle" or "RowHeaderTemplate" or "PosterTemplate" or "StillTemplate" or "PageHeaderStyle" or "SignInBackdropTemplate")),
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
		Assert.All(dark.Keys, k => Assert.StartsWith("Editorial", k));
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
