// Perf harness for FluentFin. Runs the REAL FluentFin.Core code (JellyfinClient + view models) against a fake, local,
// credential-free Jellyfin-shaped HTTP server and counts/timestamps the requests each page makes. Builds against any Core
// (upstream FluentFin, this fork, a copy of an older commit): Core types are made by CoreFactory, whatever their
// constructors take. No real Jellyfin server is contacted.
//   (no argument)  every page once at 0 and 50 ms per request (the perf scout's table)
//   home ...       the home page from sign-in to its last row through a latency proxy (HomeBench.cs)
//   unreachable    how long a sign-in to a dead server blocks
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Kiota.Serialization.Json;
using Moq;

if (args.Length > 0 && args[0] == "home")
{
	await HomeBench.RunAsync(args[1..]);
	return;
}

ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

if (args.Length > 0 && args[0] == "unreachable")
{
	// how long a sign-in attempt blocks when the saved server does not answer (the main window waits for it)
	foreach (var url in new[] { "http://10.255.255.1:8096", "http://127.0.0.1:9" })
	{
		var settings = new Jellyfin.Sdk.JellyfinSdkSettings();
		settings.Initialize("bench", "1.0", "bench", "bench-device");
		settings.SetServerUrl(url);
		var api = new Jellyfin.Sdk.JellyfinApiClient(new Jellyfin.Sdk.JellyfinRequestAdapter(new Jellyfin.Sdk.JellyfinAuthenticationProvider(settings), settings));
		var sw = Stopwatch.StartNew();
		try { await api.System.Info.Public.GetAsync(); Console.WriteLine($"{url}: answered?"); }
		catch (Exception ex) { Console.WriteLine($"{url}: failed after {sw.Elapsed.TotalSeconds:N1} s with {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
	}
	return;
}

var fake = new FakeServer(libraries: 6, libraryItems: 5000, seasons: 10, episodesPerSeason: 12);
var baseUrl = await fake.StartAsync();
Console.WriteLine($"fake server at {baseUrl}");

var socket = new Subject<IOutboundWebSocketMessage>();
var deviceProfiles = new Mock<IDeviceProfileFactory>();
deviceProfiles.Setup(x => x.GetDeviceProfile()).Returns(new DeviceProfile());
// a data folder of the bench's own for a Core whose home page keeps its last state there (HomeCache): a new one each time,
// so every home measurement is a first start; never the user's folder (KnownFolders' default). A Core whose client is
// told of sign-ins (HomeCache) gets one too; other Cores ignore it.
var scratchData = Path.Combine(Path.GetTempPath(), $"bench-pages-{Environment.ProcessId}");
KnownFolders ScratchData() => new(Path.Combine(scratchData, Guid.NewGuid().ToString("N")));

var client = CoreFactory.Make<JellyfinClient>(socket, deviceProfiles.Object, ScratchData());
var commands = CoreFactory.Make<GlobalCommands>(client);

async Task<(double ms, List<string> reqs)> Measure(string label, int delayMs, Func<Task> action)
{
	fake.DelayMs = delayMs;
	fake.Reset();
	var sw = Stopwatch.StartNew();
	await action();
	sw.Stop();
	var reqs = fake.Snapshot();
	Console.WriteLine($"{label,-58} delay={delayMs,3}ms  requests={reqs.Count,3}  elapsed={sw.Elapsed.TotalMilliseconds,8:N0} ms  bytes={fake.BytesSent,10:N0}");
	return (sw.Elapsed.TotalMilliseconds, reqs);
}

void PrintRequests(List<string> reqs)
{
	foreach (var g in reqs.GroupBy(x => x)) Console.WriteLine($"      {g.Count(),3} x {g.Key}");
}

// --- sign-in initialisation (what runs before the main window is activated when one server+user is saved)
var auth = new AuthenticationResult
{
	AccessToken = "fake-token-not-a-credential",
	ServerId = "fake",
	User = new UserDto { Id = Guid.NewGuid(), Name = "bench", Policy = new UserPolicy { IsAdministrator = true } },
	SessionInfo = new SessionInfoDto { Id = "bench-session" },
};
foreach (var d in new[] { 0, 50 })
{
	var (_, r) = await Measure("JellyfinClient.Initialize (after auth)", d, () => client.Initialize(baseUrl, auth));
	if (d == 50) PrintRequests(r);
}

// `library [--home-first] [--gc]`: the library grid alone, 7 times at 0 ms, optionally after one home visit, optionally
// after a full GC each time (to tell an effect of what ran before it from a change in the library path itself)
if (args.Length > 0 && args[0] == "library")
{
	if (args.Contains("--home-first"))
	{
		await CoreFactory.Make<HomeViewModel>(client, Observable.Never<IOutboundWebSocketMessage>(), commands, ScratchData()).OnNavigatedTo(null!);
	}
	for (var i = 0; i < 7; i++)
	{
		if (args.Contains("--gc"))
		{
			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			GC.WaitForPendingFinalizers();
		}
		await Measure($"LibraryViewModel.OnNavigatedTo #{i + 1}", 0, () => CoreFactory.Make<LibraryViewModel>(client).OnNavigatedTo(fake.MovieLibrary));
	}
	await fake.StopAsync();
	if (Directory.Exists(scratchData)) Directory.Delete(scratchData, recursive: true);
	return;
}

// --- home page
foreach (var d in new[] { 0, 50 })
{
	var (_, r) = await Measure("HomeViewModel.OnNavigatedTo (6 libraries)", d, async () =>
	{
		var vm = CoreFactory.Make<HomeViewModel>(client, Observable.Never<IOutboundWebSocketMessage>(), commands, ScratchData());
		await vm.OnNavigatedTo(null!);
	});
	if (d == 50) PrintRequests(r);
}
// what the 6 'Latest' requests cost if issued together (same SDK, same server) - the concurrency headroom
foreach (var d in new[] { 50 })
{
	await Measure("  6 x Items/Latest sequential (as today)", d, async () => { foreach (var lib in fake.LibraryIds) await client.GetSimilarItems(new BaseItemDto { Id = lib }); });
	await Measure("  6 x Items/Latest-equivalent via Task.WhenAll", d, async () => await Task.WhenAll(fake.LibraryIds.Select(lib => client.GetSimilarItems(new BaseItemDto { Id = lib }))));
}

// --- library grid (5000 movies)
foreach (var d in new[] { 0, 50 })
{
	var (_, r) = await Measure("LibraryViewModel.OnNavigatedTo (5000 movies)", d, async () =>
	{
		var vm = CoreFactory.Make<LibraryViewModel>(client);
		await vm.OnNavigatedTo(fake.MovieLibrary);
		if (vm.Items.Count != 100) throw new Exception($"page size {vm.Items.Count}");
	});
	if (d == 50) PrintRequests(r);
}

// --- series / season / episode / movie pages
foreach (var d in new[] { 0, 50 })
{
	var (_, r1) = await Measure("SeriesViewModel.OnNavigatedTo", d, () => CoreFactory.Make<SeriesViewModel>(client).OnNavigatedTo(fake.Series));
	var (_, r2) = await Measure("SeasonViewModel.OnNavigatedTo", d, () => CoreFactory.Make<SeasonViewModel>(client).OnNavigatedTo(fake.Season1));
	var (_, r3) = await Measure("EpisodeViewModel.OnNavigatedTo", d, () => CoreFactory.Make<EpisodeViewModel>(client, commands).OnNavigatedTo(fake.Episode1));
	var (_, r4) = await Measure("MovieViewModel.OnNavigatedTo", d, () => CoreFactory.Make<MovieViewModel>(client, commands).OnNavigatedTo(fake.Movie1));
	if (d == 50) { Console.WriteLine("   series:"); PrintRequests(r1); Console.WriteLine("   movie:"); PrintRequests(r4); }
}

// --- play an episode: playlist build (VideoPlayerViewModel.Initialize -> PlaylistViewModel.FromEpisode)
foreach (var d in new[] { 0, 50 })
{
	var (_, r) = await Measure("PlaylistViewModel.FromEpisode (10 seasons)", d, async () =>
	{
		var p = await PlaylistViewModel.FromEpisode(client, fake.Episode1);
		if (p.Items.Count != 120) throw new Exception($"playlist {p.Items.Count}");
	});
	if (d == 50) PrintRequests(r);
}

// --- per-item start: GetMediaUrl (EndpointInfo + BitrateTest when not in network + PlaybackInfo), twice in a row like next-episode
fake.ThrottleMbps = 40;
foreach (var d in new[] { 0, 50 })
{
	var (_, r) = await Measure("GetMediaUrl, remote (bitrate test, 40 Mbit/s link)", d, () => client.GetMediaUrl(fake.Episode1Full));
	await Measure("GetMediaUrl again (next item)", d, () => client.GetMediaUrl(fake.Episode1Full));
	if (d == 50) PrintRequests(r);
}
fake.ThrottleMbps = 0;

// --- admin dashboard
foreach (var d in new[] { 0, 50 })
{
	var (_, r) = await Measure("DashboardViewModel.OnNavigatedTo", d, async () =>
	{
		var vm = CoreFactory.Make<DashboardViewModel>(client);
		await vm.OnNavigatedTo(null!);
		await vm.OnNavigatedFrom();
	});
	if (d == 50) PrintRequests(r);
}

// --- CPU-only micro measurements (no network)
Console.WriteLine();
Console.WriteLine("CPU micro-measurements (median of 7 runs after 2 warm-ups):");
double Median(Func<double> run) { run(); run(); var xs = Enumerable.Range(0, 7).Select(_ => run()).OrderBy(x => x).ToArray(); return xs[3]; }

foreach (var withOverview in new[] { true, false })
{
	var bytes = fake.LibraryJson(withOverview);
	var ms = Median(() =>
	{
		var sw = Stopwatch.StartNew();
		using var stream = new MemoryStream(bytes);
		var node = new JsonParseNodeFactory().GetRootParseNodeAsync("application/json", stream).GetAwaiter().GetResult();
		var result = node.GetObjectValue(BaseItemDtoQueryResult.CreateFromDiscriminatorValue);
		if (result?.Items?.Count != 5000) throw new Exception("parse");
		return sw.Elapsed.TotalMilliseconds;
	});
	Console.WriteLine($"  Kiota parse 5000-item library response, Overview={(withOverview ? "yes" : "no "),3}: {bytes.Length / 1024.0 / 1024.0,6:N2} MB, {ms,7:N1} ms (thread pool)");
}

{
	using var stream = new MemoryStream(fake.LibraryJson(true));
	var node = await new JsonParseNodeFactory().GetRootParseNodeAsync("application/json", stream);
	var dtos = node.GetObjectValue(BaseItemDtoQueryResult.CreateFromDiscriminatorValue)!.Items!;
	var ms = Median(() => { var sw = Stopwatch.StartNew(); var list = dtos.Select(BaseItemViewModel.FromDto).ToList(); return sw.Elapsed.TotalMilliseconds; });
	Console.WriteLine($"  BaseItemViewModel.FromDto x 5000 (UI thread today): {ms,7:N1} ms");

	var mock = new Mock<IJellyfinClient>();
	mock.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = dtos });
	var vmMs = Median(() =>
	{
		var sw = Stopwatch.StartNew();
		var vm = CoreFactory.Make<LibraryViewModel>(mock.Object);
		vm.OnNavigatedTo(new BaseItemDto()).GetAwaiter().GetResult();
		return sw.Elapsed.TotalMilliseconds;
	});
	Console.WriteLine($"  LibraryViewModel load of 5000 parsed items (FromDto+cache+sort+page, UI thread today): {vmMs,7:N1} ms");
	var vm2 = CoreFactory.Make<LibraryViewModel>(mock.Object);
	await vm2.OnNavigatedTo(new BaseItemDto());
	var sortMs = Median(() => { var sw = Stopwatch.StartNew(); vm2.UpdateSortBy(vm2.SortBy == ItemSortBy.Name ? ItemSortBy.CommunityRating : ItemSortBy.Name); return sw.Elapsed.TotalMilliseconds; });
	Console.WriteLine($"  LibraryViewModel re-sort of 5000 items (UI thread): {sortMs,7:N1} ms");
}

{
	const string hash = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";
	var pixels = new Blurhash.Pixel[20, 20];
	var us = Median(() =>
	{
		var sw = Stopwatch.StartNew();
		for (var i = 0; i < 1000; i++) Blurhash.Core.Decode(hash, pixels, 1);
		return sw.Elapsed.TotalMilliseconds; // ms per 1000 = us per decode
	});
	Console.WriteLine($"  Blurhash 20x20 decode: {us,7:N1} us per card (x:Bind converter on the UI thread)");
}

await fake.StopAsync();
if (Directory.Exists(scratchData)) Directory.Delete(scratchData, recursive: true);
return;

sealed class FakeServer(int libraries, int libraryItems, int seasons, int episodesPerSeason)
{
	private WebApplication? _app;
	private readonly ConcurrentQueue<string> _log = new();
	private long _bytes;
	private readonly Dictionary<Guid, object> _items = [];
	private readonly Dictionary<Guid, Func<byte[]>> _children = [];
	private byte[]? _libWith, _libWithout;
	public int DelayMs { get; set; }
	public int ThrottleMbps { get; set; }
	public long BytesSent => Interlocked.Read(ref _bytes);
	public List<Guid> LibraryIds { get; } = [];
	public BaseItemDto MovieLibrary { get; private set; } = null!;
	public BaseItemDto Series { get; private set; } = null!;
	public BaseItemDto Season1 { get; private set; } = null!;
	public BaseItemDto Episode1 { get; private set; } = null!;
	public BaseItemDto Episode1Full { get; private set; } = null!;
	public BaseItemDto Movie1 { get; private set; } = null!;

	private static readonly string[] Genres = ["Action", "Drama", "Comedy", "Thriller", "Horror", "Romance", "Sci-Fi", "Fantasy", "Animation", "Documentary", "Crime", "Mystery", "Family", "War", "Western", "History", "Music", "Adventure", "Biography", "Sport"];
	private static readonly string Overview = string.Concat(Enumerable.Repeat("A retired agent is pulled back for one last job that turns out to be personal. ", 5)).Trim();
	private const string Blur = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";

	public void Reset() { while (_log.TryDequeue(out _)) { } Interlocked.Exchange(ref _bytes, 0); }
	public List<string> Snapshot() => [.. _log];

	private static object Item(Guid id, string type, string name, bool overview, Guid? parent = null, Guid? series = null, int? index = null, int? parentIndex = null, int seed = 0, bool detail = false)
	{
		var r = new Random(seed);
		var tag = id.ToString("N")[..8];
		var d = new Dictionary<string, object?>
		{
			["Name"] = name, ["ServerId"] = "fake", ["Id"] = id.ToString("N"), ["Type"] = type, ["IsFolder"] = type is "Series" or "Season" or "CollectionFolder",
			["SortName"] = name.ToLowerInvariant(), ["PremiereDate"] = new DateTime(1950 + r.Next(75), 1 + r.Next(12), 1 + r.Next(28)).ToString("O") + "Z",
			["ProductionYear"] = 1950 + r.Next(75), ["CommunityRating"] = Math.Round(r.NextDouble() * 10, 1), ["OfficialRating"] = new[] { "PG", "PG-13", "R", "G", "TV-MA" }[r.Next(5)],
			["RunTimeTicks"] = (long)r.Next(20, 180) * 60 * 10_000_000, ["DateCreated"] = DateTime.UtcNow.AddDays(-r.Next(2000)).ToString("O"),
			["Genres"] = Genres.OrderBy(_ => r.Next()).Take(3).ToArray(), ["Tags"] = new[] { $"tag{r.Next(30)}", $"tag{r.Next(30)}" },
			["PrimaryImageAspectRatio"] = 0.6667, ["LocationType"] = "FileSystem", ["MediaType"] = type is "Movie" or "Episode" ? "Video" : "Unknown",
			["ImageTags"] = new Dictionary<string, string> { ["Primary"] = tag, ["Thumb"] = tag + "t" },
			["BackdropImageTags"] = new[] { tag + "b" },
			["ImageBlurHashes"] = new Dictionary<string, object> { ["Primary"] = new Dictionary<string, string> { [tag] = Blur }, ["Backdrop"] = new Dictionary<string, string> { [tag + "b"] = Blur }, ["Thumb"] = new Dictionary<string, string> { [tag + "t"] = Blur } },
			["UserData"] = new Dictionary<string, object?> { ["PlaybackPositionTicks"] = 0, ["PlayCount"] = r.Next(3), ["IsFavorite"] = r.Next(10) == 0, ["Played"] = r.Next(3) == 0, ["Key"] = tag, ["ItemId"] = id.ToString("N") },
		};
		if (overview) d["Overview"] = Overview;
		if (parent is { } p) d["ParentId"] = p.ToString("N");
		if (series is { } s) { d["SeriesId"] = s.ToString("N"); d["SeriesName"] = "Bench Show"; d["SeriesPrimaryImageTag"] = "sp"; }
		if (index is { } i) d["IndexNumber"] = i;
		if (parentIndex is { } pi) d["ParentIndexNumber"] = pi;
		if (type == "CollectionFolder") d["CollectionType"] = name.StartsWith("Shows") ? "tvshows" : "movies";
		if (detail && type is "Movie" or "Episode")
		{
			d["MediaStreams"] = new object[] {
				new Dictionary<string, object?> { ["Index"] = 0, ["Type"] = "Video", ["Codec"] = "h264", ["DisplayTitle"] = "1080p H264", ["AspectRatio"] = "16:9", ["IsDefault"] = true },
				new Dictionary<string, object?> { ["Index"] = 1, ["Type"] = "Audio", ["Codec"] = "eac3", ["DisplayTitle"] = "English EAC3 5.1", ["IsDefault"] = true },
				new Dictionary<string, object?> { ["Index"] = 2, ["Type"] = "Subtitle", ["Codec"] = "subrip", ["DisplayTitle"] = "English", ["IsExternal"] = false } };
			d["MediaSources"] = new object[] { new Dictionary<string, object?> { ["Id"] = id.ToString("N"), ["Container"] = "mkv", ["SupportsDirectPlay"] = true, ["SupportsTranscoding"] = true } };
			d["People"] = Enumerable.Range(0, 15).Select(k => new Dictionary<string, object?> { ["Name"] = $"Person {k}", ["Id"] = Guid.NewGuid().ToString("N"), ["Type"] = k < 12 ? "Actor" : "Director", ["PrimaryImageTag"] = "p" }).ToArray();
		}
		return d;
	}

	private static byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);
	private static byte[] Query(IEnumerable<object> items) { var list = items.ToList(); return Json(new Dictionary<string, object> { ["Items"] = list, ["TotalRecordCount"] = list.Count, ["StartIndex"] = 0 }); }
	public byte[] LibraryJson(bool withOverview) => withOverview ? _libWith! : _libWithout!;

	public async Task<string> StartAsync()
	{
		// libraries: first is a 5000-movie library, second is tv shows, the rest small movie libraries
		for (var l = 0; l < libraries; l++) LibraryIds.Add(Guid.NewGuid());
		var movieIds = Enumerable.Range(0, libraryItems).Select(_ => Guid.NewGuid()).ToList();
		_libWith = Query(movieIds.Select((id, i) => Item(id, "Movie", $"Movie {i:D5}", true, LibraryIds[0], seed: i)));
		_libWithout = Query(movieIds.Select((id, i) => Item(id, "Movie", $"Movie {i:D5}", false, LibraryIds[0], seed: i)));
		_children[LibraryIds[0]] = () => _libWith!;
		for (var i = 0; i < 50; i++) _items[movieIds[i]] = Item(movieIds[i], "Movie", $"Movie {i:D5}", true, LibraryIds[0], seed: i, detail: true);
		var seriesId = Guid.NewGuid();
		var seasonIds = Enumerable.Range(1, seasons).Select(_ => Guid.NewGuid()).ToList();
		_items[seriesId] = Item(seriesId, "Series", "Bench Show", true, LibraryIds[1], detail: true);
		_children[seriesId] = () => Query(seasonIds.Select((s, i) => Item(s, "Season", $"Season {i + 1}", true, seriesId, seriesId, i + 1)));
		Guid ep1 = default;
		for (var si = 0; si < seasons; si++)
		{
			var sid = seasonIds[si];
			_items[sid] = Item(sid, "Season", $"Season {si + 1}", true, seriesId, seriesId, si + 1, detail: true);
			var eps = Enumerable.Range(1, episodesPerSeason).Select(_ => Guid.NewGuid()).ToList();
			if (si == 0) ep1 = eps[0];
			foreach (var (e, ei) in eps.Select((e, ei) => (e, ei))) _items[e] = Item(e, "Episode", $"Episode {ei + 1}", true, sid, seriesId, ei + 1, si + 1, detail: true);
			var snapshot = eps.Select((e, ei) => Item(e, "Episode", $"Episode {ei + 1}", true, sid, seriesId, ei + 1, si + 1)).ToList();
			_children[sid] = () => Query(snapshot);
		}
		var libs = LibraryIds.Select((id, i) => Item(id, "CollectionFolder", i == 1 ? "Shows" : $"Movies {i}", false)).ToList();
		foreach (var (id, o) in LibraryIds.Zip(libs)) _items[id] = o;

		BaseItemDto Dto(Guid id) => new() { Id = id, Type = (BaseItemDto_Type)Enum.Parse(typeof(BaseItemDto_Type), (string)((Dictionary<string, object?>)_items[id])["Type"]!), SeriesId = ((Dictionary<string, object?>)_items[id]).TryGetValue("SeriesId", out var s) ? Guid.Parse((string)s!) : null };
		MovieLibrary = new BaseItemDto { Id = LibraryIds[0], Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		Series = Dto(seriesId); Season1 = Dto(seasonIds[0]); Episode1 = Dto(ep1); Movie1 = Dto(movieIds[0]);
		Episode1Full = new BaseItemDto { Id = ep1, Type = BaseItemDto_Type.Episode, MediaType = BaseItemDto_MediaType.Video, SeriesId = seriesId };

		var latest = Json(Enumerable.Range(0, 16).Select(i => Item(movieIds[i], "Movie", $"Movie {i:D5}", false, seed: i)).ToList());
		var twelve = Query(Enumerable.Range(0, 12).Select(i => Item(movieIds[i], "Movie", $"Movie {i:D5}", false, seed: i)));
		var nextUp = Query(Enumerable.Range(0, 24).Select(i => Item(Guid.NewGuid(), "Episode", $"Episode {i}", false, seasonIds[0], seriesId, i + 1, 1)));
		var views = Query(libs);
		var filters = Json(new { Genres, Tags = Enumerable.Range(0, 30).Select(i => $"tag{i}"), OfficialRatings = new[] { "PG", "PG-13", "R", "G", "TV-MA" }, Years = Enumerable.Range(1950, 75) });

		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		_app = builder.Build();
		// the session websocket is accepted and left open: answered 404 it reconnects now and then, landing in other rows
		_app.UseWebSockets();
		_app.Map("/socket", async (HttpContext c) =>
		{
			if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
			using var ws = await c.WebSockets.AcceptWebSocketAsync();
			var buffer = new byte[4096];
			try { while (ws.State == System.Net.WebSockets.WebSocketState.Open) await ws.ReceiveAsync(buffer, c.RequestAborted); } catch { }
		});
		_app.Use(async (ctx, next) =>
		{
			if (ctx.Request.Path == "/socket") { await next(); return; }
			var q = ctx.Request.Query;
			var shape = $"{ctx.Request.Method} {Normalize(ctx.Request.Path)}" + (q.ContainsKey("fields") ? $" fields={q["fields"]}" : "") + (q.ContainsKey("limit") ? $" limit={q["limit"]}" : "");
			_log.Enqueue(shape);
			if (DelayMs > 0) await Task.Delay(DelayMs);
			await next();
		});
		async Task Send(HttpContext ctx, byte[] body) { ctx.Response.ContentType = "application/json"; Interlocked.Add(ref _bytes, body.Length); await ctx.Response.Body.WriteAsync(body); }
		_app.MapPost("/Sessions/Capabilities/Full", () => Results.NoContent());
		_app.MapGet("/Plugins", (HttpContext c) => Send(c, Encoding.UTF8.GetBytes("[]")));
		_app.MapGet("/UserViews", (HttpContext c) => Send(c, views));
		_app.MapGet("/Items/Latest", (HttpContext c) => Send(c, latest));
		_app.MapGet("/UserItems/Resume", (HttpContext c) => Send(c, twelve));
		_app.MapGet("/Shows/NextUp", (HttpContext c) => Send(c, nextUp));
		_app.MapGet("/Items/Filters", (HttpContext c) => Send(c, filters));
		_app.MapGet("/Items/{id}/Similar", (HttpContext c) => Send(c, twelve));
		_app.MapGet("/Items/{id}", (HttpContext c, string id) => Guid.TryParse(id, out var g) && _items.TryGetValue(g, out var o) ? Send(c, Json(o)) : Task.FromResult(c.Response.StatusCode = 404));
		_app.MapGet("/Items", (HttpContext c) =>
		{
			var pid = c.Request.Query["parentId"].FirstOrDefault() ?? c.Request.Query["ParentId"].FirstOrDefault();
			return pid is not null && Guid.TryParse(pid, out var g) && _children.TryGetValue(g, out var f) ? Send(c, f()) : Send(c, Query([]));
		});
		_app.MapGet("/System/Endpoint", (HttpContext c) => Send(c, Json(new { IsLocal = false, IsInNetwork = false })));
		_app.MapGet("/Playback/BitrateTest", async (HttpContext c) =>
		{
			var size = int.Parse(c.Request.Query["size"].FirstOrDefault() ?? c.Request.Query["Size"].FirstOrDefault() ?? "1000000");
			c.Response.ContentType = "application/octet-stream";
			var chunk = new byte[64 * 1024];
			var sw = Stopwatch.StartNew();
			for (var sent = 0; sent < size; sent += chunk.Length)
			{
				var n = Math.Min(chunk.Length, size - sent);
				await c.Response.Body.WriteAsync(chunk.AsMemory(0, n));
				await c.Response.Body.FlushAsync();
				Interlocked.Add(ref _bytes, n);
				if (ThrottleMbps > 0)
				{
					var due = (sent + n) * 8.0 / (ThrottleMbps * 1_000_000.0) * 1000;
					var wait = due - sw.Elapsed.TotalMilliseconds;
					if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait));
				}
			}
		});
		_app.MapPost("/Items/{id}/PlaybackInfo", (HttpContext c, string id) => Send(c, Json(new { PlaySessionId = "ps", MediaSources = new[] { new { Id = Guid.Parse(id).ToString("N"), Container = "mkv", SupportsDirectPlay = true, SupportsTranscoding = true, ETag = "e" } } })));
		_app.MapGet("/MediaSegments/{id}", (HttpContext c) => Send(c, Query([])));
		_app.MapGet("/System/Info", (HttpContext c) => Send(c, Json(new { ServerName = "fake", Version = "10.11.0" })));
		_app.MapGet("/System/Info/Storage", (HttpContext c) => Send(c, Json(new { })));
		_app.MapGet("/Sessions", (HttpContext c) => Send(c, Encoding.UTF8.GetBytes("[]")));
		_app.MapGet("/System/ActivityLog/Entries", (HttpContext c) => Send(c, Query([])));
		_app.MapGet("/ScheduledTasks", (HttpContext c) => Send(c, Encoding.UTF8.GetBytes("[]")));
		await _app.StartAsync();
		return _app.Urls.First();
	}

	private static string Normalize(PathString p) => System.Text.RegularExpressions.Regex.Replace(p.Value ?? "", "[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}", "{id}");
	public Task StopAsync() => _app!.StopAsync();
}
