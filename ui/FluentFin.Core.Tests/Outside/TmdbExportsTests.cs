using System.IO.Compression;
using System.Text;
using FluentFin.Core.Outside;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using static FluentFin.Core.Tests.Outside.OutsideKit;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 keyless: TMDb's daily export files (Core\Outside\TmdbExports.cs) against the recorded real files and the strict fake:
/// the newest file (yesterday's before 08:00 UTC), gzip, the cache's weekly refresh and 6-month limit, and what the names do:
/// franchises named as TMDb names them, and the keyword allowlist widened, only while the source is on.
/// </summary>
public sealed class TmdbExportsTests
{
	private static readonly DateTimeOffset Early = new(2026, 11, 21, 7, 59, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset Ready = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Before_08_00_UTC_the_newest_file_is_yesterdays()
	{
		Assert.Equal(new DateOnly(2026, 11, 20), TmdbExports.NewestDay(Early));
		Assert.Equal(new DateOnly(2026, 11, 21), TmdbExports.NewestDay(Ready));
		// 23:30 in Los Angeles on the 20th is 07:30 UTC on the 21st
		Assert.Equal(new DateOnly(2026, 11, 20), TmdbExports.NewestDay(new DateTimeOffset(2026, 11, 20, 23, 30, 0, TimeSpan.FromHours(-8))));
		Assert.Equal("https://files.tmdb.org/p/exports/collection_ids_11_20_2026.json.gz", TmdbExports.FileUri("collection", new DateOnly(2026, 11, 20)).AbsoluteUri);
	}

	[Fact]
	public void The_recorded_collection_file_parses_to_ids_and_names()
	{
		var names = TmdbExports.Parse(OutsideFixtures.TmdbFile(TmdbExports.Collections))!;

		Assert.True(names.Count > 100);
		Assert.Equal("The Mummy Collection", names[1733]);
		Assert.Equal("Star Wars Collection", names[10]);
		Assert.Equal("The Lord of the Rings Collection", names[119]);
	}

	[Fact]
	public void The_recorded_keyword_file_parses_to_ids_and_names()
	{
		var names = TmdbExports.Parse(OutsideFixtures.TmdbFile(TmdbExports.Keywords))!;

		Assert.Equal("heist", names[10051]);
		Assert.Equal("time travel", names[4379]);
		Assert.Equal("slasher", names[12339]);
	}

	[Fact]
	public void Plain_text_parses_too_and_a_bad_line_is_skipped()
	{
		var text = "{\"id\":1,\"name\":\"One\"}\nnot json\n{\"id\":\"2\",\"name\":\"string id\"}\n{\"id\":3,\"name\":\"  \"}\n{\"id\":1,\"name\":\"again\"}\n{\"id\":4,\"name\":\" Four \"}";

		var names = TmdbExports.Parse(Encoding.UTF8.GetBytes(text));

		Assert.Equal(new Dictionary<int, string> { [1] = "One", [4] = "Four" }, names);
	}

	[Fact]
	public void The_cache_form_round_trips_and_is_smaller_than_the_file()
	{
		var names = TmdbExports.Parse(OutsideFixtures.TmdbFile(TmdbExports.Collections))!;
		var cached = TmdbExports.ToCache(names);

		Assert.Equal(names, TmdbExports.FromCache(cached));
		Assert.Equal(new Dictionary<int, string> { [5] = "a b" }, TmdbExports.FromCache(TmdbExports.ToCache(new Dictionary<int, string> { [5] = "a\tb" })));
	}

	private static (OutsideHub Hub, StrictTmdbExports Fake, OutsideRequestRecorder Recorder) Hub(TestClock clock)
	{
		var fake = new StrictTmdbExports(clock.Func);
		var recorder = new OutsideRequestRecorder(fake);
		var store = new MemoryStore();
		var hub = new OutsideHub(new HttpClient(recorder), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), store, clock.Func);
		hub.SetOn(OutsideSourceId.TmdbExports, true);
		return (hub, fake, recorder);
	}

	[Fact]
	public async Task Before_08_00_it_asks_for_yesterdays_file_and_after_for_todays()
	{
		var clock = new TestClock(Early);
		var (hub, fake, _) = Hub(clock);

		var (early, _) = await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.NotNull(early);
		Assert.Equal(["/p/exports/collection_ids_11_20_2026.json.gz"], fake.Asked);

		hub.Cache.Clear();
		clock.Now = Ready;
		await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.Equal("/p/exports/collection_ids_11_21_2026.json.gz", fake.Asked[^1]);
	}

	[Fact]
	public async Task Asking_for_todays_file_before_08_00_is_refused_with_404_by_the_strict_fake()
	{
		var clock = new TestClock(Early);
		var (hub, _, _) = Hub(clock);

		var result = await hub[OutsideSourceId.TmdbExports].GetAsync(TmdbExports.FileUri("collection", new DateOnly(2026, 11, 21)));

		Assert.Equal(OutsideOutcome.Refused, result.Outcome);
		Assert.Equal(404, result.Status);
		Assert.StartsWith("TMDb export files doesn't have that list right now (404).", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task When_todays_file_is_late_after_08_00_yesterdays_is_used()
	{
		var clock = new TestClock(Ready);
		var fake = new StrictTmdbExports(() => Ready.AddHours(-1)); // the fake's day isn't ready yet
		var hub = new OutsideHub(new HttpClient(fake), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), new MemoryStore(), clock.Func);

		var (names, result) = await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);

		Assert.NotNull(names);
		Assert.True(result!.Ok);
		Assert.Equal(["/p/exports/collection_ids_11_21_2026.json.gz", "/p/exports/collection_ids_11_20_2026.json.gz"], fake.Asked);
	}

	[Fact]
	public async Task A_fresh_list_is_read_from_the_cache_and_refreshed_weekly()
	{
		// the owner, 2026-11-22: a gentler refresh, weekly (the names barely change); a minute either side of the week
		var clock = new TestClock(Ready);
		var (hub, fake, _) = Hub(clock);

		await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		clock.Now = Ready + TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1);
		var (cached, none) = await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.Single(fake.Asked);
		Assert.Null(none);
		Assert.Equal("The Mummy Collection", cached![1733]);

		clock.Now = Ready + TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
		await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.Equal(2, fake.Asked.Count);
	}

	[Fact]
	public async Task The_last_good_copy_is_used_up_to_182_days_while_TMDb_cant_be_reached_and_not_a_minute_longer()
	{
		var clock = new TestClock(Ready);
		var (hub, fake, _) = Hub(clock);
		await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		fake.RequireKey = true;   // TMDb refuses from now on

		clock.Now = Ready + OutsideSources.TmdbExports.MaxAge - TimeSpan.FromMinutes(1);
		var (kept, _) = await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.Equal("The Mummy Collection", kept![1733]);

		clock.Now = Ready + OutsideSources.TmdbExports.MaxAge + TimeSpan.FromMinutes(1);
		var (gone, _) = await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.Null(gone);
	}

	[Fact]
	public async Task A_key_requirement_401_keeps_the_cached_names_until_6_months_then_none()
	{
		var clock = new TestClock(Ready);
		var (hub, fake, _) = Hub(clock);
		await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		fake.RequireKey = true;

		clock.Now = Ready.AddDays(8);   // past the weekly refresh: asked again, and refused
		var (kept, refused) = await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.Equal("The Mummy Collection", kept![1733]);
		Assert.Equal(401, refused!.Status);
		Assert.StartsWith("TMDb export files refused the request (401).", refused.Message, StringComparison.Ordinal);

		clock.Now = Ready + OutsideSources.TmdbExports.MaxAge + TimeSpan.FromDays(1);
		var (gone, _) = await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.Null(gone);
	}

	[Fact]
	public async Task A_file_sent_with_Content_Encoding_gzip_is_read_the_same()
	{
		var clock = new TestClock(Ready);
		var handler = new FuncHandler(_ =>
		{
			var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(OutsideFixtures.TmdbFile("collection")) };
			response.Content.Headers.ContentEncoding.Add("gzip"); // the layer undoes it; the parser sees plain lines
			return response;
		});
		var hub = new OutsideHub(new HttpClient(handler), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), new MemoryStore(), clock.Func);

		var (names, _) = await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);

		Assert.Equal("The Mummy Collection", names![1733]);
	}

	// ---------- the lists service: on, off, and what the builders get

	[Fact]
	public async Task Off_by_default_nothing_is_asked_and_the_lists_are_empty()
	{
		var clock = new TestClock(Ready);
		var fake = new StrictTmdbExports(clock.Func);
		var hub = new OutsideHub(new HttpClient(fake), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), new MemoryStore(), clock.Func);
		var lists = new OutsideLists(hub, null, clock.Func);

		var snapshot = await lists.RefreshAsync();

		Assert.True(snapshot.IsEmpty);
		Assert.Empty(fake.Asked);
	}

	[Fact]
	public async Task Turning_the_source_on_brings_the_names_and_turning_it_off_takes_them_away()
	{
		var clock = new TestClock(Ready);
		var fake = new StrictTmdbExports(clock.Func);
		var store = new MemoryStore();
		var hub = new OutsideHub(new HttpClient(fake), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), store, clock.Func);
		var lists = new OutsideLists(hub, store, clock.Func);
		var changes = 0;
		lists.Changed += _ => changes++;

		hub.SetOn(OutsideSourceId.TmdbExports, true);
		var on = await lists.RefreshAsync();
		Assert.Equal("The Mummy Collection", on.FranchiseNames["1733"]);
		Assert.Equal("heist", on.KeywordNames["heist"]);
		Assert.Equal(["/p/exports/collection_ids_11_21_2026.json.gz", "/p/exports/keyword_ids_11_21_2026.json.gz"], fake.Asked.Distinct());

		hub.SetOn(OutsideSourceId.TmdbExports, false);
		var off = await lists.RefreshAsync();
		Assert.True(off.IsEmpty);
		Assert.True(changes >= 2);
		Assert.Same(OutsideListsSnapshot.Empty, OutsideListsSnapshot.Shared); // a test's lists never reach the app's
	}

	private static IndexEntry Film(string name, string collection, int year) =>
		new() { Id = Guid.NewGuid(), Name = name, Kind = IndexKind.Movie, TmdbCollection = collection, Year = year };

	[Fact]
	public void A_franchise_takes_TMDbs_name_when_the_source_is_on_and_keeps_todays_name_when_off()
	{
		var index = IndexSnapshot.Empty with { Entries = [Film("The Mummy", "1733", 1999), Film("The Mummy Returns", "1733", 2001), Film("Alien", "8091", 1979), Film("Aliens", "8091", 1986)] };
		var outside = OutsideListsSnapshot.Empty with { FranchiseNames = new Dictionary<string, string> { ["1733"] = "The Mummy Collection" } };

		var on = new FranchiseBuilder().Build(BuildContext.For(index, Noon) with { Outside = outside }).ToList();
		var off = new FranchiseBuilder().Build(BuildContext.For(index, Noon)).ToList();

		var mummy = Assert.Single(on, x => x.TmdbCollection == "1733");
		Assert.Equal("The Mummy", mummy.Title);
		Assert.Equal("Films in TMDb's “The Mummy Collection”, in release order", mummy.Why); // 1.8 (P1-4): no "Your"
		Assert.Equal("Alien films", Assert.Single(on, x => x.TmdbCollection == "8091").Title); // TMDb's list has no name for it
		Assert.Equal("The Mummy", Assert.Single(off, x => x.TmdbCollection == "1733").Title);
		Assert.Equal("Films that TMDb lists in one collection, in release order", Assert.Single(off, x => x.TmdbCollection == "1733").Why);
	}

	[Theory]
	[InlineData("The Mummy Collection", "The Mummy")]
	[InlineData("Star Wars Collection", "Star Wars")]
	[InlineData("Star Trek: The Original Series Collection", "Star Trek: The Original Series")]
	[InlineData("Trois Couleurs Collection", "Trois Couleurs")]
	[InlineData("Collection", "Collection")]
	[InlineData("The Godfather - Collection", "The Godfather")]
	[InlineData("  Before Trilogy  ", "Before Trilogy")]
	[InlineData(null, "")]
	public void TMDbs_name_drops_its_closing_Collection_word(string? tmdb, string shown) => Assert.Equal(shown, FranchiseNames.FromTmdb(tmdb));

	[Fact]
	public void The_built_collections_follow_the_lists_passed_in()
	{
		var index = IndexSnapshot.Empty with { Entries = [Film("A", "10", 1977), Film("B", "10", 1980)] };
		var outside = OutsideListsSnapshot.Empty with { FranchiseNames = new Dictionary<string, string> { ["10"] = "Star Wars Collection" } };

		Assert.Equal("Star Wars", SmartCollectionCatalog.BuildAll(index, Noon, outside: outside).Single(x => x.Kind == SmartCollectionKind.Franchise).Title);
	}

	// ---------- keyword names confirm the allowlist and add nothing (review #1)

	private static IReadOnlyDictionary<string, string> RecordedKeywordNames(params string[] more) =>
		OutsideLists.KeywordSet(TmdbExports.Parse(OutsideFixtures.TmdbFile(TmdbExports.Keywords))!.Values.Concat(more));

	[Fact]
	public void The_index_keeps_only_the_allowlists_tags_whatever_TMDb_lists()
	{
		var names = RecordedKeywordNames("duringcreditsstinger");
		Assert.True(names.ContainsKey("suicide")); // a real TMDb keyword, in the recorded file
		var dto = new Jellyfin.Sdk.Generated.Models.BaseItemDto
		{
			Id = Guid.NewGuid(),
			Name = "A film",
			Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie,
			Tags = ["suicide", "duringcreditsstinger", "Heist", "helsinki, finland", "my own tag"],
		};

		Assert.Equal(["heist"], IndexEntry.From(dto, Guid.NewGuid())!.Tags);
	}

	[Fact]
	public void TMDbs_names_confirm_an_allowlist_keyword_and_name_nothing_else()
	{
		var names = OutsideLists.KeywordSet(["heist", "submarine", "suicide"]);

		Assert.Equal("Heists", Keywords.NameOf("heist", names));
		Assert.Equal("Heists", Keywords.NameOf(" Heist ", names));
		Assert.Null(Keywords.NameOf("submarine", names));   // TMDb's, not the allowlist's
		Assert.Null(Keywords.NameOf("suicide", names));     // never a collection
		Assert.Null(Keywords.NameOf("zombie", names));      // the allowlist's, but TMDb doesn't confirm it
		Assert.Equal("Zombies", Keywords.NameOf("zombie", new Dictionary<string, string>())); // the export off: the allowlist alone
	}

	[Fact]
	public void Harmful_and_junk_TMDb_keywords_never_make_a_collection_or_the_collection_of_the_day()
	{
		static IndexEntry Tagged(string tag, int i) => new() { Id = Guid.NewGuid(), Name = $"{tag} {i}", Kind = IndexKind.Movie, Tags = [tag] };
		var entries = new List<IndexEntry>();
		foreach (var tag in new[] { "suicide", "duringcreditsstinger", "helsinki, finland", "underdog" })
		{
			entries.AddRange(Enumerable.Range(0, 40).Select(i => Tagged(tag, i)));
		}

		entries.AddRange(Enumerable.Range(0, 6).Select(i => Tagged("heist", i)));
		var index = IndexSnapshot.Empty with { Entries = entries };
		var outside = OutsideListsSnapshot.Empty with { KeywordNames = RecordedKeywordNames("duringcreditsstinger") };

		var all = SmartCollectionCatalog.BuildAll(index, Noon, outside: outside);

		Assert.Equal(["Heists"], all.Where(x => x.Kind == SmartCollectionKind.Keyword).Select(x => x.Title));
		var junk = new[] { "suicide", "duringcreditsstinger", "helsinki", "underdog" };
		Assert.DoesNotContain(all, x => junk.Any(j => x.Title.Contains(j, StringComparison.OrdinalIgnoreCase) || x.Why.Contains(j, StringComparison.OrdinalIgnoreCase)));
		for (var day = 0; day < 60; day++)
		{
			var today = SmartCollectionCatalog.OfTheDay(all, Noon.AddDays(day));
			Assert.True(today is null || !junk.Any(j => today.Title.Contains(j, StringComparison.OrdinalIgnoreCase)));
		}
	}

	private static byte[] Gzip(string text)
	{
		using var output = new MemoryStream();
		using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
		{
			gz.Write(Encoding.UTF8.GetBytes(text));
		}

		return output.ToArray();
	}

	// ---------- a bad file or a failed write stops nothing (review #3)

	/// <summary>The recorded collection file with its gzip cut in half: a 200 that can't be read.</summary>
	private static byte[] CutFile()
	{
		var whole = OutsideFixtures.TmdbFile(TmdbExports.Collections);
		return whole[..(whole.Length / 2)];
	}

	[Fact]
	public void A_gzip_cut_in_the_middle_is_unreadable_not_half_a_list()
	{
		Assert.Null(TmdbExports.Parse(CutFile()));
		Assert.Null(TmdbExports.Parse([0x1f, 0x8b, 0x08, 0x00, 0xde, 0xad]));
		// a whole gzip of a file whose last line is cut still gives its whole lines
		Assert.Equal(new Dictionary<int, string> { [1] = "One" }, TmdbExports.Parse(Gzip("{\"id\":1,\"name\":\"One\"}\n{\"id\":2,\"na")));
	}

	/// <summary>files.tmdb.org answering 200 with a cut file (counted), everything else to the strict Wikidata fake.</summary>
	private sealed class CutTmdb(Func<DateTimeOffset> clock) : HttpMessageHandler
	{
		public int TmdbAsked { get; private set; }

		public bool Throw { get; set; }

		public StrictWikidata Wikidata { get; } = new(clock);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri!.Host != "files.tmdb.org")
			{
				return new HttpMessageInvoker(Wikidata, disposeHandler: false).SendAsync(request, cancellationToken);
			}

			TmdbAsked++;
			if (Throw)
			{
				throw new InvalidOperationException("not a network failure: one the loaders don't expect");
			}

			var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(CutFile()) };
			response.Content.Headers.ContentType = new("application/octet-stream");
			return Task.FromResult(response);
		}
	}

	[Fact]
	public async Task A_cut_file_keeps_the_cached_names_counts_a_failure_and_isnt_downloaded_again_for_an_hour()
	{
		var clock = new TestClock(Ready);
		var good = new StrictTmdbExports(clock.Func);
		var cut = new CutTmdb(clock.Func);
		var useCut = false;
		var handler = new FuncHandler((r, ct) => useCut ? new HttpMessageInvoker(cut, false).SendAsync(r, ct) : new HttpMessageInvoker(good, false).SendAsync(r, ct));
		var hub = new OutsideHub(new HttpClient(handler), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), new MemoryStore(), clock.Func);
		await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);   // cached today
		useCut = true;
		clock.Now = Ready + OutsideSources.TmdbExports.RefreshAfter + TimeSpan.FromHours(1); // stale (a week, 2cd31272): asked again, and the file is cut

		var (kept, result) = await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);

		Assert.Equal("The Mummy Collection", kept![1733]);
		Assert.Equal(OutsideOutcome.Failed, result!.Outcome);
		Assert.Equal("TMDb export files sent a list Nixie couldn't read. Nixie keeps the lists it has and tries again later.", result.Message);
		Assert.Equal(result, hub[OutsideSourceId.TmdbExports].Last);
		Assert.Equal(1, cut.TmdbAsked);

		clock.Now = clock.Now.AddMinutes(59);
		await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.Equal(1, cut.TmdbAsked); // not on every visit
		clock.Now = clock.Now.AddMinutes(2);
		await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		Assert.Equal(2, cut.TmdbAsked);
	}

	[Fact]
	public async Task Cut_files_count_as_a_run_of_failures()
	{
		var clock = new TestClock(Ready);
		var hub = new OutsideHub(new HttpClient(new CutTmdb(clock.Func)), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), new MemoryStore(), clock.Func);

		for (var i = 0; i < OutsideSources.ErrorsInARow; i++)
		{
			Assert.Null((await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func)).Names);
			clock.Now = clock.Now + OutsideSources.RetryAfterFailure + TimeSpan.FromMinutes(1);
		}

		Assert.NotNull(hub[OutsideSourceId.TmdbExports].LastFailedAt);
		Assert.Equal(OutsideSources.ErrorPause, hub[OutsideSourceId.TmdbExports].Last!.PausedUntil - hub[OutsideSourceId.TmdbExports].LastFailedAt);
	}

	[Theory]
	[InlineData(false)] // a 200 with a cut file
	[InlineData(true)]  // an exception the loaders don't expect
	public async Task A_failing_TMDb_source_still_lets_Wikidata_run(bool throws)
	{
		var clock = new TestClock(Ready);
		var store = new MemoryStore();
		var handler = new CutTmdb(clock.Func) { Throw = throws };
		var hub = new OutsideHub(new HttpClient(handler), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), store, clock.Func);
		var lists = new OutsideLists(hub, store, clock.Func);
		var failures = new List<Exception>();
		lists.Failed += failures.Add;
		store.SaveSetting(Settings.SettingKeys.LookUpTmdbExports.Name, true);
		store.SaveSetting(Settings.SettingKeys.LookUpWikidata.Name, true);

		var snapshot = await lists.RefreshAsync();

		Assert.Equal(WikidataLists.All.Count, snapshot.Lists.Count);
		Assert.Empty(snapshot.FranchiseNames);
		Assert.Equal(OutsideOutcome.Failed, hub[OutsideSourceId.TmdbExports].Last!.Outcome);
		Assert.Equal(throws ? 1 : 0, failures.Count);
	}

	[Fact]
	public async Task A_cache_that_cant_be_written_still_gives_this_runs_names_and_lists()
	{
		var clock = new TestClock(Ready);
		var store = new MemoryStore();
		var notAFolder = Path.Combine(TempFolder(), "file");
		File.WriteAllText(notAFolder, "a file where the cache folder should be");
		var router = new FuncHandler((r, ct) => r.RequestUri!.Host == "files.tmdb.org"
			? new HttpMessageInvoker(new StrictTmdbExports(clock.Func), true).SendAsync(r, ct)
			: new HttpMessageInvoker(new StrictWikidata(clock.Func), true).SendAsync(r, ct));
		var hub = new OutsideHub(new HttpClient(router), OutsideRouting.Real, "1.7.0", new OutsideCache(notAFolder, clock.Func), store, clock.Func);
		var notes = new List<string>();
		hub.Noted += notes.Add;
		var lists = new OutsideLists(hub, store, clock.Func);
		store.SaveSetting(Settings.SettingKeys.LookUpTmdbExports.Name, true);
		store.SaveSetting(Settings.SettingKeys.LookUpWikidata.Name, true);

		var snapshot = await lists.RefreshAsync();

		Assert.Equal("The Mummy Collection", snapshot.FranchiseNames["1733"]);
		Assert.Equal(WikidataLists.All.Count, snapshot.Lists.Count);
		Assert.Contains(notes, n => n.Contains("couldn't be kept on disk", StringComparison.Ordinal));
	}
}
