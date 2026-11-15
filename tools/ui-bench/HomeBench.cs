// `home` mode: what the home page costs from sign-in to the last row, through LatencyProxy (a remote HTTPS server: every
// request 1 RTT, every new connection 2 more) against HomeFakeServer (a realistic library). The view model runs on
// UiThread (one thread, like the app's dispatcher); after every dispatch the bench reads what the page would show:
//  - the upstream page swaps its shimmer for content only when IsLoading turns false, so every row appears at once;
//  - a progressive page (IsContinueLoading/IsNextUpLoading and a per-row IsLoading) shows each row when its data is in.
// With a Core that has HomeCache (the last home page, shown at once), each run is a new app start with a new HomeCache
// on a data folder of the bench's own (under --out, else the temp folder; never the user's): --snapshot none (default)
// empties it first (a first start ever), --snapshot disk keeps the last run's page (a start after a previous session).
// The revisit is Back to Home in the same "app" (HomeCache in memory).
// Usage: home [--out <dir>] [--reps 5] [--rtts 50,150] [--protocols h1,h2] [--server-scale 1] [--mbps 0]
//             [--snapshot none|disk] [--label x]
using System.Collections;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

static class HomeBench
{
	sealed record HomeResult(double FirstRowMs, double AllRowsMs, int Requests, long Bytes, int MaxInFlight, int NewConnections,
		double UiBusyMs, double UiLongestMs, int UiDispatches, List<string> Rows, List<(double Ms, int Rows, int Skeletons)> Timeline, List<ServedRequest> Served);

	sealed record Run(double InitMs, int InitRequests, HomeResult Home, HomeResult Revisit);

	public static async Task RunAsync(string[] args)
	{
		string Opt(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
		var outDir = Opt("--out", "");
		var reps = int.Parse(Opt("--reps", "5"));
		var rtts = Opt("--rtts", "50,150").Split(',').Select(double.Parse).ToArray();
		var protocols = Opt("--protocols", "h1,h2").Split(',');
		var scale = double.Parse(Opt("--server-scale", "1"), System.Globalization.CultureInfo.InvariantCulture);
		var mbps = double.Parse(Opt("--mbps", "0"), System.Globalization.CultureInfo.InvariantCulture);
		var label = Opt("--label", typeof(HomeViewModel).GetProperty("IsContinueLoading") is null ? "upstream home" : "progressive home");
		var keepSnapshot = Opt("--snapshot", "none") == "disk";
		var homeCacheType = typeof(HomeViewModel).Assembly.GetType("FluentFin.Core.Services.HomeCache");
		var dataRoot = Path.Combine(outDir.Length > 0 ? outDir : Path.GetTempPath(), $"bench-data-{Environment.ProcessId}");

		LatencyProxy.UseFineTimer();
		ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();
		await using var server = new HomeFakeServer { ServerScale = scale };
		var port = await server.StartAsync();
		using var ui = new UiThread();

		var socket = new Subject<IOutboundWebSocketMessage>();
		var profiles = new Mock<IDeviceProfileFactory>();
		profiles.Setup(x => x.GetDeviceProfile()).Returns(new DeviceProfile());
		var client = CoreFactory.Make<JellyfinClient>(socket, profiles.Object);
		var commands = CoreFactory.Make<GlobalCommands>(client);

		Console.WriteLine($"# {label}: {server.Views.Count} views, server-scale {scale}, downlink {(mbps > 0 ? $"{mbps} Mbit/s" : "unlimited")}, {reps} runs per row (+1 warm-up), medians" +
			(homeCacheType is null ? "" : keepSnapshot ? ", each start with the last run's saved home page" : ", each start without a saved home page"));
		Console.WriteLine("| RTT | protocol | sign-in init (req) | home: first row | home: all rows | requests | KB | max in flight | new TCP+TLS conns | VM UI-thread ms (longest) | sign-in to first row | sign-in to all rows | revisit: first / all (req) |");
		Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
		var all = new Dictionary<string, List<Run>>();
		foreach (var rtt in rtts)
		{
			foreach (var proto in protocols)
			{
				await using var proxy = new LatencyProxy(port, rtt, handshakeRtts: 2, http2Like: proto == "h2", downMbps: mbps);
				var runs = new List<Run>();
				for (var rep = 0; rep <= reps; rep++)
				{
					// a new app start: a new HomeCache, its folder emptied unless the last page is kept
					object? homeCache = null;
					if (homeCacheType is not null)
					{
						if (!keepSnapshot && Directory.Exists(dataRoot))
						{
							Directory.Delete(dataRoot, recursive: true);
						}
						homeCache = CoreFactory.Make(homeCacheType, new KnownFolders(dataRoot), socket);
					}
					var run = await OneRun(client, commands, server, proxy, ui, homeCache);
					(homeCache as IDisposable)?.Dispose();
					if (rep > 0)
					{
						runs.Add(run);
					}
				}
				all[$"{rtt}ms-{proto}"] = runs;
				double M(Func<Run, double> f) { var xs = runs.Select(f).OrderBy(x => x).ToArray(); return xs[xs.Length / 2]; }
				var h = (Func<Run, HomeResult>)(r => r.Home);
				Console.WriteLine($"| {rtt} ms | {proto} | {M(r => r.InitMs):N0} ms ({M(r => r.InitRequests):N0}) | {M(r => r.Home.FirstRowMs):N0} ms | {M(r => r.Home.AllRowsMs):N0} ms | {M(r => r.Home.Requests):N0} | {M(r => r.Home.Bytes) / 1024:N0} | {M(r => r.Home.MaxInFlight):N0} | {(proto == "h2" ? "-" : M(r => r.Home.NewConnections).ToString("N0"))} | {M(r => r.Home.UiBusyMs):N1} ({M(r => r.Home.UiLongestMs):N1}) | {M(r => r.InitMs + r.Home.FirstRowMs):N0} ms | {M(r => r.InitMs + r.Home.AllRowsMs):N0} ms | {M(r => r.Revisit.FirstRowMs):N0} / {M(r => r.Revisit.AllRowsMs):N0} ms ({M(r => r.Revisit.Requests):N0}) |");
			}
		}

		var sample = all.Last().Value[runs_median_index(all.Last().Value)];
		Console.WriteLine();
		Console.WriteLine($"rows shown (in order): {string.Join(", ", sample.Home.Rows)}");
		Console.WriteLine($"waterfall, home right after sign-in, {all.Last().Key} (server-side times from navigation; the client sees each answer RTT/2 later):");
		foreach (var r in sample.Home.Served.OrderBy(x => x.StartMs))
		{
			Console.WriteLine($"  {r.StartMs,7:N0} -> {r.EndMs,7:N0} ms  {r.Method} {r.Path}{(r.ParentId is null ? "" : $" parentId={server.Views.FirstOrDefault(v => v.Id.ToString("N") == r.ParentId.Replace("-", ""))?.Name}")}  conn {r.Connection}  {r.Bytes:N0} B  in flight {r.InFlightAtStart}");
		}
		Console.WriteLine("page timeline (ms from navigation: rows with content / placeholder rows):");
		Console.WriteLine("  " + string.Join("  ", sample.Home.Timeline.Select(t => $"{t.Ms:N0}:{t.Rows}/{t.Skeletons}")));

		if (Directory.Exists(dataRoot))
		{
			Directory.Delete(dataRoot, recursive: true);
		}

		if (outDir.Length > 0)
		{
			Directory.CreateDirectory(outDir);
			var json = all.ToDictionary(kv => kv.Key, kv => kv.Value.Select(r => new
			{
				r.InitMs, r.InitRequests,
				Home = Summary(r.Home), Revisit = Summary(r.Revisit),
			}));
			await File.WriteAllTextAsync(Path.Combine(outDir, $"home-{label.Replace(' ', '-')}.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
		}

		static object Summary(HomeResult h) => new
		{
			h.FirstRowMs, h.AllRowsMs, h.Requests, h.Bytes, h.MaxInFlight, h.NewConnections, h.UiBusyMs, h.UiLongestMs, h.UiDispatches, h.Rows,
			Timeline = h.Timeline.Select(t => new { t.Ms, t.Rows, t.Skeletons }),
			Served = h.Served.Select(s => new { s.StartMs, s.EndMs, s.Method, s.Path, s.ParentId, s.Connection, s.Bytes, s.InFlightAtStart }),
		};
		static int runs_median_index(List<Run> runs) => runs.Select((r, i) => (r, i)).OrderBy(x => x.r.Home.AllRowsMs).ElementAt(runs.Count / 2).i;
	}

	// one user for every run, so a start finds the page the last run saved (HomeCache is per server and user)
	private static readonly Guid BenchUser = Guid.Parse("b0b0b0b0-0000-0000-0000-000000000001");

	private static async Task<Run> OneRun(JellyfinClient client, GlobalCommands commands, HomeFakeServer server, LatencyProxy proxy, UiThread ui, object? homeCache)
	{
		var auth = new AuthenticationResult
		{
			AccessToken = "fake-token-not-a-credential",
			ServerId = "fake",
			User = new UserDto { Id = BenchUser, Name = "bench", Policy = new UserPolicy { IsAdministrator = true } },
			SessionInfo = new SessionInfoDto { Id = "bench-session" },
		};
		server.Reset();
		proxy.NewClient();
		var t0 = server.Now;
		await ui.Run(() => client.Initialize(proxy.Url, auth));
		var initMs = server.Now - t0;
		var initRequests = server.Requests.Count;

		var home = await MeasureHome(client, commands, server, proxy, ui, homeCache);
		var revisit = await MeasureHome(client, commands, server, proxy, ui, homeCache);
		return new Run(initMs, initRequests, home, revisit);
	}

	private static async Task<HomeResult> MeasureHome(JellyfinClient client, GlobalCommands commands, HomeFakeServer server, LatencyProxy proxy, UiThread ui, object? homeCache)
	{
		var vm = homeCache is null
			? CoreFactory.Make<HomeViewModel>(client, Observable.Never<IOutboundWebSocketMessage>(), commands)
			: CoreFactory.Make<HomeViewModel>(client, Observable.Never<IOutboundWebSocketMessage>(), commands, homeCache);
		var probe = new HomeProbe(vm);
		var timeline = new List<(double Ms, int Rows, int Skeletons)>();
		double start = 0;
		ui.ResetCounters();
		server.Reset();
		var conns = proxy.Connections;
		ui.AfterEach = () =>
		{
			var (rows, skeletons) = probe.Read();
			if (timeline.Count == 0 || timeline[^1].Rows != rows || timeline[^1].Skeletons != skeletons)
			{
				timeline.Add((server.Now - start, rows, skeletons));
			}
		};
		start = server.Now;
		await ui.Run(() => vm.OnNavigatedTo(null!));
		var allRows = server.Now - start;
		ui.AfterEach = null;
		List<string> order = [];
		ui.Send(_ => order = probe.Order(), null);
		var served = server.Requests.Select(r => r with { StartMs = r.StartMs - start, EndMs = r.EndMs - start }).ToList();
		var result = new HomeResult(
			timeline.FirstOrDefault(t => t.Rows > 0) is { Rows: > 0 } first ? first.Ms : double.NaN,
			allRows, served.Count, served.Sum(x => x.Bytes), server.MaxInFlight, proxy.Connections - conns,
			ui.BusyMs, ui.LongestMs, ui.Dispatches, order, timeline, served);
		await ui.Run(() => vm.OnNavigatedFrom());
		return result;
	}

	/// <summary>Reads what the home page shows from its view model, by reflection (works for every Core).</summary>
	private sealed class HomeProbe(object vm)
	{
		private readonly Type _type = vm.GetType();
		private bool Progressive => _type.GetProperty("IsContinueLoading") is not null;
		private static object? P(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);
		private static bool B(object o, string name) => (bool)P(o, name)!;
		private static int Count(object o, string name) => ((ICollection)P(o, name)!).Count;
		private List<object> Recent => [.. ((IEnumerable)P(vm, "RecentItems")!).Cast<object>()];

		public (int Rows, int Skeletons) Read()
		{
			if (!Progressive)
			{
				return B(vm, "IsLoading") ? (0, 3) : ((Count(vm, "ContinueItems") > 0 ? 1 : 0) + (Count(vm, "NextUpItems") > 0 ? 1 : 0) + Recent.Count, 0);
			}
			int rows = 0, skeletons = 0;
			foreach (var (loading, items) in new[] { ("IsContinueLoading", "ContinueItems"), ("IsNextUpLoading", "NextUpItems") })
			{
				if (B(vm, loading)) skeletons++;
				else if (Count(vm, items) > 0) rows++;
			}
			foreach (var row in Recent)
			{
				if (B(row, "IsLoading")) skeletons++;
				else if (Count(row, "Items") > 0) rows++;
			}
			return (rows, skeletons);
		}

		public List<string> Order()
		{
			List<string> rows = [];
			if (Count(vm, "ContinueItems") > 0) rows.Add($"Continue Watching ({Count(vm, "ContinueItems")})");
			if (Count(vm, "NextUpItems") > 0) rows.Add($"Next Up ({Count(vm, "NextUpItems")})");
			foreach (var row in Recent)
			{
				if (Count(row, "Items") > 0) rows.Add($"{((BaseItemDto)P(row, "Library")!).Name} ({Count(row, "Items")})");
			}
			return rows;
		}
	}
}
