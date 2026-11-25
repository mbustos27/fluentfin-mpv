using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Outside;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests.Outside;

/// <summary>Nixie 1.7.5 (stream fx1): the outside lists' Minors from the 1.7.0 bug hunt (BH-170-q3, q5).</summary>
public sealed class OutsideFixes175Tests
{
	private static readonly DateTimeOffset Noon = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

	private static OutsideList Heists(params string[] tmdb) => new("genre-heist", OutsideSourceId.Wikidata, OutsideListKind.Genre, "Heists", "heist films from Wikidata",
		[.. tmdb.Select(id => new OutsideListItem(new OutsideRef(Tmdb: id, Kind: IndexKind.Movie)))]) { KeywordTag = "heist" };

	// --- BH-170-q5: a refresh is compared item by item ------------------------------------------------------------------

	[Fact]
	public void TwoSnapshots_WithAsManyTitlesButOtherOnes_AreNotTheSame()
	{
		var names = OutsideLists.KeywordSet(["heist", "time travel"]);
		var before = OutsideListsSnapshot.Empty with { KeywordNames = names, Lists = [Heists("1", "2", "3")] };

		Assert.True(OutsideLists.Same(before, before with { KeywordNames = OutsideLists.KeywordSet(["time travel", "heist"]), Lists = [Heists("1", "2", "3")] }));
		Assert.False(OutsideLists.Same(before, before with { Lists = [Heists("1", "2", "4")] })); // a title swapped
		Assert.False(OutsideLists.Same(before, before with { Lists = [Heists("1", "3", "2")] })); // the list's order (an award's years)
		Assert.False(OutsideLists.Same(before, before with { KeywordNames = OutsideLists.KeywordSet(["heist", "slasher"]) })); // a keyword name swapped
		Assert.False(OutsideLists.Same(before, before with { Lists = [Heists("1", "2", "3") with { Why = "other words" }] }));
		Assert.False(OutsideLists.Same(before, before with { FranchiseNames = new Dictionary<string, string> { ["10"] = "Star Wars Collection" } }));
	}

	[Fact]
	public async Task AWikidataRefresh_WithAsManyTitlesButOtherOnes_IsPublished()
	{
		var clock = new TestClock(Noon);
		var fake = new StrictWikidata(clock.Func);
		var store = new MemoryStore();
		var hub = new OutsideHub(new HttpClient(new OutsideRequestRecorder(fake)), OutsideRouting.Real, "1.7.0", new OutsideCache(OutsideKit.TempFolder(), clock.Func), store, clock.Func);
		var lists = new OutsideLists(hub, store, clock.Func);
		store.SaveSetting(SettingKeys.LookUpWikidata, true);
		var first = await lists.RefreshAsync();
		var heists = Assert.Single(first.Lists, x => x.Id == "genre-heist");
		var changed = 0;
		lists.Changed += _ => changed++;

		// a week on, Wikidata has one heist film in place of another: as many titles
		var original = OutsideFixtures.Wikidata("genre-heist");
		const string Swapped = "\"tmdb\":{\"type\":\"literal\",\"value\":\"35671\"}";
		Assert.Contains(Swapped, original, StringComparison.Ordinal);
		fake.Answer = query => query.Id == "genre-heist"
			? original.Replace(Swapped, "\"tmdb\":{\"type\":\"literal\",\"value\":\"999999175\"}", StringComparison.Ordinal)
			: OutsideFixtures.Wikidata(query.Id);
		hub.Cache.Clear();
		var second = await lists.RefreshAsync();

		var now = Assert.Single(second.Lists, x => x.Id == "genre-heist");
		Assert.Equal(heists.Items.Count, now.Items.Count);
		Assert.Contains(now.Items, x => x.Ref.Tmdb == "999999175");
		Assert.DoesNotContain(now.Items, x => x.Ref.Tmdb == "35671");
		Assert.Same(second, lists.Current); // published
		Assert.Equal(1, changed);

		// asked again with nothing changed: nothing published
		hub.Cache.Clear();
		Assert.Same(second, await lists.RefreshAsync());
		Assert.Equal(1, changed);
	}

	// --- BH-170-q3: a list fetched across a switch-off is never written or held ------------------------------------------

	[Theory]
	[InlineData(OutsideSourceId.Trakt)]
	[InlineData(OutsideSourceId.TmdbTrending)]
	public async Task AListAskedBeforeASwitchOff_IsNeitherWrittenNorHeld_EvenWithTheSourceOnAgain(OutsideSourceId id)
	{
		// the bug hunt: IsActive was read, then the list written and held; turned off (Forget) between the two, and on again,
		// that list stayed on disk and showed at once (PRIVACY.md: turning one off deletes its lists)
		using var rig = new TrendingRig(trakt: id == OutsideSourceId.Trakt, tmdb: id == OutsideSourceId.TmdbTrending, token: TrendingFake.Token);
		rig.Fake.Trakt["movies/trending"] = TrendingFake.Unowned("Film", 40);
		rig.Fake.Trakt["shows/trending"] = TrendingFake.Unowned("Show", 30, 1000);
		rig.Fake.Trakt["movies/popular"] = TrendingFake.Unowned("Popular film", 20, 3000);
		rig.Fake.Trakt["shows/popular"] = TrendingFake.Unowned("Popular show", 20, 4000);
		rig.Fake.Trakt["movies/anticipated"] = TrendingFake.Unowned("Coming film", 12, 5000);
		rig.Fake.Trakt["shows/anticipated"] = TrendingFake.Unowned("Coming show", 12, 6000);
		rig.Fake.Trakt["movies/boxoffice"] = TrendingFake.Unowned("Box film", 10, 7000);
		rig.Fake.Tmdb["movie"] = [.. Enumerable.Range(1, 40).Select(i => (500_000 + i, $"TMDb film {i}"))];
		rig.Fake.Tmdb["tv"] = [.. Enumerable.Range(1, 40).Select(i => (600_000 + i, $"TMDb show {i}"))];
		var flipped = false;
		rig.Fake.OnRequest = _ =>
		{
			if (!flipped)
			{
				// the first list's request is on its way: turned off (its lists forgotten) and on again
				flipped = true;
				rig.Hub.SetOn(id, false);
				rig.Hub.SetOn(id, true);
			}
		};

		await rig.Sources.RefreshAsync();

		var first = id == OutsideSourceId.Trakt ? "movies-trending" : "trending-movie-week";
		var info = OutsideSources.Get(id);
		Assert.True(flipped);
		Assert.Null(rig.Hub.Cache.Read(info, first)); // not written
		var lists = rig.Sources.Current().Lists;
		Assert.DoesNotContain(lists, x => x.Source == id && x.Kind == ListedKind.Movie && x.List == TrendingList.Trending); // not held
		Assert.Contains(lists, x => x.Source == id && x.Kind == ListedKind.Series && x.List == TrendingList.Trending); // the lists asked after it are
		Assert.NotNull(rig.Hub.Cache.Read(info, id == OutsideSourceId.Trakt ? "shows-trending" : "trending-tv-week"));

		// the next round asks the forgotten one again
		await rig.Sources.RefreshAsync();
		Assert.NotNull(rig.Hub.Cache.Read(info, first));
		Assert.Contains(rig.Sources.Current().Lists, x => x.Source == id && x.Kind == ListedKind.Movie && x.List == TrendingList.Trending);
	}

	// --- the live trending check's key guard (the 1.7.5 review) -------------------------------------------------------------

	[Fact]
	public void TheLiveTrendingCheck_GuardsItsKeys_WithAssertionsThatNeverPrintThem()
	{
		// xUnit's DoesNotContain, Contains, Equal and the like print what they compared when they fail: a guard that tripped
		// would have written the Client ID or the token to the test log. Only Assert.True and Assert.False (a message of the
		// test's own) may take a key
		var source = File.ReadAllLines(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin.Core.Tests", "Outside", "LiveTrendingSourcesTests.cs"));
		var key = new System.Text.RegularExpressions.Regex(@"\b(clientId|token)\b");
		var printing = new System.Text.RegularExpressions.Regex(@"Assert\.(?!True\b|False\b)\w+\(");
		var guards = source.Select((line, i) => (Line: line, At: i + 1)).Where(x => x.Line.Contains("Assert.", StringComparison.Ordinal) && key.IsMatch(x.Line)).ToList();

		Assert.True(guards.Count >= 4, "the summary's and the addresses' guards are found");
		Assert.All(guards, x => Assert.False(printing.IsMatch(x.Line), $"line {x.At}: an assertion that prints its arguments takes a key"));
	}

	[Theory]
	[InlineData(OutsideSourceId.Trakt)]
	[InlineData(OutsideSourceId.TmdbTrending)]
	public async Task AListReadFromTheCacheWhileItIsForgotten_IsNotKept(OutsideSourceId id)
	{
		// the 1.7.5 review: Forget moved the generation on, let go of the lock, then cleared the cache; a list read from the
		// cache in between (Held: a page opening) saw the new generation, read the old file and kept it, shown again at once
		using var rig = new TrendingRig(trakt: id == OutsideSourceId.Trakt, tmdb: id == OutsideSourceId.TmdbTrending, token: TrendingFake.Token);
		rig.Fake.Trakt["movies/trending"] = TrendingFake.Unowned("Film", 40);
		rig.Fake.Trakt["shows/trending"] = TrendingFake.Unowned("Show", 30, 1000);
		rig.Fake.Trakt["movies/popular"] = TrendingFake.Unowned("Popular film", 20, 3000);
		rig.Fake.Trakt["shows/popular"] = TrendingFake.Unowned("Popular show", 20, 4000);
		rig.Fake.Trakt["movies/anticipated"] = TrendingFake.Unowned("Coming film", 12, 5000);
		rig.Fake.Trakt["shows/anticipated"] = TrendingFake.Unowned("Coming show", 12, 6000);
		rig.Fake.Trakt["movies/boxoffice"] = TrendingFake.Unowned("Box film", 10, 7000);
		rig.Fake.Tmdb["movie"] = [.. Enumerable.Range(1, 40).Select(i => (500_000 + i, $"TMDb film {i}"))];
		rig.Fake.Tmdb["tv"] = [.. Enumerable.Range(1, 40).Select(i => (600_000 + i, $"TMDb show {i}"))];
		await rig.Sources.RefreshAsync();
		Assert.Contains(rig.Sources.Current().Lists, x => x.Source == id);
		Task<TrendingSnapshot>? reader = null;
		rig.Sources.Forgetting = _ =>
		{
			// a page opens just as the lists are forgotten: it reads them from the cache (given time to finish, if it can)
			reader = Task.Run(rig.Sources.Current);
			reader.Wait(TimeSpan.FromMilliseconds(500));
		};

		rig.Sources.Forget(id);
		await reader!.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.DoesNotContain(rig.Sources.Current().Lists, x => x.Source == id); // nothing kept in memory
		Assert.All(CacheNames(id), name => Assert.Null(rig.Hub.Cache.Read(OutsideSources.Get(id), name))); // nor on disk
	}

	private static string[] CacheNames(OutsideSourceId id) =>
		id == OutsideSourceId.Trakt ? ["movies-trending", "shows-trending"] : ["trending-movie-week", "trending-tv-week"];
}
