using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Outside;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;
using static FluentFin.Core.Tests.Outside.OutsideKit;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 keyless: the permanent privacy test (plan §3, binding; review #4). The app's whole path runs with a fake library in
/// it: the server's items read into the library index, the Collections page opened (which asks the lists for a refresh and
/// builds Nixie's collections, matching the lists to the library), every enabled source against its strict fake, then a week
/// later on query.wikidata.org's successor query-next, a custom endpoint and QLever. Every outgoing request is recorded.
/// Two different libraries and an empty one must produce the very same requests (URLs, headers, bodies, count and order),
/// and none may carry an id, title or name from a library. Stream T adds its sources to <see cref="Enable"/> and its hosts to
/// <see cref="Fakes"/>.
/// 1.7 trending (merged at the 1.7.0 integration): Trakt and TMDb trending run on the same path, the page's trending
/// collections through <see cref="TrendingServices.Collections"/>; their requests run beside the lists' own, so each family
/// (the lists, the trending sources) is compared in its own order.
/// </summary>
[Collection(SharedOutsideLists.Name)]
public sealed class OutsidePrivacyTests
{
	static OutsidePrivacyTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	/// <summary>A library as the server sends it, and everything in it that must never leave the PC.</summary>
	private sealed record Library(string BaseUrl, Guid UserId, BaseItemDto View, IReadOnlyList<BaseItemDto> Items, IReadOnlyList<string> Secrets);

	private static Library MakeLibrary(string seed, IReadOnlyList<string> tmdbIds)
	{
		var view = new BaseItemDto { Id = Guid.NewGuid(), Name = $"Movies of {seed}", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var baseUrl = $"http://jellyfin-{seed.ToLowerInvariant()}.example:8096";
		var user = Guid.NewGuid();
		var secrets = new List<string> { baseUrl, $"jellyfin-{seed.ToLowerInvariant()}.example", user.ToString(), user.ToString("N"), view.Name!, view.Id!.Value.ToString("N") };
		var items = new List<BaseItemDto>();
		for (var i = 0; i < tmdbIds.Count; i++)
		{
			var id = Guid.NewGuid();
			var imdb = $"tt9{seed.Length}{i:D5}";
			var collection = i % 3 == 0 ? "1733" : $"77{i:D3}";
			items.Add(new BaseItemDto
			{
				Id = id,
				Name = $"Kestrel {seed} Requiem {i}",
				SortName = $"Requiem {seed} {i}",
				Type = BaseItemDto_Type.Movie,
				ProductionYear = 1950 + i,
				Tags = ["heist", $"own tag {seed} {i}"],
				Studios = [new NameGuidPair { Name = $"Studio {seed} {i}", Id = Guid.NewGuid() }],
				ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = tmdbIds[i], ["Imdb"] = imdb, ["TmdbCollection"] = collection } },
				UserData = new UserItemDataDto { Played = false },
			});
			secrets.AddRange([items[^1].Name!, items[^1].SortName!, id.ToString(), id.ToString("N"), imdb, tmdbIds[i], collection, $"own tag {seed} {i}", $"Studio {seed} {i}"]);
		}

		return new Library(baseUrl, user, view, items, secrets);
	}

	/// <summary>Turns every source on that this build has. 1.7 trending: stream T turns its own on here too.</summary>
	private static void Enable(MemoryStore store)
	{
		store.SaveSetting(SettingKeys.LookUpTmdbExports, true);
		store.SaveSetting(SettingKeys.LookUpWikidata, true);
		// 1.7 trending: stream T's sources
		store.SaveSetting(SettingKeys.LookUpTrakt, true);
		store.SaveSetting(SettingKeys.LookUpTmdbTrending, true);
		// end 1.7 trending
	}

	private const string CustomEndpoint = "https://sparql.example.org/wikidata/sparql";

	/// <summary>Sends each host to its strict fake. 1.7 trending: stream T adds its hosts.</summary>
	private sealed class Fakes(Func<DateTimeOffset> clock) : HttpMessageHandler
	{
		private readonly StrictTmdbExports _tmdb = new(clock);
		private readonly StrictWikidata _wikidata = new(clock);

		// 1.7 trending: Trakt and TMDb trending's strict fake (TrendingKit), its lists the same whatever the library holds:
		// Best Picture winners (some of them each library owns) among titles no library has
		private readonly TrendingFake _trending = TrendingLists();

		private static TrendingFake TrendingLists()
		{
			var fake = new TrendingFake();
			var winners = OwnedTmdbIds(0, 25).Select(id => ((string?)id, (string?)null, (string?)null, "a winner")).ToList();
			fake.Trakt["movies/trending"] = [.. winners, .. TrendingFake.Unowned("Film", 300)];
			fake.Trakt["shows/trending"] = TrendingFake.Unowned("Show", 40, 1000);
			fake.Trakt["movies/popular"] = [.. TrendingFake.Unowned("Popular", 10, 2000), .. winners];
			fake.Trakt["shows/popular"] = TrendingFake.Unowned("Popular show", 10, 3000);
			fake.Trakt["movies/anticipated"] = TrendingFake.Unowned("Coming", 10, 4000);
			fake.Trakt["shows/anticipated"] = TrendingFake.Unowned("Coming show", 10, 5000);
			fake.Trakt["movies/boxoffice"] = [.. winners.Take(3), .. TrendingFake.Unowned("Box", 7, 6000)];
			fake.Tmdb["movie"] = [.. OwnedTmdbIds(0, 25).Select(id => (int.Parse(id, System.Globalization.CultureInfo.InvariantCulture), "a winner")), .. Enumerable.Range(1, 80).Select(i => (700_000 + i, "elsewhere"))];
			fake.Tmdb["tv"] = [.. Enumerable.Range(1, 100).Select(i => (800_000 + i, "elsewhere"))];
			return fake;
		}
		// end 1.7 trending

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			HttpMessageHandler fake = request.RequestUri!.Host switch
			{
				"files.tmdb.org" => _tmdb,
				"query.wikidata.org" or "query-next.wikidata.org" or "qlever.cs.uni-freiburg.de" or "sparql.example.org" => _wikidata,
				"api.trakt.tv" or "api.themoviedb.org" => _trending, // 1.7 trending
				_ => throw new InvalidOperationException("no fake for " + request.RequestUri.Host),
			};
			return new HttpMessageInvoker(fake, disposeHandler: false).SendAsync(request, cancellationToken);
		}
	}

	/// <summary>The app's path for one library, every request recorded.</summary>
	private static async Task<(IReadOnlyList<OutsideRequestRecord> Requests, bool MadeAwards, bool MadeTrending)> RunAll(Library library)
	{
		var folder = TempFolder();
		var clock = new TestClock(Noon);
		var recorder = new OutsideRequestRecorder(new Fakes(clock.Func));
		var store = new MemoryStore();
		Enable(store);
		var hub = new OutsideHub(new HttpClient(recorder), OutsideRouting.Real, "1.7.0", new OutsideCache(Path.Combine(folder, "Outside"), clock.Func), store, clock.Func);
		var lists = new OutsideLists(hub, store, clock.Func, publish: true);
		OutsideLists.App = lists;

		var messages = new Subject<IOutboundWebSocketMessage>();
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns(library.BaseUrl);
		client.SetupGet(x => x.UserId).Returns(library.UserId);
		client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([library.View]);
		client.Setup(x => x.GetUserViews()).ReturnsAsync([library.View]);
		client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. library.Items.Skip(start).Take(limit)], TotalRecordCount = library.Items.Count });
		client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		using var homeCache = new HomeCache(Path.Combine(folder, "HomeCache"), messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		var index = new LibraryIndex(client.Object, homeCache, Path.Combine(folder, "LibraryIndex"), messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		var store2 = new SmartCollectionStore(client.Object, homeCache, Path.Combine(folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		// 1.7 trending: Trakt (this build's Client ID) and TMDb trending (the user's token), as the app wires them for the pages
		var token = new TmdbToken(store);
		Assert.Null(token.Save(TrendingFake.Token));
		var trending = new TrendingCollections(new TrendingSources(hub, token, TrendingFake.ClientId, clock.Func), index, homeCache);
		TrendingServices.Collections = trending;
		// end 1.7 trending
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		var page = new CollectionsViewModel(client.Object, commands, index, store2);
		try
		{
			// 1. the Collections page opens: the library is read and matched while the lists are refreshed (from nothing)
			await page.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
			await page.Reading;
			await lists.Background;
			await lists.RefreshAsync();
			await trending.RefreshAsync(); // 1.7 trending: the page's own refresh, or nothing when it's in
			var madeAwards = await Eventually(() => page.Rows.Any(r => r.Kind == SmartCollectionKind.Award));
			var madeTrending = await Eventually(() => page.Rows.Any(r => r.Kind == SmartCollectionKind.Trending)); // 1.7 trending
			await page.OnNavigatedFrom();

			// 2. a week later, on query-next (it refuses until it opens: a run of refusals)
			clock.Now = Noon.AddDays(8);
			store.SaveSetting(SettingKeys.WikidataEndpoint, "Next");
			await page.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
			await lists.Background;
			await lists.RefreshAsync();
			await trending.RefreshAsync(); // 1.7 trending
			await page.OnNavigatedFrom();

			// 3. two hours on (the failures' pause and hour are over), a custom endpoint, then QLever
			clock.Now = clock.Now.AddHours(2);
			store.SaveSetting(SettingKeys.WikidataEndpoint, "Custom");
			store.SaveSetting(SettingKeys.WikidataCustomEndpoint, CustomEndpoint);
			await lists.RefreshAsync();
			store.SaveSetting(SettingKeys.WikidataEndpoint, "QLever");
			hub.Cache.Clear(OutsideSourceId.Wikidata);
			await page.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
			await lists.Background;
			await lists.RefreshAsync();
			await trending.RefreshAsync(); // 1.7 trending
			await page.OnNavigatedFrom();
			return (recorder.Requests, madeAwards, madeTrending);
		}
		finally
		{
			OutsideLists.App = null;
			OutsideListsSnapshot.Shared = OutsideListsSnapshot.Empty;
			TrendingServices.Collections = null; // 1.7 trending
			index.Dispose();
		}
	}

	private static async Task<bool> Eventually(Func<bool> condition)
	{
		for (var i = 0; i < 100 && !condition(); i++)
		{
			await Task.Delay(50);
		}

		return condition();
	}

	private static bool Carries(OutsideRequestRecord request, string secret)
	{
		var pattern = $"(?<![0-9A-Za-z]){Regex.Escape(secret)}(?![0-9A-Za-z])";
		return Regex.IsMatch(request.AllText, pattern, RegexOptions.IgnoreCase) || Regex.IsMatch(Uri.UnescapeDataString(request.AllText), pattern, RegexOptions.IgnoreCase);
	}

	private static IReadOnlyList<string> OwnedTmdbIds(int skip, int count) =>
		[.. WikidataLists.Parse(OutsideFixtures.Wikidata("award-best-picture"), OutsideListKind.Award)!.Select(x => x.Ref.Tmdb!).Skip(skip).Take(count)];

	[Fact]
	public async Task Two_libraries_and_an_empty_one_send_the_same_requests_and_none_carries_anything_from_a_library()
	{
		var a = MakeLibrary("A", OwnedTmdbIds(0, 10));
		var b = MakeLibrary("Bee", OwnedTmdbIds(20, 25));
		var none = MakeLibrary("None", []);

		var (ra, awardsA, trendingA) = await RunAll(a);
		var (rb, awardsB, trendingB) = await RunAll(b);
		var (rn, awardsNone, trendingNone) = await RunAll(none);

		// the path really ran: the libraries' winners were matched to Wikidata's lists
		Assert.True(awardsA);
		Assert.True(awardsB);
		Assert.False(awardsNone);
		// 1.7 trending: and to the trending lists
		Assert.True(trendingA);
		Assert.True(trendingB);
		Assert.False(trendingNone);
		// every source and every endpoint was asked
		var hosts = ra.Select(r => r.Url.Host).Distinct().Order(StringComparer.Ordinal).ToList();
		Assert.Equal(["api.themoviedb.org", "api.trakt.tv", "files.tmdb.org", "qlever.cs.uni-freiburg.de", "query-next.wikidata.org", "query.wikidata.org", "sparql.example.org"], hosts);
		// the very same requests, whatever the library: URLs, headers, bodies, count and order (1.7 trending: the trending
		// sources refresh beside the lists, so each family keeps its own order)
		static bool Trending(OutsideRequestRecord r) => r.Url.Host is "api.trakt.tv" or "api.themoviedb.org";
		foreach (var family in new Func<OutsideRequestRecord, bool>[] { Trending, r => !Trending(r) })
		{
			Assert.Equal(ra.Where(family).Select(r => r.AllText), rb.Where(family).Select(r => r.AllText));
			Assert.Equal(ra.Where(family).Select(r => r.AllText), rn.Where(family).Select(r => r.AllText));
		}

		Assert.Equal(ra.Count, rb.Count);
		Assert.Equal(ra.Count, rn.Count);
		Assert.All(ra, r =>
		{
			Assert.Equal("GET", r.Method);
			Assert.Equal("", r.Body);
			foreach (var secret in a.Secrets.Concat(b.Secrets).Concat(none.Secrets))
			{
				Assert.False(Carries(r, secret), $"{r.Url} carries a library's \"{secret}\"");
			}
		});
	}

	[Fact]
	public void The_check_would_catch_a_request_built_from_the_library()
	{
		// a contrast for the test itself: a SPARQL VALUES of owned ids (a ruled-out shape) and a title in a header are caught
		var library = MakeLibrary("A", OwnedTmdbIds(0, 3));
		var owned = (string)library.Items[1].ProviderIds!.AdditionalData!["Tmdb"];
		var bad = new OutsideRequestRecord("GET", new Uri("https://query.wikidata.org/sparql?query=" + Uri.EscapeDataString($"SELECT ?i WHERE {{ VALUES ?t {{ \"{owned}\" }} ?i wdt:P4947 ?t }}")), [], "");
		var title = new OutsideRequestRecord("GET", new Uri("https://example.org/"), [new("X-Title", library.Items[0].Name!)], "");
		var server = new OutsideRequestRecord("GET", new Uri("https://example.org/?from=" + Uri.EscapeDataString(library.BaseUrl)), [], "");

		Assert.Contains(library.Secrets, s => Carries(bad, s));
		Assert.Contains(library.Secrets, s => Carries(title, s));
		Assert.Contains(library.Secrets, s => Carries(server, s));
	}
}
