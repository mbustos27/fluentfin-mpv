using System.Net;
using FluentFin.Core.Outside;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 trending: Trakt and TMDb trending on the outside layer: off by default, their headers, paging, a bare 429, the cache's
/// ages, a source turned off, the key and token never in a log line or an address, no image ever asked for, and the privacy
/// test: nothing of the library leaves, and the requests are the same whatever the library holds.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TrendingSourcesTests
{
	private static void SomeLists(TrendingFake fake)
	{
		fake.Trakt["movies/trending"] = TrendingFake.Unowned("Film", 240);
		fake.Trakt["shows/trending"] = TrendingFake.Unowned("Show", 679, 1000);
		fake.Trakt["movies/popular"] = TrendingFake.Unowned("Popular film", 486, 3000);
		fake.Trakt["shows/popular"] = TrendingFake.Unowned("Popular show", 30, 4000);
		fake.Trakt["movies/anticipated"] = TrendingFake.Unowned("Coming film", 12, 5000);
		fake.Trakt["shows/anticipated"] = TrendingFake.Unowned("Coming show", 12, 6000);
		fake.Trakt["movies/boxoffice"] = TrendingFake.Unowned("Box film", 10, 7000);
		fake.Tmdb["movie"] = [.. Enumerable.Range(1, 240).Select(i => (500_000 + i, $"TMDb film {i}"))];
		fake.Tmdb["tv"] = [.. Enumerable.Range(1, 240).Select(i => (600_000 + i, $"TMDb show {i}"))];
	}

	[Fact]
	public async Task BothAreOffByDefault_AndNothingIsSent()
	{
		Assert.False(SettingKeys.LookUpTrakt.Default.Value);
		Assert.False(SettingKeys.LookUpTmdbTrending.Default.Value);
		using var rig = new TrendingRig(token: TrendingFake.Token);
		SomeLists(rig.Fake);

		await rig.Sources.RefreshAsync();

		Assert.Equal(0, rig.Fake.Calls);
		Assert.Empty(rig.Sources.Current().Lists);
		Assert.False(rig.Sources.AnyActive);
	}

	[Fact]
	public async Task TraktOn_InABuildWithoutAClientId_SendsNothing_AndTmdbOnWithoutAToken_SendsNothing()
	{
		using var rig = new TrendingRig(clientId: null, trakt: true, tmdb: true);
		SomeLists(rig.Fake);

		await rig.Sources.RefreshAsync();

		Assert.Equal(0, rig.Fake.Calls);
		Assert.False(rig.Sources.TraktAvailable);
		Assert.Equal("Not available in this build.", TrendingStatus.Describe(rig.Sources, OutsideSourceId.Trakt));
		Assert.Equal(TrendingStatus.TmdbNotAvailable, TrendingStatus.Describe(rig.Sources, OutsideSourceId.TmdbTrending));
		Assert.False(rig.Sources.HasTmdbToken);
	}

	[Fact]
	public async Task NixiesOwnTmdbToken_IsUsed_WithoutTheUsers_AndTheUsersWins()
	{
		// the owner, 2026-11-22: a release builds Nixie's own TMDb token in; the user's own, entered in Settings, wins
		using var rig = new TrendingRig(tmdb: true, appToken: TrendingFake.AppToken);
		SomeLists(rig.Fake);
		Assert.True(rig.Sources.HasAppTmdbToken);
		Assert.False(rig.Sources.HasUserTmdbToken);

		await rig.Sources.RefreshAsync();

		Assert.Equal(2 * TmdbLists.Pages, rig.Recorder.Requests.Count);
		Assert.All(rig.Recorder.Requests, r =>
		{
			Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer " + TrendingFake.AppToken), r.Headers);
			Assert.DoesNotContain(TrendingFake.AppToken, r.Url.OriginalString);
		});
		Assert.Equal(2, rig.Sources.Current().Lists.Count);

		// the user's own token: it is the one sent from now on
		Assert.Null(rig.Token.Save(TrendingFake.Token));
		await rig.Sources.RefreshAsync();   // the round the new token starts (the lists are still fresh: nothing asked)
		rig.Clock.Now += TimeSpan.FromDays(2);
		var before = rig.Recorder.Requests.Count;
		await rig.Sources.RefreshAsync();
		var after = rig.Recorder.Requests.Skip(before).ToList();
		Assert.NotEmpty(after);
		Assert.All(after, r => Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer " + TrendingFake.Token), r.Headers));

		// a malformed built-in token is no token
		Assert.False(new TrendingSources(rig.Hub, null, null, rig.Clock.Func, "not a token").HasTmdbToken);
	}

	[Fact]
	public async Task NixiesOwnTmdbToken_Refused_IsSaidPlainly_AndNotTriedAgain_TillTheUserEntersTheirs()
	{
		// the terms re-check of 2026-11-22: the token's rotation isn't self-service, so a refused app key must be said plainly
		// and must not be retried in a loop
		using var rig = new TrendingRig(tmdb: true, appToken: TrendingFake.AppToken);
		SomeLists(rig.Fake);
		rig.Fake.RefuseAppToken = HttpStatusCode.Unauthorized;

		await rig.Sources.RefreshAsync();

		Assert.Equal(1, rig.Fake.Calls);
		Assert.True(rig.Sources.AppTmdbTokenRefused);
		Assert.False(rig.Sources.HasTmdbToken);
		Assert.Equal("TMDb refused Nixie's key. You can enter your own TMDb token below.", TrendingStatus.Describe(rig.Sources, OutsideSourceId.TmdbTrending));
		for (var i = 0; i < 3; i++)
		{
			rig.Clock.Now += TimeSpan.FromDays(2);
			await rig.Sources.RefreshAsync();
		}

		Assert.Equal(1, rig.Fake.Calls);   // not again: no loop, no retry on the next page visit

		// the user's own token brings it back
		Assert.Null(rig.Token.Save(TrendingFake.Token));
		await rig.Sources.RefreshAsync();
		Assert.True(rig.Fake.Calls > 1);
		Assert.Equal(2, rig.Sources.Current().Lists.Count);
		Assert.Equal(TrendingStatus.UpToDate, TrendingStatus.Describe(rig.Sources, OutsideSourceId.TmdbTrending));
	}

	[Fact]
	public async Task TraktsRequests_CarryItsHeaders_AndOnlyTheListAddresses()
	{
		using var rig = new TrendingRig(trakt: true);
		SomeLists(rig.Fake);

		await rig.Sources.RefreshAsync();

		var asked = rig.Recorder.Requests;
		Assert.NotEmpty(asked);
		Assert.All(asked, r =>
		{
			Assert.Equal("GET", r.Method);
			Assert.Equal("api.trakt.tv", r.Url.Host);
			Assert.Equal("https", r.Url.Scheme);
			Assert.Equal("Nixie/1.7.0 (https://github.com/mbustos27/fluentfin-mpv)", string.Join(" ", r.Headers.Where(h => h.Key == "User-Agent").Select(h => h.Value)));
			Assert.Contains(new KeyValuePair<string, string>("trakt-api-version", "2"), r.Headers);
			Assert.Contains(new KeyValuePair<string, string>("trakt-api-key", TrendingFake.ClientId), r.Headers);
			Assert.Contains(r.Headers, h => h.Key == "Content-Type" && h.Value.StartsWith("application/json", StringComparison.Ordinal));
			Assert.Equal("", r.Body);
			Assert.DoesNotContain(TrendingFake.ClientId, r.Url.OriginalString);
		});
		// every list once at least, and nothing else: the fixed addresses of TraktLists
		var paths = asked.Select(r => r.Url.PathAndQuery).ToList();
		Assert.All(paths, p => Assert.Matches(@"^/(movies|shows)/(trending|popular|anticipated)\?page=\d&limit=250$|^/movies/boxoffice$", p));
		Assert.Equal(TraktLists.All.Count, paths.Select(p => p.Split('?')[0]).Distinct().Count());
		Assert.Empty(rig.To("api.themoviedb.org"));
	}

	[Fact]
	public async Task TmdbsRequests_CarryTheTokenAsABearerOnly_NeverInTheAddress()
	{
		using var rig = new TrendingRig(tmdb: true, token: TrendingFake.Token);
		SomeLists(rig.Fake);

		await rig.Sources.RefreshAsync();

		var asked = rig.Recorder.Requests;
		Assert.Equal(2 * TmdbLists.Pages, asked.Count); // five pages each of films and shows
		Assert.All(asked, r =>
		{
			Assert.Equal("api.themoviedb.org", r.Url.Host);
			Assert.Matches(@"^/3/trending/(movie|tv)/week\?page=[1-5]$", r.Url.PathAndQuery);
			Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer " + TrendingFake.Token), r.Headers);
			Assert.DoesNotContain("api_key", r.Url.OriginalString);
			Assert.DoesNotContain(TrendingFake.Token, r.Url.OriginalString);
		});
		var lists = rig.Sources.Current().Lists;
		Assert.Equal([ListedKind.Movie, ListedKind.Series], lists.Select(l => l.Kind));
		Assert.All(lists, l => Assert.Equal(100, l.Titles.Count));
	}

	[Fact]
	public async Task Paging_FollowsTraktsCount_AndReadsPastAPlaceholderCountUntilAnEmptyPage()
	{
		using var rig = new TrendingRig(trakt: true);
		SomeLists(rig.Fake);

		await rig.Sources.RefreshAsync();

		int Pages(string list) => rig.Recorder.Requests.Count(r => r.Url.AbsolutePath == "/" + list);
		Assert.Equal(1, Pages("movies/trending"));     // 240: one short page, the count says 1
		Assert.Equal(3, Pages("shows/trending"));      // 679: three pages, as counted
		Assert.Equal(2, Pages("movies/popular"));      // 486
		Assert.Equal(1, Pages("movies/boxoffice"));    // never paged
		var shows = rig.Sources.Current().Lists.Single(l => l.Source == OutsideSourceId.Trakt && l.List == TrendingList.Trending && l.Kind == ListedKind.Series);
		Assert.Equal(679, shows.Titles.Count);
		Assert.Equal(Enumerable.Range(1, 679), shows.Titles.Select(t => t.Rank));

		// placeholder counts (1 on every page): full pages are read on until the empty one, never past four pages
		using var placeholder = new TrendingRig(trakt: true);
		SomeLists(placeholder.Fake);
		placeholder.Fake.Counts = "placeholder";
		await placeholder.Sources.RefreshAsync();
		int PlaceholderPages(string list) => placeholder.Recorder.Requests.Count(r => r.Url.AbsolutePath == "/" + list);
		Assert.Equal(3, PlaceholderPages("shows/trending"));   // 250, 250, 179 (short: the end)
		Assert.Equal(2, PlaceholderPages("movies/popular"));   // 250, 236
		Assert.Equal(486, placeholder.Sources.Current().Lists.Single(l => l.List == TrendingList.Popular && l.Kind == ListedKind.Movie).Titles.Count);
		placeholder.Fake.Trakt["shows/popular"] = TrendingFake.Unowned("Many", 2000, 9000);
		placeholder.Clock.Now += TimeSpan.FromHours(7);
		await placeholder.Sources.RefreshAsync();
		Assert.Equal(1 + TraktLists.MaxPages, PlaceholderPages("shows/popular"));
	}

	[Fact]
	public async Task ABare429_PausesTrakt_IsNeverRetriedInsideTheRequest_AndItsOtherListsWait()
	{
		using var rig = new TrendingRig(trakt: true);
		SomeLists(rig.Fake);
		rig.Fake.Fail = HttpStatusCode.TooManyRequests;

		await rig.Sources.RefreshAsync();

		Assert.Equal(1, rig.Fake.Calls);
		Assert.NotNull(rig.Hub[OutsideSourceId.Trakt].PausedUntil);
		Assert.StartsWith("Trakt asked Nixie to slow down.", TrendingStatus.Describe(rig.Sources, OutsideSourceId.Trakt, OutsideKit.Noon));

		rig.Fake.Fail = null;
		await rig.Sources.RefreshAsync();
		Assert.Equal(1, rig.Fake.Calls); // still paused: nothing sent
		rig.Clock.Now += OutsideSources.DefaultPause + TimeSpan.FromSeconds(1);
		await rig.Sources.RefreshAsync();
		Assert.True(rig.Fake.Calls > 1);
		Assert.Equal(TrendingStatus.UpToDate, TrendingStatus.Describe(rig.Sources, OutsideSourceId.Trakt));
	}

	[Theory]
	[InlineData(HttpStatusCode.Forbidden, "Trakt turned this copy of Nixie away. Its registration may have lapsed; a Nixie update fixes that.", 1)]
	[InlineData(HttpStatusCode.Unauthorized, "Trakt turned this copy of Nixie away. Its registration may have lapsed; a Nixie update fixes that.", 1)]
	// a 502 is one list's: the next list is asked, until three in a row pause the source (OutsideSources.ErrorsInARow)
	[InlineData(HttpStatusCode.BadGateway, "Trakt is having trouble right now. Nixie asks again later.", 3)]
	public async Task ARefusal_IsSaidInNixiesWords_AndTheListsNixieHasStay(HttpStatusCode status, string words, int asked)
	{
		using var rig = new TrendingRig(trakt: true);
		SomeLists(rig.Fake);
		await rig.Sources.RefreshAsync();
		var before = rig.Sources.Current().Lists.Count;

		rig.Clock.Now += TimeSpan.FromHours(7);
		rig.Fake.Fail = status;
		var calls = rig.Fake.Calls;
		await rig.Sources.RefreshAsync();

		Assert.Equal(calls + asked, rig.Fake.Calls); // 401 or 403: Trakt's other lists aren't asked this round
		Assert.Equal(words, TrendingStatus.Describe(rig.Sources, OutsideSourceId.Trakt));
		Assert.Equal(before, rig.Sources.Current().Lists.Count);
	}

	[Fact]
	public async Task OneListsRefusal_DoesntEndTheRound_TheOtherListsAreAsked()
	{
		// the review's #4: a 404 for one list (an address Trakt retired) stopped every list after it
		using var rig = new TrendingRig(trakt: true);
		SomeLists(rig.Fake);
		rig.Fake.Trakt.Remove("movies/popular");   // the fake answers 404 for it

		await rig.Sources.RefreshAsync();

		var lists = rig.Sources.Current().Lists.Select(l => (l.List, l.Kind)).ToList();
		Assert.DoesNotContain((TrendingList.Popular, ListedKind.Movie), lists);
		Assert.Contains((TrendingList.Popular, ListedKind.Series), lists);
		Assert.Contains((TrendingList.Anticipated, ListedKind.Series), lists);
		Assert.Contains((TrendingList.BoxOffice, ListedKind.Movie), lists);
		Assert.Contains(rig.Recorder.Requests, r => r.Url.AbsolutePath == "/movies/boxoffice");
	}

	[Fact]
	public async Task TmdbsNoToTheToken_SaysCheckTheToken()
	{
		using var rig = new TrendingRig(tmdb: true, token: "eyJhbGciOiJIUzI1NiJ9.eyJ3cm9uZyI6dHJ1ZX0.d3Jvbmc");
		SomeLists(rig.Fake);

		await rig.Sources.RefreshAsync();

		Assert.Equal("TMDb didn't accept your token. Check the API Read Access Token in Settings.", TrendingStatus.Describe(rig.Sources, OutsideSourceId.TmdbTrending));
		Assert.Empty(rig.Sources.Current().Lists);
	}

	[Fact]
	public async Task TheCache_KeepsAListFor6Hours_TheBoxOfficeUntilMonday_AndTmdbsADay()
	{
		using var rig = new TrendingRig(trakt: true, tmdb: true, token: TrendingFake.Token);
		SomeLists(rig.Fake);
		await rig.Sources.RefreshAsync();
		var first = rig.Fake.Calls;

		rig.Clock.Now += TimeSpan.FromHours(5);
		await rig.Sources.RefreshAsync();
		Assert.Equal(first, rig.Fake.Calls); // all fresh: nothing asked

		rig.Clock.Now += TimeSpan.FromHours(2); // 7 hours: Trakt's paged lists again; the box office (Saturday noon → Sunday noon) and TMDb (a day) not
		await rig.Sources.RefreshAsync();
		var again = rig.Recorder.Requests.Skip(first).ToList();
		Assert.NotEmpty(again);
		Assert.All(again, r => Assert.Equal("api.trakt.tv", r.Url.Host));
		Assert.DoesNotContain(again, r => r.Url.AbsolutePath == "/movies/boxoffice");

		// a new start: the lists come from the cache, nothing asked
		var restarted = new TrendingSources(rig.Hub, rig.Token, TrendingFake.ClientId, rig.Clock.Func);
		var calls = rig.Fake.Calls;
		Assert.Equal(rig.Sources.Current().Lists.Count, restarted.Current().Lists.Count);
		await restarted.RefreshAsync();
		Assert.Equal(calls, rig.Fake.Calls);

	}

	[Fact]
	public async Task AList_ShowsUntilItsSourcesMaximumAge_NotAMinuteLonger_FromMemoryOrTheCache()
	{
		// the review's #8: this was checked at +8 days, where any rule passes; now just before and just after each limit
		using var rig = new TrendingRig(trakt: true, tmdb: true, token: TrendingFake.Token);
		SomeLists(rig.Fake);
		await rig.Sources.RefreshAsync();   // every list read at noon
		rig.Fake.Fail = HttpStatusCode.BadGateway;
		IReadOnlyList<OutsideSourceId> Shown(TrendingSources sources) => [.. sources.Current().Lists.Select(l => l.Source).Distinct().Order()];
		TrendingSources Restarted() => new(rig.Hub, rig.Token, TrendingFake.ClientId, rig.Clock.Func);

		rig.Clock.Now = OutsideKit.Noon + OutsideSources.Trakt.MaxAge - TimeSpan.FromMinutes(1);
		Assert.Equal([OutsideSourceId.Trakt, OutsideSourceId.TmdbTrending], Shown(rig.Sources));
		Assert.Equal([OutsideSourceId.Trakt, OutsideSourceId.TmdbTrending], Shown(Restarted()));

		rig.Clock.Now = OutsideKit.Noon + OutsideSources.Trakt.MaxAge + TimeSpan.FromMinutes(1);
		Assert.Equal([OutsideSourceId.TmdbTrending], Shown(rig.Sources));
		Assert.Equal([OutsideSourceId.TmdbTrending], Shown(Restarted()));

		rig.Clock.Now = OutsideKit.Noon + OutsideSources.TmdbTrending.MaxAge - TimeSpan.FromMinutes(1);
		Assert.Equal([OutsideSourceId.TmdbTrending], Shown(rig.Sources));
		rig.Clock.Now = OutsideKit.Noon + OutsideSources.TmdbTrending.MaxAge + TimeSpan.FromMinutes(1);
		Assert.Empty(Shown(rig.Sources));
		Assert.Empty(Shown(Restarted()));
	}

	[Fact]
	public async Task AListTheCacheCantWrite_IsStillShownForThisRun_AndTheRoundGoesOn()
	{
		// a8a5e5d: a full disk or a locked folder kept a list from being written; the round faulted and the list was lost
		using var rig = new TrendingRig(trakt: true);
		SomeLists(rig.Fake);
		Directory.CreateDirectory(Path.Combine(rig.Folder, "Outside"));
		File.WriteAllText(Path.Combine(rig.Folder, "Outside", OutsideSources.Trakt.Key), "a file where Trakt's folder goes");

		await rig.Sources.RefreshAsync();   // doesn't throw

		Assert.Equal(TraktLists.All.Count, rig.Sources.Current().Lists.Count);
		Assert.Empty(Directory.GetDirectories(Path.Combine(rig.Folder, "Outside")));
	}

	[Fact]
	public async Task TurningASourceOff_ForgetsItsLists_InMemoryAndInTheCache_AndTellsThePages()
	{
		using var rig = new TrendingRig(trakt: true, tmdb: true, token: TrendingFake.Token);
		SomeLists(rig.Fake);
		await rig.Sources.RefreshAsync();
		Assert.True(Directory.Exists(Path.Combine(rig.Folder, "Outside", "trakt")));
		var changed = 0;
		rig.Sources.Changed += () => changed++;

		rig.Hub.SetOn(OutsideSourceId.Trakt, false);

		Assert.True(changed >= 1);
		Assert.DoesNotContain(rig.Sources.Current().Lists, l => l.Source == OutsideSourceId.Trakt);
		Assert.Contains(rig.Sources.Current().Lists, l => l.Source == OutsideSourceId.TmdbTrending);
		Assert.False(Directory.Exists(Path.Combine(rig.Folder, "Outside", "trakt")));
		Assert.Equal(TrendingStatus.Off, TrendingStatus.Describe(rig.Sources, OutsideSourceId.Trakt));

		// removing the token: TMDb's lists aren't shown (nothing is asked without one)
		rig.Token.Clear();
		Assert.Empty(rig.Sources.Current().Lists);
	}

	[Theory]
	[InlineData(OutsideSourceId.Trakt)]
	[InlineData(OutsideSourceId.TmdbTrending)]
	public async Task ASourceTurnedOffMidList_SendsNoFurtherPage(OutsideSourceId id)
	{
		// the review's #2: the page loops read the switch before every request, not only between lists
		using var rig = new TrendingRig(trakt: id == OutsideSourceId.Trakt, tmdb: id == OutsideSourceId.TmdbTrending, token: TrendingFake.Token);
		SomeLists(rig.Fake);
		rig.Fake.Trakt["movies/trending"] = TrendingFake.Unowned("Long", 900);   // four full pages
		var turnedOff = false;
		rig.Fake.OnRequest = _ =>
		{
			if (!turnedOff)
			{
				turnedOff = true;
				rig.Hub.SetOn(id, false);
			}
		};

		await rig.Sources.RefreshAsync();

		Assert.Equal(1, rig.Fake.Calls);
		Assert.Empty(rig.Sources.Current().Lists);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task TheClientIdAndTheToken_NeverAppearInALogLineAMessageOrAnAddress(bool nixiesOwnToken)
	{
		// with the user's token, and with Nixie's own (built in), refused or not
		using var rig = nixiesOwnToken
			? new TrendingRig(trakt: true, tmdb: true, appToken: TrendingFake.AppToken)
			: new TrendingRig(trakt: true, tmdb: true, token: TrendingFake.Token);
		SomeLists(rig.Fake);
		await rig.Sources.RefreshAsync();
		rig.Clock.Now += TimeSpan.FromDays(2);
		rig.Fake.Fail = HttpStatusCode.Unauthorized;
		await rig.Sources.RefreshAsync();

		var said = rig.Notes.Concat(rig.Recorder.Requests.Select(r => r.Url.OriginalString))
			.Concat(TrendingSources.Ids.Select(id => TrendingStatus.Describe(rig.Sources, id)))
			.Concat(TrendingSources.Ids.Select(id => rig.Hub[id].Last?.Message ?? ""))
			.Append(rig.Token.ToString()).ToList();
		Assert.NotEmpty(rig.Notes);
		Assert.All(said, line =>
		{
			Assert.DoesNotContain(TrendingFake.ClientId, line);
			Assert.DoesNotContain(TrendingFake.Token, line);
			Assert.DoesNotContain(TrendingFake.AppToken, line);
			Assert.DoesNotContain("eyJ", line);
		});
		// the log names the source and the list's path, never a query
		Assert.All(rig.Notes, n => Assert.DoesNotContain("?", n));
	}

	[Theory]
	[InlineData("https://cdn.example.org/movies/trending", false)]
	[InlineData("https://api.trakt.tv/movies/trending?page=1&limit=250&moved=1", true)]
	public async Task TraktsClientId_AndTmdbsToken_NeverFollowARedirectToAnotherHost(string location, bool sameHost)
	{
		// the review's #5: the layer dropped Authorization on a cross-host redirect, but not trakt-api-key
		var seen = new List<HttpRequestMessage>();
		var handler = new FuncHandler(r =>
		{
			seen.Add(r);
			return seen.Count == 1
				? OutsideKit.Answer(HttpStatusCode.Found, "", x => x.Headers.Location = new Uri(location))
				: OutsideKit.Answer(HttpStatusCode.OK, "[]");
		});
		var source = OutsideKit.Source(handler, new TestClock(OutsideKit.Noon), OutsideSources.Trakt);
		var headers = TraktLists.Headers(TrendingFake.ClientId).Append(new("Authorization", "Bearer " + TrendingFake.Token)).ToDictionary(h => h.Key, h => h.Value);

		var result = await source.GetAsync(new Uri("https://api.trakt.tv/movies/trending?page=1&limit=250"), headers);

		Assert.True(result.Ok);
		Assert.Equal(2, seen.Count);
		Assert.True(seen[0].Headers.Contains("trakt-api-key"));
		Assert.Equal(sameHost, seen[1].Headers.Contains("trakt-api-key"));
		Assert.Equal(sameHost, seen[1].Headers.Contains("Authorization"));
		Assert.True(seen[1].Headers.Contains("trakt-api-version"));   // not a key: it follows
		Assert.True(OutsideSource.IsKeyHeader("TRAKT-API-KEY"));
	}

	[Fact]
	public async Task NoImage_IsEverAskedFor_AndNoHostButTraktsAndTmdbs()
	{
		using var rig = new TrendingRig(trakt: true, tmdb: true, token: TrendingFake.Token);
		SomeLists(rig.Fake);

		await rig.Sources.RefreshAsync();
		_ = TrendingBuilder.Build(TrendingRig.Index(TrendingRig.Entry("Owned", IndexKind.Movie, tmdb: "500001")), rig.Sources.Current());

		Assert.NotEmpty(rig.Recorder.Requests);
		Assert.All(rig.Recorder.Requests, r =>
		{
			Assert.Contains(r.Url.Host, new[] { "api.trakt.tv", "api.themoviedb.org" });
			Assert.DoesNotContain("/t/p/", r.Url.AbsolutePath);
			Assert.DoesNotContain(".jpg", r.Url.AbsolutePath);
			Assert.DoesNotContain(r.Headers, h => h.Key == "Accept" && h.Value.Contains("image", StringComparison.OrdinalIgnoreCase));
		});
	}

	[Fact]
	public async Task InAHarnessRun_EveryRequestGoesToTheFake_NeverToARealHost()
	{
		var origin = new Uri("http://127.0.0.1:45678/");
		using var rig = new TrendingRig(trakt: true, tmdb: true, token: TrendingFake.Token, routing: OutsideRouting.Harness(origin));

		await rig.Sources.RefreshAsync();

		Assert.NotEmpty(rig.Recorder.Requests);
		Assert.All(rig.Recorder.Requests, r =>
		{
			Assert.Equal("127.0.0.1", r.Url.Host);
			Assert.Matches("^/__harness/outside/(api\\.trakt\\.tv|api\\.themoviedb\\.org)/", r.Url.AbsolutePath);
		});
		Assert.Equal(TraktClientId.HarnessValue, TraktClientId.Resolve(harness: true, []));
	}

	/// <summary>
	/// The privacy test (plan section 3), for Trakt and TMDb trending: both on against the strict fake, with a library whose
	/// titles are on the lists. Every request is recorded; none carries an id, a title or a name of the library, and the
	/// requests are the same, one by one, with another library or none.
	/// </summary>
	[Fact]
	public async Task Privacy_NothingOfTheLibraryLeaves_AndTheRequestsDontDependOnIt()
	{
		var library = TrendingRig.Index(
			TrendingRig.Entry("Kestrel Bay Requiem", IndexKind.Movie, tmdb: "9000007", imdb: "tt9000007"),
			TrendingRig.Entry("The Lantern Keepers", IndexKind.Movie, tmdb: "500012", imdb: "tt4815162"),
			TrendingRig.Entry("Northwind Harbour", IndexKind.Series, tmdb: "600033", tvdb: "8001003"),
			TrendingRig.Entry("Paper Moon Saga", IndexKind.Series, imdb: "tt9001020"),
			TrendingRig.Entry("Ironwood Chronicles", IndexKind.Movie, tmdb: "9003005"));
		var other = TrendingRig.Index(TrendingRig.Entry("Something Else Entirely", IndexKind.Movie, tmdb: "77"));

		async Task<(IReadOnlyList<OutsideRequestRecord> Requests, IReadOnlyList<SmartCollection> Made)> Run(IndexSnapshot? index)
		{
			using var rig = new TrendingRig(trakt: true, tmdb: true, token: TrendingFake.Token);
			SomeLists(rig.Fake);
			var collections = new TrendingCollections(rig.Sources);
			IReadOnlyList<SmartCollection> made = index is null ? [] : collections.For(index);
			await collections.RefreshAsync();
			made = index is null ? [] : collections.For(index);
			if (index is not null)
			{
				_ = collections.HomeRowFor(index);
			}

			return (rig.Recorder.Requests, made);
		}

		var (requests, made) = await Run(library);

		Assert.NotEmpty(made); // the library's titles were matched, on this PC
		var secrets = library.Entries.SelectMany(e => new[] { e.Id.ToString(), e.Id.ToString("N"), e.Name, e.Tmdb, e.Imdb, e.Tvdb })
			.OfType<string>().Where(s => s.Length > 0).ToList();
		Assert.All(requests, r =>
		{
			var text = r.AllText;
			foreach (var secret in secrets)
			{
				Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"a request carries “{secret}” from the library: {r.Method} {r.Url}");
			}
		});

		var withOther = await Run(other);
		var withNone = await Run(null);
		string Shape(OutsideRequestRecord r) => r.Method + " " + r.Url + " " + string.Join("|", r.Headers.Select(h => h.Key + "=" + h.Value)) + " " + r.Body;
		Assert.Equal(requests.Select(Shape), withOther.Requests.Select(Shape));
		Assert.Equal(requests.Select(Shape), withNone.Requests.Select(Shape));
	}
}
