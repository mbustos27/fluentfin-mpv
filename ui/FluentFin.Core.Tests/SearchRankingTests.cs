using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Search;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.5 (search; docs/research/search.md): the PC's ranking. A whole title beats a title's start, which beats all the
/// words, which beats some of them; accents, case, apostrophes and punctuation count as equal; "The" or "A" never blocks a
/// match; original and sort titles count; episodes lead to their show; the best match of any kind leads; the groups follow
/// their best match; weak matches wait behind "Show N more"; the chips show one kind at a time.
/// </summary>
public sealed class SearchRankingTests
{
	private static BaseItemDto Item(string name, BaseItemDto_Type type, string? original = null, string? sort = null, Guid? series = null, string? seriesName = null) =>
		new() { Id = Guid.NewGuid(), Name = name, Type = type, OriginalTitle = original, SortName = sort, SeriesId = series, SeriesName = seriesName };

	private static SearchTier Tier(string query, string title) => new SearchQuery(query).Match(title).Tier;

	// ---- the fixture of tricky titles: what each query must find, and how well

	[Theory]
	// apostrophes, either way and curly
	[InlineData("it's always sunny", "It's Always Sunny in Philadelphia", SearchTier.Start)]
	[InlineData("its always sunny", "It's Always Sunny in Philadelphia", SearchTier.Start)]
	[InlineData("it’s always sunny", "It's Always Sunny in Philadelphia", SearchTier.Start)]
	[InlineData("it s always sunny", "It's Always Sunny in Philadelphia", SearchTier.Start)]
	[InlineData("always sunny", "It's Always Sunny in Philadelphia", SearchTier.AllWords)]
	[InlineData("sunny philadelphia", "It's Always Sunny in Philadelphia", SearchTier.AllWords)]
	[InlineData("schindler's list", "Schindler's List", SearchTier.Whole)]
	[InlineData("schindlers list", "Schindler's List", SearchTier.Whole)]
	// "The", "A" at the start
	[InlineData("matrix", "The Matrix", SearchTier.Whole)]
	[InlineData("the matrix", "The Matrix", SearchTier.Whole)]
	[InlineData("matrix", "The Matrix Reloaded", SearchTier.Start)]
	[InlineData("reloaded", "The Matrix Reloaded", SearchTier.AllWords)]
	[InlineData("the office", "Office Space", SearchTier.Start)]
	[InlineData("quiet place", "A Quiet Place", SearchTier.Whole)]
	[InlineData("the", "The Matrix", SearchTier.Start)]
	// numbers
	[InlineData("2001", "2001: A Space Odyssey", SearchTier.Start)]
	[InlineData("toy story 2", "Toy Story 2", SearchTier.Whole)]
	[InlineData("toy story 2", "Toy Story", SearchTier.SomeWords)]
	[InlineData("se7en", "Se7en", SearchTier.Whole)]
	[InlineData("seven", "Se7en", SearchTier.None)]
	// punctuation in the title
	[InlineData("mash", "M*A*S*H", SearchTier.Whole)]
	[InlineData("m*a*s*h", "M*A*S*H", SearchTier.Whole)]
	[InlineData("walle", "WALL·E", SearchTier.Whole)]
	[InlineData("wall-e", "WALL·E", SearchTier.Whole)]
	[InlineData("wall e", "WALL·E", SearchTier.Whole)]
	[InlineData("spiderman", "Spider-Man: No Way Home", SearchTier.Start)]
	[InlineData("no way home spiderman", "Spider-Man: No Way Home", SearchTier.AllWords)]
	[InlineData("spy family", "SPY x FAMILY", SearchTier.AllWords)]
	// accents and case, unicode
	[InlineData("amelie", "Amélie", SearchTier.Whole)]
	[InlineData("AMÉLIE", "Amélie", SearchTier.Whole)]
	[InlineData("pokemon", "Pokémon: The First Movie", SearchTier.Start)]
	[InlineData("oslo", "Øslo Ünderground", SearchTier.Start)]
	[InlineData("crouching tiger", "Crouching Tiger, Hidden Dragon", SearchTier.Start)]
	[InlineData("千と千尋", "千と千尋の神隠し", SearchTier.Start)]
	// one-word titles, and words out of order or skipped
	[InlineData("up", "Up", SearchTier.Whole)]
	[InlineData("up", "Upgrade", SearchTier.Start)]
	[InlineData("up", "Pixar Shorts: Up Close", SearchTier.AllWords)]
	[InlineData("it", "It", SearchTier.Whole)]
	[InlineData("night dead", "Night of the Living Dead", SearchTier.AllWords)]
	[InlineData("dead night", "Night of the Living Dead", SearchTier.AllWords)]
	[InlineData("tel", "Sintel", SearchTier.SomeWords)]
	[InlineData("always", "Always Be My Maybe", SearchTier.Start)]
	[InlineData("it's always sunny", "Always Be My Maybe", SearchTier.SomeWords)]
	[InlineData("it's always sunny", "Sunny", SearchTier.SomeWords)]
	[InlineData("it's always sunny", "Blade Runner", SearchTier.None)]
	public void TheFixture(string query, string title, SearchTier tier)
	{
		Assert.Equal(tier, Tier(query, title));
	}

	[Fact]
	public void TheTiers_RankWholeThenStartThenAllWordsThenSome()
	{
		var ranked = SearchRanking.Order("it's always sunny",
		[
			Item("Sunny", BaseItemDto_Type.Movie),
			Item("Always Sunny Days", BaseItemDto_Type.Movie),
			Item("It's Always Sunny in Philadelphia", BaseItemDto_Type.Series),
			Item("It's Always Sunny", BaseItemDto_Type.Movie),
			Item("It Follows", BaseItemDto_Type.Movie),
		]);

		Assert.Equal(["It's Always Sunny", "It's Always Sunny in Philadelphia", "Always Sunny Days", "Sunny", "It Follows"], ranked.Select(r => r.Item.Name));
		Assert.Equal([SearchTier.Whole, SearchTier.Start, SearchTier.SomeWords, SearchTier.SomeWords, SearchTier.None], ranked.Select(r => r.Match.Tier));
	}

	[Fact]
	public void WithinATier_TheCloserTitleWins_ThenTheServersOrder()
	{
		// 12.4 put The Matrix Reloaded first for "matrix" (a word start scored above the exact title after "The")
		var ranked = SearchRanking.Order("matrix", [Item("The Matrix Reloaded", BaseItemDto_Type.Movie), Item("The Matrix Revolutions", BaseItemDto_Type.Movie), Item("The Matrix", BaseItemDto_Type.Movie)]);
		Assert.Equal(["The Matrix", "The Matrix Reloaded", "The Matrix Revolutions"], ranked.Select(r => r.Item.Name));

		var same = SearchRanking.Order("dune", [Item("Dune", BaseItemDto_Type.Movie), Item("Dune", BaseItemDto_Type.Movie)]);
		Assert.Equal([0, 1], same.Select(r => r.Order));
	}

	[Theory]
	// review (M2): a title that starts with the query's whole words beats one where the query ends inside a word
	[InlineData("star", "Star Wars: A New Hope", "Starman")]
	[InlineData("love", "Love Actually", "The Lovely Bones")]
	[InlineData("man", "Man of Steel", "Mandy")]
	[InlineData("spiderman", "Spider-Man: Across the Spider-Verse", "Spidermania")]
	[InlineData("the office", "The Office", "The Officer")]
	[InlineData("it", "It Follows", "Italian Job")]
	public void AStartOnWholeWords_BeatsAStartInsideAWord(string query, string whole, string inside)
	{
		var ranked = SearchRanking.Order(query, [Item(inside, BaseItemDto_Type.Movie), Item(whole, BaseItemDto_Type.Movie)]);
		Assert.Equal([whole, inside], ranked.Select(r => r.Item.Name));
		Assert.All(ranked, r => Assert.True(r.Match.Tier <= SearchTier.Start, r.Item.Name));
	}

	[Fact]
	public void OriginalAndSortTitles_CountToo()
	{
		var amelie = Item("Amélie", BaseItemDto_Type.Movie, original: "Le Fabuleux Destin d'Amélie Poulain", sort: "amelie");
		Assert.Equal(SearchTier.AllWords, new SearchQuery("fabuleux destin d'amelie").Match(amelie).Tier);
		Assert.Equal(SearchTier.Start, new SearchQuery("le fabuleux destin").Match(amelie).Tier);
		Assert.Equal(SearchTier.AllWords, new SearchQuery("destin amélie").Match(amelie).Tier);

		// an administrator's sort name ("Star Wars 4"), and the padded numbers of the server's sort names
		var hope = Item("Star Wars: A New Hope", BaseItemDto_Type.Movie, sort: "star wars 0000000004");
		Assert.Equal(SearchTier.Whole, new SearchQuery("star wars 4").Match(hope).Tier);
		Assert.Equal("se7en", SearchText.Unpad("se0000000007en"));
		Assert.Equal("2001 space odyssey", SearchText.Unpad("0000002001 space odyssey"));
		Assert.Equal("0", SearchText.Unpad("0000000000"));

		// the name wins a tie with the other titles
		var both = Item("Matrix", BaseItemDto_Type.Movie, original: "Matrix");
		Assert.Equal(1, new SearchQuery("matrix").Match(both).Closeness);
	}

	[Theory]
	[InlineData("It's Always Sunny", "its always sunny")]
	[InlineData("WALL·E", "wall e")]
	[InlineData("  M*A*S*H  ", "m a s h")]
	[InlineData("Amélie", "amelie")]
	[InlineData("Ærø Straße", "aero strasse")]
	[InlineData("¡Three Amigos!", "three amigos")]
	[InlineData("", "")]
	[InlineData(null, "")]
	[InlineData("'", "")]
	public void Fold(string? text, string folded)
	{
		Assert.Equal(folded, SearchText.Fold(text));
	}

	[Fact]
	public void Fold_ByRune_KeepsOtherScriptsMarks_DropsLatinGreekCyrillicAccents()
	{
		// review (m7): a letter outside the BMP stays; kana voicing is part of the word; accents go
		Assert.Equal("𠮷野家", SearchText.Fold("𠮷野家"));
		Assert.NotEqual(SearchText.Fold("ガンダム"), SearchText.Fold("カンタム"));
		Assert.Equal("ガンダム", SearchText.Fold("ｶﾞﾝﾀﾞﾑ")); // half-width kana and voicing, as full-width
		Assert.Equal(SearchTier.None, new SearchQuery("カンタム").Match("機動戦士ガンダム").Tier);
		Assert.Equal(SearchTier.Whole, new SearchQuery("ガンダム").Match("ガンダム").Tier);
		Assert.Equal("αθηνα", SearchText.Fold("Αθήνα"));
		Assert.Equal("иога", SearchText.Fold("Йога"));
		Assert.Equal("cafe", SearchText.Fold("Café"));
		Assert.Equal("हिंदी", SearchText.Fold("हिंदी")); // a Devanagari vowel sign is not an accent
	}

	[Fact]
	public async Task AQueryWithNothingToMatch_SendsNoRequest()
	{
		// review (m6): "%" and "!!!" fold to nothing; the server's LIKE would answer "%" with everything
		var client = new Mock<IJellyfinClient>(MockBehavior.Strict);
		foreach (var q in (string[])["%", "!!!", "  ", "'"])
		{
			Assert.Equal(0, (await SearchRunner.Run(client.Object, q))?.Count);
			var suggestion = SearchSuggestions.For(q, await SearchRunner.Suggest(client.Object, q));
			Assert.False(Assert.IsType<SearchMessage>(Assert.Single(suggestion)).CanRetry);
		}

		var vm = new SearchResultsViewModel(client.Object);
		await vm.OnNavigatedTo("%");
		Assert.True(vm.Results.IsEmpty);
		Assert.Equal("No results for “%”", vm.Results.Title);
	}

	[Fact]
	public void AnEpisodeMatch_LiftsAShowAlreadyFound_AboveAWeakerShow()
	{
		// review (m4): the show came back by itself (a loose server), but its own title matches nothing; its episode's
		// whole title must rank it, above a show that matches only one word
		var spy = new BaseItemDto { Id = Guid.NewGuid(), Name = "Spy x Family", Type = BaseItemDto_Type.Series };
		var strix = Item("Strix Variations", BaseItemDto_Type.Series);
		var episode = Item("Operation Strix", BaseItemDto_Type.Episode, series: spy.Id, seriesName: "Spy x Family");

		var results = SearchRanking.Build("operation strix", Answers(strix, spy, episode));

		var shows = results.GroupOf(SearchKind.Shows)!;
		Assert.Equal(["Spy x Family"], shows.Strong.Select(s => s.Item.Name));
		Assert.Same(episode, shows.Strong[0].ViaEpisode);
		Assert.Equal(SearchTier.Whole, shows.Strong[0].Match.Tier);
		Assert.Equal(["Strix Variations"], shows.Weak.Select(s => s.Item.Name));
	}

	[Theory]
	[InlineData("night dead", "night")]
	[InlineData("steel tears", "steel")]
	[InlineData("the office", "office")]
	[InlineData("its always sunny", "always")]
	[InlineData("matrix", null)]
	[InlineData("a b", null)]
	[InlineData("", null)]
	public void TheLongestWord_ForTheRetry(string query, string? word)
	{
		Assert.Equal(word, SearchText.LongestWord(query));
	}

	[Fact]
	public void AnEmptyQuery_MatchesNothing()
	{
		Assert.Equal(SearchMatch.Nothing, new SearchQuery("  ").Match("Anything"));
		Assert.Equal(SearchMatch.Nothing, new SearchQuery("--").Match("Anything"));
		Assert.Equal(SearchMatch.Nothing, new SearchQuery("x").Match((string?)null));
		Assert.Empty(SearchRanking.Build("  ", new Dictionary<SearchKind, IReadOnlyList<BaseItemDto>> { [SearchKind.Movies] = [Item("Up", BaseItemDto_Type.Movie)] }).Groups);
	}

	// ---- the page's model

	private static Dictionary<SearchKind, IReadOnlyList<BaseItemDto>> Answers(params BaseItemDto[] items) =>
		SearchKinds.All.ToDictionary(k => k.Kind, k => (IReadOnlyList<BaseItemDto>)[.. items.Where(i => i.Type == k.Type)]);

	/// <summary>The owner's case: the show, and 295 films a loose search plugin found for any of the words.</summary>
	private static BaseItemDto[] Sunny()
	{
		var films = Enumerable.Range(1, 295).Select(i => Item(i % 3 == 0 ? $"Sunny Day {i}" : i % 3 == 1 ? $"Always Here {i}" : $"It Came {i}", BaseItemDto_Type.Movie));
		return [.. films, Item("It's Always Sunny in Philadelphia", BaseItemDto_Type.Series)];
	}

	[Fact]
	public void TheOwnersCase_TheShowLeads_AndTheFilmsWaitBehindShowMore()
	{
		var results = SearchRanking.Build("it's always sunny", Answers(Sunny()));

		Assert.Equal("It's Always Sunny in Philadelphia", results.Top?.Item.Name);
		Assert.Equal(["Shows", "Movies"], results.Groups.Select(g => g.Kind.Title));
		var movies = results.GroupOf(SearchKind.Movies)!;
		Assert.Empty(movies.Strong);
		Assert.Equal(295, movies.Weak.Count);
	}

	[Fact]
	public void NothingStrong_ShowsEverything_NothingFolded()
	{
		var results = SearchRanking.Build("tel", Answers(Item("Sintel", BaseItemDto_Type.Movie), Item("Hotel Ruins", BaseItemDto_Type.Series)));

		Assert.All(results.Groups, g => Assert.Empty(g.Weak));
		Assert.Equal(2, results.Count);
		Assert.Null(results.Top); // nothing whole or at the start: no top result
	}

	[Fact]
	public void TheGroups_FollowTheirBestMatch_TiesInTheChipsOrder()
	{
		var results = SearchRanking.Build("dune", Answers(
			Item("Dune Messiah", BaseItemDto_Type.Episode),
			Item("Dune", BaseItemDto_Type.BoxSet),
			Item("Dune", BaseItemDto_Type.Movie),
			Item("Children of Dune", BaseItemDto_Type.Series)));

		// Movies and Collections tie on a whole title: the fixed order (Shows, Movies, Episodes, Collections) decides
		Assert.Equal(["Movies", "Collections", "Episodes", "Shows"], results.Groups.Select(g => g.Kind.Title));
		Assert.Equal(BaseItemDto_Type.Movie, results.Top?.Item.Type);
	}

	[Fact]
	public void People_AreIn_OtherKinds_AreLeftOut()
	{
		var results = SearchRanking.Build("dune", new Dictionary<SearchKind, IReadOnlyList<BaseItemDto>>
		{
			[SearchKind.Movies] = [Item("Dune", BaseItemDto_Type.Movie), Item("Dune", BaseItemDto_Type.Person), Item("Dune", BaseItemDto_Type.Folder)],
			[SearchKind.People] = [Item("Dune Holm", BaseItemDto_Type.Person)],
		});

		Assert.Equal(2, results.Count);
		Assert.Equal(["Movies", "People"], results.Groups.Select(g => g.Kind.Title));
		Assert.Single(SearchRanking.Order("dune", [Item("Dune", BaseItemDto_Type.Person), Item("Dune", BaseItemDto_Type.Genre)]));
	}

	[Fact]
	public void AnEpisodeMatch_RanksItsShow_AndBringsTheShowIn()
	{
		var strixShow = Guid.NewGuid();
		var episode = Item("Operation Strix", BaseItemDto_Type.Episode, series: strixShow, seriesName: "Spy x Family");
		var other = Item("Strix Variations", BaseItemDto_Type.Series);
		var show = new BaseItemDto { Id = strixShow, Name = "Spy x Family", Type = BaseItemDto_Type.Series };

		var results = SearchRanking.Build("operation strix", Answers(episode, other), [show]);

		var shows = results.GroupOf(SearchKind.Shows)!;
		Assert.Equal(["Spy x Family", "Strix Variations"], shows.Strong.Concat(shows.Weak).Select(s => s.Item.Name));
		Assert.Same(episode, shows.Strong[0].ViaEpisode);
		// the episode's own title is the top result; its show yields to it
		Assert.Same(episode, results.Top?.Item);
		Assert.Equal(["Episodes", "Shows"], results.Groups.Select(g => g.Kind.Title));
	}

	[Fact]
	public void AShowsOwnTitle_BeatsItsEpisodes_WhenBetter()
	{
		var id = Guid.NewGuid();
		var show = new BaseItemDto { Id = id, Name = "Pilot", Type = BaseItemDto_Type.Series };
		var results = SearchRanking.Build("pilot", Answers(show, Item("The Pilot Returns", BaseItemDto_Type.Episode, series: id)));

		var ranked = results.GroupOf(SearchKind.Shows)!.Strong.Single();
		Assert.Null(ranked.ViaEpisode);
		Assert.Equal(SearchTier.Whole, ranked.Match.Tier);
	}

	// ---- the runner: what is asked

	private static BaseItemDtoQueryResult Found(params BaseItemDto[] items) => new() { Items = [.. items], TotalRecordCount = items.Length };

	[Fact]
	public async Task TheRunner_AsksOncePerKind_ThenTheLongestWord_WhenNothingIsFound()
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>())).ReturnsAsync(Found());
		client.Setup(x => x.Search("night", It.Is<IReadOnlyList<BaseItemKind>>(k => k.Single() == BaseItemKind.Movie), 100))
			.ReturnsAsync(Found(Item("Night Moves", BaseItemDto_Type.Movie), Item("Night of the Living Dead", BaseItemDto_Type.Movie)));

		var results = await SearchRunner.Run(client.Object, "night dead");

		Assert.Equal("Night of the Living Dead", results?.Top is null ? results?.Groups[0].Strong[0].Item.Name : null);
		Assert.Equal(["Night Moves"], results!.GroupOf(SearchKind.Movies)!.Weak.Select(r => r.Item.Name));
		// 1.5.1 fixes (BH-150-m7): five kinds through /Items (Videos too), people through /Persons
		client.Verify(x => x.Search("night dead", It.IsAny<IReadOnlyList<BaseItemKind>>(), 100), Times.Exactly(5));
		client.Verify(x => x.Search("night", It.IsAny<IReadOnlyList<BaseItemKind>>(), 100), Times.Exactly(5));
		client.Verify(x => x.Search(It.IsAny<string>(), It.Is<IReadOnlyList<BaseItemKind>>(k => k.Contains(BaseItemKind.Person)), It.IsAny<int>()), Times.Never);
	}

	[Fact]
	public async Task TheRunner_FetchesTheShowsOfMatchingEpisodes_AndFailsOnlyWhenEveryRequestFails()
	{
		var client = new Mock<IJellyfinClient>();
		var showId = Guid.NewGuid();
		client.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>())).ReturnsAsync(Found());
		client.Setup(x => x.Search("operation strix", It.Is<IReadOnlyList<BaseItemKind>>(k => k.Single() == BaseItemKind.Episode), 100))
			.ReturnsAsync(Found(Item("Operation Strix", BaseItemDto_Type.Episode, series: showId)));
		client.Setup(x => x.GetItem(showId)).ReturnsAsync(new BaseItemDto { Id = showId, Name = "Spy x Family", Type = BaseItemDto_Type.Series });

		var results = await SearchRunner.Run(client.Object, "operation strix");

		Assert.Equal("Spy x Family", results!.GroupOf(SearchKind.Shows)!.Strong.Single().Item.Name);
		client.Verify(x => x.GetItem(showId), Times.Once);

		var down = new Mock<IJellyfinClient>();
		down.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		Assert.Null(await SearchRunner.Run(down.Object, "operation strix"));

		var half = new Mock<IJellyfinClient>();
		half.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>())).ThrowsAsync(new HttpRequestException("refused"));
		half.Setup(x => x.Search("up", It.Is<IReadOnlyList<BaseItemKind>>(k => k.Single() == BaseItemKind.Movie), 100)).ReturnsAsync(Found(Item("Up", BaseItemDto_Type.Movie)));
		Assert.Equal(1, (await SearchRunner.Run(half.Object, "up"))?.Count);
	}

	[Fact]
	public async Task TheSuggestions_RetryTheLongestWord_AndRankLikeThePage()
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Search("its always sunny")).ReturnsAsync(Found());
		client.Setup(x => x.Search("always")).ReturnsAsync(Found(Item("Always Be My Maybe", BaseItemDto_Type.Movie), Item("It's Always Sunny in Philadelphia", BaseItemDto_Type.Series)));

		var result = await SearchRunner.Suggest(client.Object, "its always sunny");
		var rows = SearchSuggestions.For("its always sunny", result);

		// the strong match only; the weak one is on the results page ("See all 2 results")
		Assert.Equal("It's Always Sunny in Philadelphia", Assert.IsType<SearchSuggestion>(rows[0]).Name);
		Assert.Equal("See all 2 results for “its always sunny”", Assert.IsType<SearchSeeAll>(rows[1]).ToString());
		Assert.Equal(2, rows.Count);
	}

	[Fact]
	public async Task TheRunner_MergesTheHints_EachItemOnce_AndAsksPeopleThroughPersons()
	{
		var client = new Mock<IJellyfinClient>();
		var known = Item("Sunny", BaseItemDto_Type.Movie);
		var showId = Guid.NewGuid();
		client.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>())).ReturnsAsync(Found());
		client.Setup(x => x.Search("sunny", It.Is<IReadOnlyList<BaseItemKind>>(k => k.Single() == BaseItemKind.Movie), 100)).ReturnsAsync(Found(known));
		client.Setup(x => x.SearchPeople("sunny", 100)).ReturnsAsync(Found(Item("Sunny Holm", BaseItemDto_Type.Person)));
		// the hints name the film again (once only), a show the kinds missed, and a genre (no kind of ours)
		client.Setup(x => x.SearchHints("sunny", SearchRunner.HintLimit)).ReturnsAsync(new SearchHintResult
		{
			SearchHints =
			[
				new SearchHint { Id = known.Id, Name = "Sunny", Type = SearchHint_Type.Movie },
				new SearchHint { Id = showId, Name = "Sunny Days", Type = SearchHint_Type.Series },
				new SearchHint { Id = Guid.NewGuid(), Name = "Sunny Pop", Type = SearchHint_Type.MusicGenre },
			],
		});
		client.Setup(x => x.GetSearchItems(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(Found(new BaseItemDto { Id = showId, Name = "Sunny Days", Type = BaseItemDto_Type.Series }));

		var results = await SearchRunner.Run(client.Object, "sunny");

		client.Verify(x => x.GetSearchItems(It.Is<IReadOnlyList<Guid>>(ids => ids.Single() == showId)), Times.Once);
		client.Verify(x => x.Search(It.IsAny<string>(), It.Is<IReadOnlyList<BaseItemKind>>(k => k.Contains(BaseItemKind.Person)), It.IsAny<int>()), Times.Never);
		Assert.Equal(["Sunny"], results!.GroupOf(SearchKind.Movies)!.Strong.Select(r => r.Item.Name));
		Assert.Equal(["Sunny Days"], results.GroupOf(SearchKind.Shows)!.Strong.Select(r => r.Item.Name));
		Assert.Equal(["Sunny Holm"], results.GroupOf(SearchKind.People)!.Strong.Select(r => r.Item.Name));
		Assert.Equal("Sunny", results.Top?.Item.Name);
	}

	[Fact]
	public async Task TheHintsAlone_AreEnough_WhenEveryKindFails()
	{
		var client = new Mock<IJellyfinClient>();
		var id = Guid.NewGuid();
		client.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		client.Setup(x => x.SearchHints("up", SearchRunner.HintLimit)).ReturnsAsync(new SearchHintResult { SearchHints = [new SearchHint { Id = id, Name = "Up", Type = SearchHint_Type.Movie }] });
		client.Setup(x => x.GetSearchItems(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(Found(new BaseItemDto { Id = id, Name = "Up", Type = BaseItemDto_Type.Movie }));

		Assert.Equal("Up", (await SearchRunner.Run(client.Object, "up"))?.Top?.Item.Name);
	}

	[Fact]
	public async Task APerson_HasAPeopleChip_AndOpensWithName()
	{
		var client = Client(Item("Sunny", BaseItemDto_Type.Movie));
		client.Setup(x => x.SearchPeople("sunny", 100)).ReturnsAsync(Found(Item("Sunny Holm", BaseItemDto_Type.Person)));
		var vm = new SearchResultsViewModel(client.Object);
		SearchPageMemory.Forget("sunny");
		await vm.OnNavigatedTo("sunny");

		Assert.Equal(["All", "Movies", "People"], vm.Chips.Select(c => c.Label));
		Assert.Equal("People, 1 result", vm.Chips[2].Name);
		vm.ShowKindCommand.Execute(SearchKind.People);
		Assert.Equal("People (1)", Assert.Single(vm.CardGroups).Heading);
		Assert.Equal(typeof(PersonItemsViewModel), GlobalCommands.PageFor(BaseItemDto_Type.Person));
	}

	[Fact]
	public async Task WithName_ListsTheirFilmsAndShows_OrSaysWhyNot()
	{
		var person = Item("Sunny Holm", BaseItemDto_Type.Person);
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetPersonItems(person.Id!.Value)).ReturnsAsync(Found(Item("Sunny", BaseItemDto_Type.Movie), Item("Sunny Days", BaseItemDto_Type.Series)));
		var vm = new PersonItemsViewModel(client.Object);

		await vm.OnNavigatedTo(person);
		Assert.Equal("With Sunny Holm", vm.Title);
		Assert.True(vm.Results.IsLoaded);
		Assert.Equal(["Sunny", "Sunny Days"], vm.Items.Select(i => i.Dto.Name));

		var nobody = Item("Nobody Here", BaseItemDto_Type.Person);
		client.Setup(x => x.GetPersonItems(nobody.Id!.Value)).ReturnsAsync(Found());
		await vm.OnNavigatedTo(nobody);
		Assert.True(vm.Results.IsEmpty);
		Assert.Equal("Nothing with Nobody Here", vm.Results.Title);
		// BH-150-m3: a guest star is credited on episodes only, which the page doesn't ask: it says what it looked at
		Assert.StartsWith("No films or shows on ", vm.Results.Message, StringComparison.Ordinal);
		Assert.EndsWith(" list them. Guest roles on single episodes aren't shown here.", vm.Results.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("has no films or shows", vm.Results.Message, StringComparison.Ordinal);
		Assert.Empty(vm.Items);

		var down = Item("Down Town", BaseItemDto_Type.Person);
		client.Setup(x => x.GetPersonItems(down.Id!.Value)).ReturnsAsync((BaseItemDtoQueryResult?)null);
		await vm.OnNavigatedTo(down);
		Assert.True(vm.Results.IsFailed);
		Assert.Equal("Couldn't load the films and shows with Down Town", vm.Results.Title);

		await vm.OnNavigatedTo("not a person");
		Assert.True(vm.Results.IsEmpty);
		Assert.Equal("With this person", vm.Title);
	}

	// ---- the view model: chips, the fold, and their ways out

	private static Mock<IJellyfinClient> Client(params BaseItemDto[] items)
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>()))
			.ReturnsAsync((string _, IReadOnlyList<BaseItemKind> kinds, int _) => Found([.. items.Where(i => SearchKinds.All.Any(k => k.ServerKinds.SequenceEqual(kinds) && i.Type is { } type && k.Types.Contains(type)))]));
		return client;
	}

	/// <summary>
	/// 1.5.1 fixes (BH-150-m7): search finds home videos and music videos (it asked for shows, films, episodes, collections
	/// and people only): a Videos group with its chip, asked for in one request, named for Narrator; each opens its page. Not
	/// songs or albums (BH-151-M2: nothing plays them): none asked for, none shown.
	/// </summary>
	[Fact]
	public async Task HomeVideosAndMusicVideos_AreFound_InTheirOwnGroup_SongsAndAlbumsAreNot()
	{
		var home = Item("Sunny Beach Day", BaseItemDto_Type.Video);
		var clip = Item("Sunny Afternoon", BaseItemDto_Type.MusicVideo);
		var client = Client(Item("Sunny", BaseItemDto_Type.Movie), home, clip, Item("Sunny Side", BaseItemDto_Type.MusicAlbum), Item("Sunny Morning", BaseItemDto_Type.Audio));
		var vm = new SearchResultsViewModel(client.Object);
		SearchPageMemory.Forget("sunny");

		await vm.OnNavigatedTo("sunny");

		client.Verify(x => x.Search("sunny", It.Is<IReadOnlyList<BaseItemKind>>(k => k.SequenceEqual(new[] { BaseItemKind.Video, BaseItemKind.MusicVideo })), 100), Times.Once);
		client.Verify(x => x.Search(It.IsAny<string>(), It.Is<IReadOnlyList<BaseItemKind>>(k => k.Contains(BaseItemKind.Audio) || k.Contains(BaseItemKind.MusicAlbum)), It.IsAny<int>()), Times.Never);
		Assert.Equal(["All", "Movies", "Videos"], vm.Chips.Select(c => c.Label));
		vm.ShowKindCommand.Execute(SearchKind.Videos);
		Assert.Equal("Videos (2)", Assert.Single(vm.CardGroups).Heading);
		Assert.Equal("Sunny Beach Day, video", SearchLabels.For(home));
		Assert.Equal("Sunny Afternoon, music video", SearchLabels.For(clip));
		Assert.Equal(typeof(MovieViewModel), GlobalCommands.PageFor(BaseItemDto_Type.Video));
		Assert.Equal(typeof(MovieViewModel), GlobalCommands.PageFor(BaseItemDto_Type.MusicVideo));
		// a hint of these kinds counts too (/Search/Hints names them by the same names); a song's does not
		Assert.Equal(SearchKind.Videos, SearchKinds.KindOf(SearchHint_Type.MusicVideo));
		Assert.Null(SearchKinds.KindOf(SearchHint_Type.Audio));
		Assert.Null(SearchKinds.KindOf(SearchHint_Type.MusicAlbum));
	}

	/// <summary>
	/// 1.5.1 (the bug hunt's BH-151-M2): everything search finds can be played from where it opens: an item Play starts
	/// (CollectionBrowse.IsPlayable), a collection whose page plays what it holds, or a person whose page lists films and
	/// shows. An album found by search opened with Play off.
	/// </summary>
	[Fact]
	public void EveryKindSearchFinds_OpensSomethingThatPlays()
	{
		foreach (var type in SearchKinds.All.SelectMany(k => k.Types))
		{
			Assert.True(CollectionBrowse.IsPlayable(new BaseItemDto { Type = type }) || type is BaseItemDto_Type.BoxSet or BaseItemDto_Type.Person,
				$"{type}: search finds it, but nothing plays it");
			Assert.NotNull(GlobalCommands.PageFor(type));
		}
	}

	[Fact]
	public async Task ShowMore_OpensTheWeakMatches_AndShowFewerClosesThem()
	{
		SearchPageMemory.Forget("it's always sunny");
		var vm = new SearchResultsViewModel(Client(Sunny()).Object);
		await vm.OnNavigatedTo("it's always sunny");

		var movies = Assert.Single(vm.CardGroups);
		Assert.Equal("Movies (295)", movies.Heading);
		Assert.False(movies.HasCards); // nothing strong: just the heading and the button
		Assert.Equal("Show 295 weaker matches", movies.MoreText); // nothing shown yet: not "more"
		Assert.Equal("Show 295 more movies, weaker matches", movies.MoreName);

		movies.ToggleMoreCommand.Execute(null);
		Assert.True(movies.HasCards);
		Assert.Equal(295, movies.Items.Count);
		Assert.Equal("Show fewer", movies.MoreText);
		Assert.Equal("Show fewer movies", movies.MoreName);

		movies.ToggleMoreCommand.Execute(null);
		Assert.Empty(movies.Items);
		Assert.Equal("Show 295 weaker matches", movies.MoreText);
	}

	[Fact]
	public async Task TheChips_ShowOneKind_OneLit_AndAllComesBack()
	{
		SearchPageMemory.Forget("dune");
		var vm = new SearchResultsViewModel(Client(Item("Dune", BaseItemDto_Type.Movie), Item("Dune Part Two", BaseItemDto_Type.Movie), Item("Dune: Prophecy", BaseItemDto_Type.Series), Item("Dune Collection", BaseItemDto_Type.BoxSet)).Object);
		await vm.OnNavigatedTo("dune");

		Assert.Equal(["All", "Shows", "Movies", "Collections"], vm.Chips.Select(c => c.Label));
		Assert.Equal("Dune", vm.TopResult?.Dto.Name);
		Assert.Equal(["Movies (1)", "Shows (1)", "Collections (1)"], vm.CardGroups.Select(g => g.Heading)); // the top result is not repeated

		vm.ShowKindCommand.Execute(SearchKind.Movies);
		Assert.Equal(SearchKind.Movies, vm.Kind);
		Assert.Equal([false, false, true, false], vm.Chips.Select(c => c.IsSelected));
		Assert.Null(vm.TopResult); // a kind shows its whole group, the top result in it
		Assert.Equal(["Dune", "Dune Part Two"], Assert.Single(vm.CardGroups).Items.Select(i => i.Dto.Name));

		vm.ShowKindCommand.Execute(null);
		Assert.Null(vm.Kind);
		Assert.Equal([true, false, false, false], vm.Chips.Select(c => c.IsSelected));
		Assert.Equal("Dune", vm.TopResult?.Dto.Name);
		Assert.Equal(3, vm.CardGroups.Count);
	}

	[Fact]
	public async Task AChipLitByNarratorsSelect_ShowsItsKind_AndAStrongGroupSaysShowNMore()
	{
		// Narrator's select (and a click, and Space) only checks the radio button: the chip itself must switch the view
		SearchPageMemory.Forget("dune");
		var vm = new SearchResultsViewModel(Client(Item("Dune", BaseItemDto_Type.Movie), Item("Dune Part Two", BaseItemDto_Type.Movie), Item("Sandunes", BaseItemDto_Type.Movie),
			Item("Dune: Prophecy", BaseItemDto_Type.Series)).Object);
		await vm.OnNavigatedTo("dune");

		vm.Chips.Single(c => c.Kind == SearchKind.Shows).IsSelected = true;
		Assert.Equal(SearchKind.Shows, vm.Kind);
		Assert.Equal(["Shows (1)"], vm.CardGroups.Select(g => g.Heading));
		vm.Chips[0].IsSelected = true;
		Assert.Null(vm.Kind);

		// strong matches shown, then the weaker ones: "Show 1 more"
		var movies = vm.CardGroups.Single(g => g.Kind == SearchKind.Movies);
		Assert.Equal("Show 1 more", movies.MoreText);
		Assert.Equal("Show 1 more movie, weaker matches", movies.MoreName);
	}

	[Fact]
	public async Task TheTopResult_HasItsTitle_KindAndYear_AndWhyItMatched()
	{
		var showId = Guid.NewGuid();
		var client = Client(new BaseItemDto { Id = Guid.NewGuid(), Name = "Operation Strix", Type = BaseItemDto_Type.Episode, SeriesId = showId, SeriesName = "Spy x Family", ProductionYear = 2022 });
		var vm = new SearchResultsViewModel(client.Object);
		await vm.OnNavigatedTo("operation strix");
		Assert.Equal("Operation Strix", vm.TopResultTitle);
		Assert.Equal("Episode of Spy x Family · 2022", vm.TopResultMeta);
		Assert.Equal("", vm.TopResultNote);

		var show = new RankedItem(new BaseItemDto { Name = "Spy x Family", Type = BaseItemDto_Type.Series, ProductionYear = 2022 }, new SearchMatch(SearchTier.Whole, 1), 0) { ViaEpisode = new BaseItemDto { Name = "Operation Strix" } };
		Assert.Equal(("Spy x Family", "Show · 2022", "Has the episode “Operation Strix”"), SearchResultsText.Describe(show));
		var amelie = new RankedItem(new BaseItemDto { Name = "Amélie", OriginalTitle = "Le Fabuleux Destin d'Amélie Poulain", Type = BaseItemDto_Type.Movie }, new SearchMatch(SearchTier.AllWords, 1), 0);
		Assert.Equal(("Amélie", "Movie", "Original title: Le Fabuleux Destin d'Amélie Poulain"), SearchResultsText.Describe(amelie));
	}

	[Fact]
	public async Task OneKindOfResults_NeedsNoChips()
	{
		var vm = new SearchResultsViewModel(Client(Item("Dune", BaseItemDto_Type.Movie), Item("Dune Part Two", BaseItemDto_Type.Movie)).Object);
		await vm.OnNavigatedTo("dune");

		Assert.Empty(vm.Chips);
		Assert.Equal("Dune", vm.TopResult?.Dto.Name);
		Assert.Equal(["Movies (1)"], vm.CardGroups.Select(g => g.Heading));
	}

	[Fact]
	public async Task BackAndRefresh_KeepTheChipAndTheOpenedGroups_ANewSearchStartsOnAll()
	{
		SearchPageMemory.Forget("it's always sunny");
		var items = Sunny();
		var vm = new SearchResultsViewModel(Client(items).Object);
		await vm.OnNavigatedTo("it's always sunny");
		vm.ShowKindCommand.Execute(SearchKind.Movies);
		vm.CardGroups.Single().ToggleMoreCommand.Execute(null);

		// Back to the page, or R on it: a new view model for the same query
		var again = new SearchResultsViewModel(Client(items).Object);
		await again.OnNavigatedTo("it's always sunny");
		Assert.Equal(SearchKind.Movies, again.Kind);
		Assert.True(again.CardGroups.Single().IsExpanded);
		Assert.True(again.Chips.Single(c => c.Kind == SearchKind.Movies).IsSelected);

		// Enter in the search box with the same query on its own page: the page starts over in place (the navigation
		// service shows nothing new for the same page and query)
		SearchPageMemory.Forget("it's always sunny");
		Assert.Null(again.Kind);
		Assert.True(again.Chips[0].IsSelected);
		Assert.False(Assert.Single(again.CardGroups, g => g.Kind == SearchKind.Movies).IsExpanded);
		await again.OnNavigatedFrom();

		// a new page for it: All too
		var fresh = new SearchResultsViewModel(Client(items).Object);
		await fresh.OnNavigatedTo("it's always sunny");
		Assert.Null(fresh.Kind);
		Assert.False(fresh.CardGroups.Single().IsExpanded);
	}

	[Fact]
	public async Task AChipForAKindWithNothing_FallsBackToAll()
	{
		var vm = new SearchResultsViewModel(Client(Item("Dune", BaseItemDto_Type.Movie), Item("Dune: Prophecy", BaseItemDto_Type.Series)).Object);
		await vm.OnNavigatedTo("dune");

		vm.ShowKindCommand.Execute(SearchKind.Episodes);

		Assert.Null(vm.Kind);
		Assert.True(vm.Chips[0].IsSelected);
	}

	[Fact]
	public void TheEdges_TenThousandItems_NullFields()
	{
		var many = Enumerable.Range(0, 10_000).Select(i => Item(i % 2 == 0 ? $"Night {i}" : "Elsewhere", BaseItemDto_Type.Episode)).ToArray();
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var results = SearchRanking.Build("night", Answers(many));
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
		Assert.Equal(5_000, results.GroupOf(SearchKind.Episodes)!.Strong.Count);

		var nulls = SearchRanking.Build("night", Answers(new BaseItemDto { Type = BaseItemDto_Type.Movie }, new BaseItemDto { Name = "Night", Type = BaseItemDto_Type.Episode, SeriesId = Guid.NewGuid() }));
		Assert.Equal(2, nulls.Count);
		Assert.Equal("Night", nulls.Top?.Item.Name);
	}
}
