using System.Globalization;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-4, stream t): what a Made by Nixie tile and a Home row show, in every theme: the words the templates bind
/// (<see cref="NixieTileWords"/>), and that each template binds them (the Collections page's tile, shared by the four
/// themes; Home's Nixie rows in Classic, Cinematic, Drive-in and Editorial). The off-screen check
/// (tools\ui-harness\offscreen\then-180-tiles.ps1) reads them from the running app.
/// </summary>
public sealed class NixieTileTemplates180Tests : IDisposable
{
	private readonly CultureInfo _was = CultureInfo.CurrentCulture;

	public NixieTileTemplates180Tests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");

	public void Dispose() => CultureInfo.CurrentCulture = _was;

	private static SmartCollection Heists(bool badges = true) =>
		new("keyword:heist", SmartCollectionKind.Keyword, "Heists", "Titles with TMDb's keyword “heist”", false, [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())])
		{
			Only = IndexKind.Movie,
			Reason = ReasonLine.Of("TMDb keyword “heist”", ReasonCount.Titles(5, IndexKind.Movie, 5)),
			Badges = badges ? [new CollectionBadge("Best Picture winner", "Best Picture", true, 1), new CollectionBadge("2 Palme d'Or winners", "Palme d'Or", true, 2)] : [],
		};

	// 1.7.x's saved Home page: no reason line, no badges
	private static SmartCollection Older() =>
		new("pick:highly-rated", SmartCollectionKind.HighlyRated, "Acclaimed, not seen yet", "w", false, [Guid.NewGuid(), Guid.NewGuid()]) { Only = IndexKind.Movie };

	[Fact]
	public void A_Nixie_tile_says_its_facts_then_its_count_and_wears_its_badges()
	{
		var heists = Heists();

		Assert.Equal("TMDb keyword “heist”", NixieTileWords.TileFacts("5 films", heists));
		Assert.Equal("5 films\nnone watched", NixieTileWords.TileCount(heists));
		Assert.Equal(("Best Picture winner", "2 Palme d'Or winners", ""), (NixieTileWords.Badge(heists, 0), NixieTileWords.Badge(heists, 1), NixieTileWords.Badge(heists, 2)));
		Assert.Equal((true, true, false), (NixieTileWords.HasBadge(heists, 0), NixieTileWords.HasBadge(heists, 1), NixieTileWords.HasBadge(heists, 2)));
		Assert.Equal("Heists, made by Nixie, keyword, TMDb keyword “heist”, 5 films, none watched, Best Picture winner, 2 Palme d'Or winners",
			NixieTileWords.TileName("Heists, made by Nixie, keyword, 5 films", heists));
	}

	[Fact]
	public void A_server_tile_keeps_its_line_and_an_older_saved_tile_says_its_count()
	{
		Assert.Equal("6 films · 3 left", NixieTileWords.TileFacts("6 films · 3 left", null));
		Assert.Equal("", NixieTileWords.TileCount(null));
		Assert.Equal("", NixieTileWords.Badge(null, 0));
		Assert.Equal("Marvel, collection, 6 films, 3 left", NixieTileWords.TileName("Marvel, collection, 6 films, 3 left", null));

		var spotlight = Heists() with { Reason = ReasonLine.Of("Director", ReasonCount.Titles(8, IndexKind.Movie, 8)) with { Until = new DateOnly(2026, 11, 29) } };
		Assert.Equal("8 films\nnone watched\nuntil Sun 29 Nov", NixieTileWords.TileCount(spotlight)); // each part on its own line, never cut off

		var older = Older();
		Assert.Equal("", NixieTileWords.TileFacts("2 films", older));
		Assert.Equal("2 films", NixieTileWords.TileCount(older));
		Assert.False(NixieTileWords.HasBadge(older, 0));
	}

	[Theory]
	[InlineData(1204, 1150, "1,204 titles\n1,150 unwatched")]
	[InlineData(40, 40, "40 titles\nnone watched")]
	[InlineData(3, 0, "3 titles\nall watched")]
	public void A_long_count_puts_how_many_are_watched_on_a_line_of_its_own(int count, int unwatched, string lines)
	{
		// "1,204 titles, 1,150 unwatched" is wider than a 168 px tile: its end was cut off with "…"
		var big = Heists() with { Only = null, Reason = ReasonLine.Of("x", ReasonCount.Titles(count, null, unwatched)) };

		Assert.Equal(lines, NixieTileWords.TileCount(big));
		Assert.Equal("2 of 7 films\nwatched", NixieTileWords.TileCount(big with { Reason = ReasonLine.Of("x", ReasonCount.Watched(2, 7, IndexKind.Movie)) }));
		Assert.Equal("2 of 7 films watched", ReasonCount.Watched(2, 7, IndexKind.Movie).Text); // one line where there is room
		Assert.True(NixieTileWords.TileCount(big with { Reason = big.Reason! with { Until = new DateOnly(2026, 11, 29) } }).Split('\n').Length <= NixieTileWords.TileCountLines);
	}

	[Theory]
	[InlineData(1.0, 124)]
	[InlineData(1.25, 154)]
	[InlineData(1.5, 184)]
	[InlineData(2.25, 274)]
	[InlineData(0.5, 124)] // never smaller than at 100 %
	public void The_tiles_words_grow_with_Windows_text_size_so_nothing_is_clipped(double textScale, double height)
	{
		// 125 % and 150 % text: a fixed 108 px clipped the count lines (two lines of title and facts took it all)
		Assert.Equal(height, NixieTileWords.WordsHeight(textScale));
		var lines = (2 * 20 + (2 + NixieTileWords.TileCountLines) * 16) * Math.Max(1, textScale);
		Assert.True(NixieTileWords.WordsHeight(textScale) >= lines + 4, $"{textScale}: {lines} px of lines");
	}

	[Fact]
	public void A_saved_filter_tile_says_its_library_and_what_it_keeps_never_0_titles()
	{
		// the user's own filter: its titles aren't counted until it opens (1.7 said "0 titles" on every one)
		var saved = new SmartCollection("saved:r3-x", SmartCollectionKind.Saved, "Late night", "Your filter in Movies: Horror · Unwatched", false, [])
		{
			Filter = new SavedFilter(Guid.NewGuid(), "Movies", Unwatched: true, Genres: ["Horror"]),
		};

		Assert.Equal("Movies · Horror · Unwatched", NixieTileWords.TileFacts("0 titles", saved));
		Assert.Equal("", NixieTileWords.TileCount(saved));
		Assert.Equal("Late night, saved filter, Movies, Horror, Unwatched", NixieTileWords.TileName("Late night, made by Nixie, saved filter, 0 titles", saved));
		Assert.Equal("Horror · Unwatched", NixieTileWords.TileFacts("", saved with { Filter = saved.Filter! with { LibraryName = " " } }));
	}

	[Fact]
	public void A_Home_row_says_the_reason_and_the_badges_beside_its_kicker()
	{
		Assert.Equal("TMDb keyword “heist” · 5 films, none watched · Best Picture winner · 2 Palme d'Or winners", NixieTileWords.RowLine(Heists()));
		Assert.Equal("Made by Nixie · TMDb keyword “heist” · 5 films, none watched", NixieTileWords.RowSubtitle("Made by Nixie", Heists(badges: false)));
		Assert.Equal("Made by Nixie · 2 films", NixieTileWords.RowSubtitle("Made by Nixie", Older()));
		Assert.Equal("Made by Nixie", NixieTileWords.RowSubtitle("Made by Nixie", null));
	}

	// ---------- the templates bind them

	private static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!File.Exists(Path.Combine(dir, "ui", "FluentFin.sln")))
		{
			dir = Path.GetDirectoryName(dir)!;
		}

		return dir;
	}

	private static string Xaml(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), "ui", "FluentFin", .. parts]));

	// the element that carries x:Name="name" (its start tag)
	private static string Element(string xaml, string name, int from = 0)
	{
		var at = xaml.IndexOf($"x:Name=\"{name}\"", from, StringComparison.Ordinal);
		Assert.True(at >= 0, $"{name} is in the template");
		var start = xaml.LastIndexOf('<', at);
		var end = xaml.IndexOf('>', at);
		return xaml[start..(end + 1)];
	}

	[Fact]
	public void The_Collections_tile_shows_the_title_then_the_facts_then_the_count_at_one_height_and_the_badges_over_its_art()
	{
		var xaml = Xaml("Views", "CollectionsPage.xaml");
		var template = xaml[xaml.IndexOf("x:Key=\"CollectionTileTemplate\"", StringComparison.Ordinal)..];
		template = template[..template.IndexOf("</DataTemplate>", StringComparison.Ordinal)];

		Assert.Contains("AutomationProperties.Name=\"{x:Bind sc:NixieTileWords.TileName(AutomationName, Nixie), Mode=OneWay}\"", template);
		var facts = Element(template, "TileFacts");
		Assert.Contains("Text=\"{x:Bind sc:NixieTileWords.TileFacts(Detail, Nixie), Mode=OneWay}\"", facts);
		Assert.Contains("MaxLines=\"2\"", facts);
		var count = Element(template, "TileCount");
		Assert.Contains("Text=\"{x:Bind sc:NixieTileWords.TileCount(Nixie), Mode=OneWay}\"", count);
		Assert.Contains("TextWrapping=\"NoWrap\"", count);
		Assert.Contains($"MaxLines=\"{NixieTileWords.TileCountLines}\"", count); // the count, the watched part, the end date
		// in order: the title, the facts, the count; the words one height on every tile (no jump, one grid cell size)
		Assert.True(template.IndexOf("Text=\"{x:Bind Title}\"", StringComparison.Ordinal) < template.IndexOf("x:Name=\"TileFacts\"", StringComparison.Ordinal));
		Assert.True(template.IndexOf("x:Name=\"TileFacts\"", StringComparison.Ordinal) < template.IndexOf("x:Name=\"TileCount\"", StringComparison.Ordinal));
		Assert.Contains("Height=\"{x:Bind local:NixieTileShown.WordsHeight}\"", template); // grown with Windows' text size
		// two badges at most, each shown only when there is one, over the art (before the words)
		Assert.Contains("Visibility=\"{x:Bind local:NixieTileShown.Badge(Nixie, 0), Mode=OneWay}\"", template);
		Assert.Contains("Visibility=\"{x:Bind local:NixieTileShown.Badge(Nixie, 1), Mode=OneWay}\"", template);
		Assert.Contains("Text=\"{x:Bind sc:NixieTileWords.Badge(Nixie, 1), Mode=OneWay}\"", template);
		Assert.DoesNotContain("Badge(Nixie, 2)", template);
		// a badge too long for one line ("3 BAFTA Best Film winners", "12 Best Picture nominees") wraps, never "…": its chip
		// grows over the art, the tile keeps its size
		var chips = template[template.IndexOf("x:Name=\"TileBadges\"", StringComparison.Ordinal)..template.IndexOf("</StackPanel>", template.IndexOf("x:Name=\"TileBadges\"", StringComparison.Ordinal), StringComparison.Ordinal)];
		Assert.Equal(2, chips.Split("TextWrapping=\"WrapWholeWords\"").Length - 1);
		Assert.Equal(2, chips.Split("MaxLines=\"2\"").Length - 1);
		Assert.Equal(2, chips.Split("Height=\"Auto\"").Length - 1); // not the count chip's fixed height
		Assert.True(template.IndexOf("x:Name=\"TileBadges\"", StringComparison.Ordinal) < template.IndexOf("Text=\"{x:Bind Title}\"", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("Views/HomePage.xaml")]
	[InlineData("Themes/Cinematic/Views/CinematicHomePage.xaml")]
	public void Classic_and_Cinematic_Home_rows_say_the_reason_beside_the_kicker(string page)
	{
		var xaml = Xaml(page.Split('/'));
		var template = xaml[xaml.IndexOf("x:DataType=\"vm:NixieHomeRowViewModel\"", StringComparison.Ordinal)..];
		var reason = Element(template, "NixieRowReason");

		Assert.Contains("Text=\"{x:Bind sc:NixieTileWords.RowLine(Collection), Mode=OneWay}\"", reason); // 1.8 re-review: a row takes a new lineup's collection in place
		Assert.Contains("TextTrimming=\"CharacterEllipsis\"", reason);
		Assert.Contains("TextWrapping=\"NoWrap\"", reason);
		Assert.Contains("Grid.Column=\"1\"", reason); // after the kicker, in what the kicker leaves
		Assert.True(template.IndexOf("Text=\"MADE BY NIXIE\"", StringComparison.Ordinal) < template.IndexOf("x:Name=\"NixieRowReason\"", StringComparison.Ordinal));
		Assert.Contains("xmlns:sc=\"using:FluentFin.Core.SmartCollections\"", xaml);
	}

	[Theory]
	[InlineData("Themes/DriveIn/Views/DriveInHomePage.xaml", "dc:DriveInRows.Subtitle=")]
	[InlineData("Themes/Editorial/Views/EditorialHomePage.xaml", "Caption=")]
	public void Drive_in_and_Editorial_Home_rows_say_it_in_their_line_beside_the_title(string page, string attribute)
	{
		var xaml = Xaml(page.Split('/'));
		var template = xaml[xaml.IndexOf("x:DataType=\"vm:NixieHomeRowViewModel\"", StringComparison.Ordinal)..];
		template = template[..template.IndexOf("</DataTemplate>", StringComparison.Ordinal)];

		Assert.Contains($"{attribute}\"{{x:Bind sc:NixieTileWords.RowSubtitle(Kicker, Collection), Mode=OneWay}}\"", template); // 1.8 re-review: in place
		Assert.Contains("xmlns:sc=\"using:FluentFin.Core.SmartCollections\"", xaml);
	}

	[Fact]
	public void Drive_ins_row_line_is_cut_short_at_the_headers_end_not_run_on_under_See_all()
	{
		var code = Xaml("Themes", "DriveIn", "Controls", "DriveInRows.cs");

		Assert.Contains("TextTrimming = TextTrimming.CharacterEllipsis", code);
		Assert.Contains("var line = new Grid", code);
		Assert.DoesNotContain("var line = new StackPanel", code);
	}
}
