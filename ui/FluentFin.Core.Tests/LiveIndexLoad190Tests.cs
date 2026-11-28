using System.Diagnostics;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>A fact that runs only when asked for (<c>NIXIE_LIVE_LOAD=1</c>), against the fake server on this PC.</summary>
public sealed class LiveLoadFactAttribute : FactAttribute
{
	public LiveLoadFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("NIXIE_LIVE_LOAD") != "1")
		{
			Skip = "the opt-in count of the library index's requests on a big fake server (NIXIE_LIVE_LOAD=1)";
		}
	}
}

/// <summary>
/// Nixie 1.9 (the index's load on a server that isn't the user's, about 190,000 titles): the requests and bytes the library index
/// asks of the fake server (<c>NIXIE_LIVE_LOAD_SERVER</c>, loopback only, started with <c>FAKESERVER_BIG_LIBRARY=190000</c>; its
/// request log <c>NIXIE_LIVE_LOAD_REQUESTS</c>) for the first read, then for a start two hours later with a small change: a film
/// added and one removed (the count moves by 0, which 1.8.1 read as every title). Signed in with Quick Connect through the
/// fake's harness endpoint (no password). Writes counts and bytes only to <c>NIXIE_LIVE_LOAD_OUT</c>. Uses only what 1.9's base
/// had too, so the same file measures the code before this change. Never anyone's own server.
/// </summary>
[Trait("Category", "LiveIndex")]
[Collection(SessionInfoCollection.Name)]
public sealed class LiveIndexLoad190Tests
{
	private const string Client = "MediaBrowser Client=\"Nixie live load\", Device=\"index190\", DeviceId=\"nixie-190-live-load\", Version=\"1.9.0\"";

	[LiveLoadFact]
	public async Task AStartWithASmallChange_OnABigServer_RequestsAndBytes()
	{
		var server = Environment.GetEnvironmentVariable("NIXIE_LIVE_LOAD_SERVER") ?? "";
		Assert.True(Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.IsLoopback, "a server on this PC only");
		var requests = Environment.GetEnvironmentVariable("NIXIE_LIVE_LOAD_REQUESTS") ?? throw new InvalidOperationException("NIXIE_LIVE_LOAD_REQUESTS");
		var folder = Path.Combine(Path.GetTempPath(), "nixie-live-load-" + Guid.NewGuid().ToString("N"));
		using var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(5) };
		var auth = await QuickConnect(http);
		var messages = new Subject<IOutboundWebSocketMessage>();
		var homeCache = new HomeCache(Path.Combine(folder, "HomeCache"), messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<Settings.ISettings>(), homeCache);
		var summary = new JsonObject { ["label"] = Environment.GetEnvironmentVariable("NIXIE_LIVE_LOAD_LABEL") ?? "" };
		LibraryIndex Make(CoreListLogger<LibraryIndex> log) =>
			new(client, homeCache, Path.Combine(folder, "LibraryIndex"), messages, log, TimeProvider.System) { PeopleWanted = () => false };
		try
		{
			await client.Initialize(server.TrimEnd('/'), auth);

			// the first read: every title
			var since = Now();
			var watch = Stopwatch.StartNew();
			var firstLog = new CoreListLogger<LibraryIndex>();
			IndexSnapshot read;
			using (var first = Make(firstLog))
			{
				Assert.NotNull(await first.GetAsync());
				await first.Settled;
				read = first.Current!;
			}

			await Task.Delay(TimeSpan.FromSeconds(2)); // the save
			summary["titles"] = read.Entries.Count;
			summary["firstRead"] = Counted(requests, since, watch.Elapsed);

			// two hours on: the saved index and its note dated back (a change is read at most once an hour after a read), and a
			// small change on the server: a film added, one removed (the count moves by 0)
			DateBack(Path.Combine(folder, "LibraryIndex"), TimeSpan.FromHours(2));
			var archive = read.Libraries.OrderByDescending(l => read.Entries.Count(x => x.InLibrary(l.Id))).First();
			var gone = read.Entries.Where(x => x.InLibrary(archive.Id)).GroupBy(x => x.Name).First(g => g.Count() == 1).Key;
			using (var change = new HttpRequestMessage(HttpMethod.Post, "/__harness/titles")
			{
				Content = new StringContent(new JsonObject { ["add"] = new JsonArray(new JsonObject { ["name"] = "Load Probe Film", ["library"] = archive.Name }), ["remove"] = new JsonArray(gone) }.ToJsonString(),
					System.Text.Encoding.UTF8, "application/json"),
			})
			{
				using var answer = await http.SendAsync(change);
				Assert.True(answer.IsSuccessStatusCode);
			}

			// the second start
			since = Now();
			watch.Restart();
			var secondLog = new CoreListLogger<LibraryIndex>();
			using (var second = Make(secondLog))
			{
				Assert.NotNull(await second.GetAsync());
				await Task.Delay(500);
				await second.Checking;
				await second.Settled;
				var now = second.Current!;
				var counted = Counted(requests, since, watch.Elapsed);
				counted["titles"] = now.Entries.Count;
				counted["probeIn"] = now.Entries.Any(x => x.Name == "Load Probe Film");
				counted["removedStays"] = now.Entries.Any(x => x.Name == gone);
				counted["log"] = new JsonArray([.. secondLog.Entries.Select(e => e.Message).Where(m => !m.Contains(" page of library ", StringComparison.Ordinal)).Select(m => (JsonNode?)m)]);
				summary["secondStart"] = counted;

				// 1.9 (the coordinator's review): a film played on another device, then R: whether it shows played, and what R asked
				var film = now.Entries.First(x => x.InLibrary(archive.Id) && !x.Played && x.Name != "Load Probe Film" && x.Name != gone); // not the one removed (it stays until the day's read)
				using (var play = new HttpRequestMessage(HttpMethod.Post, "/__harness/played")
				{
					Content = new StringContent(new JsonObject { ["id"] = film.Id.ToString("N"), ["played"] = true }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
				})
				{
					using var answer = await http.SendAsync(play);
					Assert.True(answer.IsSuccessStatusCode);
				}

				var logged = secondLog.Entries.Count;
				since = Now();
				watch.Restart();
				Assert.NotNull(await second.RefreshAsync());
				await second.Settled;
				var r = Counted(requests, since, watch.Elapsed);
				r["playedShows"] = second.Current!.Entries.Single(x => x.Id == film.Id).Played;
				r["log"] = new JsonArray([.. secondLog.Entries.Skip(logged).Select(e => e.Message).Where(m => !m.Contains(" page of library ", StringComparison.Ordinal)).Select(m => (JsonNode?)m)]);
				summary["rAfterAPlay"] = r;
			}

			// for the removals' choice: one page of 1,000 of the biggest library's ids alone (no fields, images or user data)
			// against the index's own page of 1,000
			var lean = await Bytes(http, auth.AccessToken!, $"/Items?ParentId={archive.Id:N}&IncludeItemTypes=Movie&Recursive=true&StartIndex=0&Limit=1000&EnableImages=false&EnableUserData=false&EnableTotalRecordCount=false");
			var index = await Bytes(http, auth.AccessToken!, $"/Items?ParentId={archive.Id:N}&IncludeItemTypes=Movie&Recursive=true&StartIndex=0&Limit=1000&Fields=ProviderIds,Tags,Genres,Studios,ProductionLocations,SortName,DateCreated&ImageTypeLimit=1&EnableImageTypes=Primary&EnableTotalRecordCount=false");
			summary["pageOf1000"] = new JsonObject { ["idsOnlyBytes"] = lean.Bytes, ["idsOnlyMs"] = lean.Ms, ["indexBytes"] = index.Bytes, ["indexMs"] = index.Ms };
		}
		catch (Exception ex)
		{
			summary["error"] = $"{ex.GetType().Name}: {ex.Message}";
			throw;
		}
		finally
		{
			await SignOut(http, auth.AccessToken!);
			homeCache.Dispose();
			if (Environment.GetEnvironmentVariable("NIXIE_LIVE_LOAD_OUT") is { Length: > 0 } output)
			{
				await File.WriteAllTextAsync(output, summary.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
			}

			TestFolder.Delete(folder);
		}
	}

	private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

	// the fake's request log since a time: how many requests and bytes, and how many of each path
	private static JsonObject Counted(string log, double since, TimeSpan took)
	{
		// the server keeps its log open for writing: read it beside it
		using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);
		var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var lines = all.Select(l => JsonNode.Parse(l)!).Where(l => (double)l["t"]! >= since && !((string)l["path"]!).StartsWith("/__harness/", StringComparison.Ordinal)).ToList();
		var paths = new JsonObject();
		foreach (var group in lines.GroupBy(l => Regex.Replace((string)l["path"]!, "[0-9a-fA-F]{32}|[0-9a-fA-F-]{36}", "{id}")).OrderByDescending(g => g.Count()))
		{
			paths[group.Key] = group.Count();
		}

		var savedSince = lines.Count(l => l["query"]?["mindatelastsaved"] is not null);
		return new JsonObject
		{
			["requests"] = lines.Count,
			["bytes"] = lines.Sum(l => (long)l["bytes"]!),
			["seconds"] = Math.Round(took.TotalSeconds, 1),
			["savedSinceRequests"] = savedSince,
			["paths"] = paths,
		};
	}

	private static void DateBack(string folder, TimeSpan by)
	{
		var when = DateTimeOffset.UtcNow.Subtract(by).ToString("O");
		foreach (var file in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories))
		{
			var text = File.ReadAllText(file);
			File.WriteAllText(file, Regex.Replace(text, "\"(ReadAt|LastRead|TitlesAt)\":\"[^\"]+\"", m => $"\"{m.Groups[1].Value}\":\"{when}\""));
		}
	}

	private static async Task<(long Bytes, long Ms)> Bytes(HttpClient http, string token, string path)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.TryAddWithoutValidation("Authorization", $"{Client}, Token=\"{token}\"");
		var watch = Stopwatch.StartNew();
		using var answer = await http.SendAsync(request);
		var body = await answer.Content.ReadAsByteArrayAsync();
		Assert.True(answer.IsSuccessStatusCode);
		return (body.LongLength, watch.ElapsedMilliseconds);
	}

	// Quick Connect, approved through the fake's harness endpoint as the test user would on another device: no password
	private static async Task<AuthenticationResult> QuickConnect(HttpClient http)
	{
		using var initiate = new HttpRequestMessage(HttpMethod.Post, "/QuickConnect/Initiate");
		initiate.Headers.TryAddWithoutValidation("Authorization", Client);
		using var started = await http.SendAsync(initiate);
		var code = JsonNode.Parse(await started.Content.ReadAsStringAsync())!;
		using (var approve = new HttpRequestMessage(HttpMethod.Post, "/__harness/quickconnect/approve")
		{
			Content = new StringContent(new JsonObject { ["code"] = (string?)code["Code"] }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
		})
		{
			using var approved = await http.SendAsync(approve);
			Assert.True(approved.IsSuccessStatusCode, "the code approved");
		}

		using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateWithQuickConnect")
		{
			Content = new StringContent(new JsonObject { ["Secret"] = (string?)code["Secret"] }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
		};
		request.Headers.TryAddWithoutValidation("Authorization", Client);
		using var answer = await http.SendAsync(request);
		Assert.True(answer.IsSuccessStatusCode, $"sign-in: HTTP {(int)answer.StatusCode}");
		var json = JsonNode.Parse(await answer.Content.ReadAsStringAsync())!;
		return new AuthenticationResult
		{
			AccessToken = (string?)json["AccessToken"],
			ServerId = (string?)json["ServerId"],
			User = new UserDto { Id = Guid.Parse((string)json["User"]!["Id"]!), Name = "live" },
		};
	}

	private static async Task SignOut(HttpClient http, string token)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Sessions/Logout");
		request.Headers.TryAddWithoutValidation("Authorization", $"{Client}, Token=\"{token}\"");
		using var _ = await http.SendAsync(request);
	}
}
