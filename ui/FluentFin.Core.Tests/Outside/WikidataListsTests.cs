using System.Text.RegularExpressions;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Outside;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using static FluentFin.Core.Tests.Outside.OutsideKit;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 keyless: Wikidata's awards and finer genres (Core\Outside\WikidataLists.cs): the queries as constants, the parser
/// against the live check's recorded answers, the endpoint setting, the strict fake's refusals (403 without a contact,
/// query-next before it opens, 429 with Retry-After, the timeout, 30 errors a minute), the week's cache, and the Awards row
/// and genre collections the lists make.
/// </summary>
public sealed partial class WikidataListsTests
{
	[GeneratedRegex(@"\b(wd|wdt|p|ps|pq|rdfs):")]
	private static partial Regex UsedPrefix();

	[Fact]
	public void Every_query_is_a_constant_with_explicit_PREFIX_lines_and_simple_OPTIONALs()
	{
		Assert.Equal(12, WikidataLists.All.Count); // nine awards (Best Picture winners and nominees, two Golden Globes) and three genres
		Assert.Equal(WikidataLists.All.Count, WikidataLists.All.Select(x => x.Id).Distinct().Count());
		foreach (var query in WikidataLists.All)
		{
			var body = query.Query[query.Query.IndexOf("SELECT", StringComparison.Ordinal)..];
			foreach (var prefix in UsedPrefix().Matches(body).Select(m => m.Groups[1].Value).Distinct())
			{
				Assert.Contains($"PREFIX {prefix}: <", query.Query, StringComparison.Ordinal);
			}

			Assert.DoesNotContain("BIND", query.Query, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("SERVICE", query.Query, StringComparison.OrdinalIgnoreCase); // no label service: ids only
			Assert.DoesNotContain("VALUES", query.Query, StringComparison.OrdinalIgnoreCase);  // nothing listed from anywhere
			foreach (Match optional in Regex.Matches(query.Query, @"OPTIONAL \{([^}]*)\}"))
			{
				Assert.Single(optional.Groups[1].Value.Split(" .", StringSplitOptions.RemoveEmptyEntries), x => x.Trim().Length > 0);
			}

			Assert.True(OutsideCache.IsValidName(query.Id), query.Id);
		}
	}

	[Fact]
	public void Every_lists_title_fits_one_line_of_a_tile()
	{
		// the harness (2026-11-22): a two-line title pushed its "9 films" out of the tile; the why line keeps the full award
		Assert.All(WikidataLists.All, q => Assert.True(q.Title.Length <= 24, q.Title));
	}

	[Theory]
	[InlineData("award-best-picture", "wd:Q102427")]
	[InlineData("award-palme-dor", "wd:Q179808")]
	[InlineData("award-golden-globe-drama", "wd:Q1011509")]
	[InlineData("award-golden-globe-comedy", "wd:Q670282")]
	[InlineData("award-bafta-film", "wd:Q139184")]
	[InlineData("award-cesar-film", "wd:Q645595")]
	[InlineData("award-golden-bear", "wd:Q154590")]
	[InlineData("award-golden-lion", "wd:Q209459")]
	[InlineData("genre-heist", "wd:Q496523")]
	[InlineData("genre-slasher", "wd:Q853630")]
	[InlineData("genre-time-travel", "wd:Q182154")]
	public void Each_query_names_the_item_the_live_checks_label_lookup_found(string id, string item)
	{
		// 2026-11-22: the live check looked each award and genre up by its English label; three first guesses were wrong
		Assert.Contains(item + " ", WikidataLists.All.Single(x => x.Id == id).Query, StringComparison.Ordinal);
	}

	[Fact]
	public void The_recorded_Best_Picture_answer_parses_to_films_by_year_and_drops_the_producers()
	{
		var items = WikidataLists.Parse(OutsideFixtures.Wikidata("award-best-picture"), OutsideListKind.Award)!;

		Assert.True(items.Count >= 90, $"{items.Count}");
		Assert.All(items, x =>
		{
			Assert.NotNull(x.Ref.Tmdb);
			Assert.Equal(IndexKind.Movie, x.Ref.Kind);
			Assert.True(x.Ref.Imdb is null || x.Ref.Imdb.StartsWith("tt", StringComparison.Ordinal));
		});
		Assert.Equal(items.Select(x => x.Year ?? int.MaxValue).Order(), items.Select(x => x.Year ?? int.MaxValue));
		Assert.Equal(items.Count, items.Select(x => x.Ref.Tmdb).Distinct().Count());
		Assert.InRange(items[0].Year!.Value, 1928, 1931);
	}

	[Fact]
	public void Every_recorded_answer_parses()
	{
		foreach (var query in WikidataLists.All)
		{
			var items = WikidataLists.Parse(OutsideFixtures.Wikidata(query.Id), query.Kind);
			Assert.NotNull(items);
			Assert.NotEmpty(items);
			Assert.All(items, x => Assert.True(x.Ref.Tmdb is not null || x.Ref.Imdb is not null));
		}
	}

	[Fact]
	public void A_shows_TMDb_id_is_a_show_and_rows_of_one_item_join()
	{
		const string json = """
			{"head":{"vars":["item","tmdb","tmdbTv","imdb","date"]},"results":{"bindings":[
			  {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q1"},"tmdbTv":{"type":"literal","value":"1399"}},
			  {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q2"},"tmdb":{"type":"literal","value":"949"},"date":{"type":"literal","value":"1996-03-25T00:00:00Z"}},
			  {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q2"},"imdb":{"type":"literal","value":"tt0113277"},"date":{"type":"literal","value":"1995-03-25T00:00:00Z"}},
			  {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q3"},"imdb":{"type":"literal","value":"nm0000229"}},
			  {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q4"},"tmdb":{"type":"literal","value":"abc"},"date":{"type":"literal","value":"-0500-01-01T00:00:00Z"}}
			]}}
			""";

		var genre = WikidataLists.Parse(json, OutsideListKind.Genre)!;

		Assert.Equal(2, genre.Count);
		Assert.Equal(new OutsideRef(Tmdb: "1399", Kind: IndexKind.Series), genre[0].Ref);
		Assert.Equal(new OutsideRef(Tmdb: "949", Imdb: "tt0113277", Kind: IndexKind.Movie), genre[1].Ref);
		Assert.Equal(1995, genre[1].Year);
	}

	[Theory]
	[InlineData("<html>Too Many Requests</html>")]
	[InlineData("{\"head\":{}}")]
	[InlineData("")]
	public void An_answer_that_isnt_SPARQL_JSON_parses_to_nothing(string text) => Assert.Null(WikidataLists.Parse(text, OutsideListKind.Award));

	// ---------- the endpoint setting

	[Theory]
	[InlineData(WikidataEndpoint.Wikidata, null, "https://query.wikidata.org/sparql")]
	[InlineData(WikidataEndpoint.Next, null, "https://query-next.wikidata.org/sparql")]
	[InlineData(WikidataEndpoint.QLever, null, "https://qlever.cs.uni-freiburg.de/api/wikidata")]
	[InlineData(WikidataEndpoint.Custom, "https://sparql.example.org/wd/sparql", "https://sparql.example.org/wd/sparql")]
	[InlineData(WikidataEndpoint.Custom, "http://sparql.example.org/sparql", "https://query.wikidata.org/sparql")]
	[InlineData(WikidataEndpoint.Custom, "https://me:secret@sparql.example.org/sparql", "https://query.wikidata.org/sparql")]
	[InlineData(WikidataEndpoint.Custom, "", "https://query.wikidata.org/sparql")]
	public void The_endpoint_is_a_setting_and_a_custom_one_must_be_https_with_no_credentials(WikidataEndpoint choice, string? custom, string expected) =>
		Assert.Equal(expected, WikidataLists.EndpointFor(choice, custom).AbsoluteUri);

	[Theory]
	[InlineData("", "Enter the endpoint's full address, starting with https://.")]
	[InlineData("query.example.org/sparql", "Enter the endpoint's full address, starting with https://.")]
	[InlineData("http://query.example.org/sparql", "The address must start with https://.")]
	[InlineData("https://u:p@query.example.org/sparql", "Leave the user name and password out of the address.")]
	[InlineData("https://query.example.org/sparql?key=1", "Enter the endpoint's address without anything after ? or #.")]
	[InlineData("https://query.example.org/sparql", null)]
	public void A_custom_endpoint_that_wont_do_says_why(string text, string? why) => Assert.Equal(why, WikidataLists.CheckCustom(text, out _));

	[Fact]
	public void The_settings_endpoint_choice_is_kept_and_a_bad_custom_address_says_what_Nixie_does_instead()
	{
		var store = new MemoryStore();
		var hub = new OutsideHub(new HttpClient(new FuncHandler(_ => throw new InvalidOperationException())), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder()), store);
		var settings = new OutsideSourcesSettingsViewModel(hub, settings: store);
		var raised = new List<string?>();
		settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		Assert.Equal(WikidataEndpoint.Wikidata, settings.SelectedWikidataEndpoint.Value);
		Assert.False(settings.IsCustomEndpoint);
		Assert.Contains("June 2027", settings.SelectedWikidataEndpoint.Description, StringComparison.Ordinal);
		settings.SelectedWikidataEndpoint = OutsideSourcesSettingsViewModel.WikidataEndpoints.Single(x => x.Value == WikidataEndpoint.Custom);
		settings.WikidataCustomUrl = " http://sparql.example.org ";

		Assert.True(settings.IsCustomEndpoint);
		Assert.Equal("Custom", store.Values["WikidataEndpoint"]!.GetValue<string>());
		Assert.Equal("The address must start with https://. Until then, Nixie asks query.wikidata.org.", settings.WikidataCustomNote);
		Assert.Equal("https://query.wikidata.org/sparql", OutsideLists.WikidataEndpointFor(store).AbsoluteUri);
		settings.WikidataCustomUrl = "https://sparql.example.org/sparql";
		Assert.Equal("Nixie asks this address for Wikidata's lists.", settings.WikidataCustomNote);
		Assert.Equal("https://sparql.example.org/sparql", OutsideLists.WikidataEndpointFor(store).AbsoluteUri);
		Assert.Contains(nameof(OutsideSourcesSettingsViewModel.IsCustomEndpoint), raised);
		Assert.Contains(nameof(OutsideSourcesSettingsViewModel.WikidataCustomNote), raised);
	}

	[Fact]
	public void The_request_puts_the_query_in_the_address_and_keeps_a_custom_endpoints_path()
	{
		var uri = WikidataLists.RequestUri(new Uri("https://sparql.example.org/wd/sparql"), WikidataLists.Awards[0]);

		Assert.Equal("https://sparql.example.org/wd/sparql", uri.GetLeftPart(UriPartial.Path));
		Assert.Equal(WikidataLists.Awards[0].Query, System.Web.HttpUtility.ParseQueryString(uri.Query)["query"]);
	}

	// ---------- against the strict fake

	private static (OutsideHub Hub, StrictWikidata Fake, OutsideRequestRecorder Recorder) Hub(TestClock clock, TimeSpan? timeout = null)
	{
		var fake = new StrictWikidata(clock.Func);
		var recorder = new OutsideRequestRecorder(fake);
		var store = new MemoryStore();
		var infos = timeout is { } t ? new[] { OutsideSources.Wikidata with { Timeout = t } } : null;
		var hub = new OutsideHub(new HttpClient(recorder), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), store, clock.Func, infos);
		hub.SetOn(OutsideSourceId.Wikidata, true);
		return (hub, fake, recorder);
	}

	[Fact]
	public async Task Every_list_is_asked_once_one_at_a_time_with_Nixies_user_agent_and_kept_a_week()
	{
		var clock = new TestClock(Noon);
		var (hub, fake, recorder) = Hub(clock);

		var (lists, last) = await WikidataLists.LoadAsync(hub, WikidataLists.MainEndpoint);

		Assert.Equal(WikidataLists.All.Select(x => x.Id), lists.Select(x => x.Id));
		Assert.True(last!.Ok);
		Assert.Equal(WikidataLists.All.Count, recorder.Requests.Count);
		Assert.All(recorder.Requests, r =>
		{
			Assert.Equal("query.wikidata.org", r.Url.Host);
			Assert.Contains(r.Headers, h => h.Key == "User-Agent" && h.Value.Contains(OutsideSources.ContactUrl, StringComparison.Ordinal));
			Assert.Contains(r.Headers, h => h.Key == "Accept" && h.Value == WikidataLists.Accept);
		});
		Assert.Equal("Best Picture winners", lists[0].Title);
		Assert.Contains("from Wikidata", lists[0].Why, StringComparison.Ordinal);

		clock.Now = Noon.AddDays(6.9);
		await WikidataLists.LoadAsync(hub, WikidataLists.MainEndpoint);
		Assert.Equal(WikidataLists.All.Count, fake.Answered); // the week's cache

		clock.Now = Noon.AddDays(7);
		await WikidataLists.LoadAsync(hub, WikidataLists.MainEndpoint);
		Assert.Equal(2 * WikidataLists.All.Count, fake.Answered);
	}

	[Fact]
	public async Task A_429_pauses_Wikidata_at_once_the_other_lists_wait_and_the_cached_ones_stay()
	{
		var clock = new TestClock(Noon);
		var (hub, fake, recorder) = Hub(clock);
		await WikidataLists.LoadAsync(hub, WikidataLists.MainEndpoint);
		var before = recorder.Requests.Count;
		fake.RetryAfter = TimeSpan.FromMinutes(2);
		clock.Now = Noon.AddDays(8);

		var (lists, last) = await WikidataLists.LoadAsync(hub, WikidataLists.MainEndpoint);

		Assert.Equal(before + 1, recorder.Requests.Count); // one 429, then nothing until the pause is over
		Assert.Equal(OutsideOutcome.RateLimited, last!.Outcome);
		Assert.Equal(clock.Now.AddMinutes(2), hub[OutsideSourceId.Wikidata].PausedUntil);
		Assert.Equal(WikidataLists.All.Count, lists.Count); // the cached lists, within their 60 days
		Assert.Contains("asked Nixie to slow down", OutsideStatus.Describe(hub, OutsideSourceId.Wikidata, clock.Now), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Query_next_before_it_opens_is_refused_and_a_run_of_refusals_stops_after_three()
	{
		var clock = new TestClock(Noon);
		var (hub, _, recorder) = Hub(clock);

		var (lists, last) = await WikidataLists.LoadAsync(hub, WikidataLists.NextEndpoint);

		Assert.Empty(lists);
		Assert.Equal(OutsideSources.ErrorsInARow, recorder.Requests.Count); // far below the service's 30 error queries a minute
		Assert.Equal(403, last!.Status);
		Assert.Contains("Unauthorized User-Agent", last.Text, StringComparison.Ordinal);
		Assert.StartsWith("Wikidata refused the request (403).", OutsideStatus.Describe(hub, OutsideSourceId.Wikidata, clock.Now), StringComparison.Ordinal); // not "slow down": it refused
		Assert.StartsWith("Wikidata refused the request (403).", (await hub[OutsideSourceId.Wikidata].GetAsync(WikidataLists.MainEndpoint)).Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_query_that_runs_past_the_timeout_ends_in_Nixies_words_and_stops_the_run()
	{
		var clock = new TestClock(Noon);
		var (hub, fake, recorder) = Hub(clock, TimeSpan.FromMilliseconds(150));
		fake.Hang = true;

		var (lists, last) = await WikidataLists.LoadAsync(hub, WikidataLists.MainEndpoint);

		Assert.Empty(lists);
		Assert.Equal(OutsideOutcome.TimedOut, last!.Outcome);
		Assert.StartsWith("Wikidata didn't answer within", last.Message, StringComparison.Ordinal);
		Assert.Equal(OutsideSources.ErrorsInARow, recorder.Requests.Count);
	}

	[Fact]
	public async Task The_strict_fake_refuses_like_the_real_service()
	{
		var clock = new TestClock(Noon);
		var fake = new StrictWikidata(clock.Func);
		using var http = new HttpClient(fake);
		var uri = WikidataLists.RequestUri(WikidataLists.MainEndpoint, WikidataLists.Awards[0]);

		using var bare = await http.GetAsync(uri);
		Assert.Equal(System.Net.HttpStatusCode.Forbidden, bare.StatusCode);
		Assert.Equal(StrictWikidata.NoContact, await bare.Content.ReadAsStringAsync()); // the live check's 403, word for word

		using var noContact = new HttpRequestMessage(HttpMethod.Get, uri);
		noContact.Headers.TryAddWithoutValidation("User-Agent", "Nixie/1.7.0");
		Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(noContact)).StatusCode);

		// 30 error queries in a minute, then 429 even for a good one
		fake.Fail = true;
		for (var i = 0; i < 30; i++)
		{
			using var failing = new HttpRequestMessage(HttpMethod.Get, uri);
			failing.Headers.TryAddWithoutValidation("User-Agent", OutsideSources.UserAgent("1.7.0"));
			await http.SendAsync(failing);
		}

		fake.Fail = false;
		using var after = new HttpRequestMessage(HttpMethod.Get, uri);
		after.Headers.TryAddWithoutValidation("User-Agent", OutsideSources.UserAgent("1.7.0"));
		using var limited = await http.SendAsync(after);
		Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, limited.StatusCode);
		Assert.NotNull(limited.Headers.RetryAfter);
	}

	// ---------- the collections the lists make

	private static IndexEntry Film(string name, string tmdb) => new() { Id = Guid.NewGuid(), Name = name, Kind = IndexKind.Movie, Tmdb = tmdb };

	private static OutsideList BestPicture() =>
		new("award-best-picture", OutsideSourceId.Wikidata, OutsideListKind.Award, "Best Picture winners", "Academy Award for Best Picture winners, from Wikidata, by year",
			WikidataLists.Parse(OutsideFixtures.Wikidata("award-best-picture"), OutsideListKind.Award)!);

	[Fact]
	public void The_awards_the_library_has_make_Award_collections_in_ceremony_order()
	{
		var list = BestPicture();
		var newest = list.Items[^1].Ref.Tmdb!;
		var oldest = list.Items[0].Ref.Tmdb!;
		var index = IndexSnapshot.Empty with { Entries = [Film("Newest", newest), Film("Oldest", oldest), Film("Not a winner", "1")] };
		var outside = OutsideListsSnapshot.Empty with { Lists = [list] };

		var built = SmartCollectionCatalog.BuildAll(index, Noon, outside: outside).Where(x => x.Kind == SmartCollectionKind.Award).ToList();

		var award = Assert.Single(built);
		Assert.Equal("award:award-best-picture", award.Id);
		Assert.Equal("Best Picture winners", award.Title);
		Assert.Equal("Made by Nixie · Award", award.Eyebrow);
		Assert.Contains("from Wikidata", award.Why, StringComparison.Ordinal);
		Assert.True(award.HasOwnOrder);
		Assert.Equal(["Oldest", "Newest"], award.ItemIds.Select(id => index.Entries.Single(e => e.Id == id).Name));
		Assert.Equal("Best Picture winners, made by Nixie, award, 2 films", award.AutomationName);
	}

	[Fact]
	public void No_award_collection_with_one_title_or_with_Wikidata_off()
	{
		var list = BestPicture();
		var index = IndexSnapshot.Empty with { Entries = [Film("Only one", list.Items[0].Ref.Tmdb!)] };

		Assert.Empty(new AwardBuilder().Build(BuildContext.For(index, Noon) with { Outside = OutsideListsSnapshot.Empty with { Lists = [list] } }));
		var two = index with { Entries = [.. index.Entries, Film("Two", list.Items[1].Ref.Tmdb!)] };
		Assert.Empty(new AwardBuilder().Build(BuildContext.For(two, Noon)));
	}

	[Fact]
	public void The_Awards_row_comes_last_of_the_kinds_and_isnt_a_kind_switch_of_its_own()
	{
		// 1.7.3 (P0-3): after Keywords, the page's last kind; Countries, which it followed, are no longer published
		var order = SmartCollectionKinds.PageOrder.ToList();
		Assert.Equal([SmartCollectionKind.Keyword, SmartCollectionKind.Award], order.TakeLast(2));
		Assert.Equal("Awards", SmartCollectionKinds.Plural(SmartCollectionKind.Award));
		Assert.DoesNotContain(new CollectionsSettingsViewModel(new MemoryStore()).Kinds, x => x.Name == "Awards");
	}

	private static IndexEntry Tagged(string name, string tmdb, params string[] tags) => new() { Id = Guid.NewGuid(), Name = name, Kind = IndexKind.Movie, Tmdb = tmdb, Tags = tags };

	[Fact]
	public void A_finer_genre_joins_its_keyword_and_says_both_sources()
	{
		var heist = new OutsideList("genre-heist", OutsideSourceId.Wikidata, OutsideListKind.Genre, "Heists", "heist films from Wikidata",
			[new(new OutsideRef(Tmdb: "501", Kind: IndexKind.Movie)), new(new OutsideRef(Tmdb: "502", Kind: IndexKind.Movie)), new(new OutsideRef(Tmdb: "1", Kind: IndexKind.Movie))])
		{ KeywordTag = "heist" };
		var entries = Enumerable.Range(1, 3).Select(i => Tagged($"Tagged {i}", $"10{i}", "heist")).Concat([Tagged("Wiki A", "501"), Tagged("Wiki B", "502")]).ToList();
		var index = IndexSnapshot.Empty with { Entries = entries };

		var off = new KeywordBuilder().Build(BuildContext.For(index, Noon)).ToList();
		var on = new KeywordBuilder().Build(BuildContext.For(index, Noon) with { Outside = OutsideListsSnapshot.Empty with { Lists = [heist] } }).ToList();

		Assert.Empty(off); // 3 tagged titles: under the keyword's 5
		var heists = Assert.Single(on);
		Assert.Equal("keyword:heist", heists.Id);
		Assert.Equal("Heists", heists.Title);
		Assert.Equal(5, heists.ItemIds.Count);
		Assert.Equal("Titles with TMDb's keyword “heist”, and heist films from Wikidata", heists.Why); // 1.8 (P1-4): no "Your"
	}

	[Fact]
	public void A_finer_genre_with_no_tagged_titles_makes_its_own_collection_from_Wikidata()
	{
		var slasher = new OutsideList("genre-slasher", OutsideSourceId.Wikidata, OutsideListKind.Genre, "Slashers", "slasher films from Wikidata",
			[.. Enumerable.Range(1, 6).Select(i => new OutsideListItem(new OutsideRef(Tmdb: $"70{i}", Kind: IndexKind.Movie)))])
		{ KeywordTag = "slasher" };
		var index = IndexSnapshot.Empty with { Entries = [.. Enumerable.Range(1, 5).Select(i => Tagged($"S{i}", $"70{i}"))] };

		var made = Assert.Single(new KeywordBuilder().Build(BuildContext.For(index, Noon) with { Outside = OutsideListsSnapshot.Empty with { Lists = [slasher] } }));

		Assert.Equal("Slashers", made.Title);
		Assert.Equal("Slasher films from Wikidata", made.Why); // 1.8 (P1-4): no "Your"
	}

	[Fact]
	public async Task The_lists_service_brings_Wikidatas_lists_from_the_endpoint_setting()
	{
		var clock = new TestClock(Noon);
		var fake = new StrictWikidata(clock.Func);
		var recorder = new OutsideRequestRecorder(fake);
		var store = new MemoryStore();
		var hub = new OutsideHub(new HttpClient(recorder), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), store, clock.Func);
		var lists = new OutsideLists(hub, store, clock.Func);
		store.SaveSetting(SettingKeys.LookUpWikidata, true);

		var main = await lists.RefreshAsync();
		Assert.Equal(WikidataLists.All.Count, main.Lists.Count);

		store.SaveSetting(SettingKeys.WikidataEndpoint, "Next");
		hub.Cache.Clear();
		var next = await lists.RefreshAsync();
		Assert.Empty(next.Lists); // query-next refuses until it opens
		Assert.Contains(recorder.Requests, r => r.Url.Host == "query-next.wikidata.org");
	}
}
