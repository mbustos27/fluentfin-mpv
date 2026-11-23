using System.Diagnostics;
using System.Net.Http.Json;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// A fact that runs only when asked for (<c>NIXIE_LIVE_INDEX=1</c>), against a throwaway Jellyfin on this PC
/// (<c>NIXIE_LIVE_INDEX_SERVER</c>, loopback only) whose generated test account is in <c>NIXIE_LIVE_INDEX_CREDENTIALS</c>
/// (a credentials file, read here and never printed). Never a QA server for load, never anyone's own server.
/// </summary>
public sealed class LiveIndexFactAttribute : FactAttribute
{
	public LiveIndexFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("NIXIE_LIVE_INDEX") != "1")
		{
			Skip = "the opt-in timing of the library index on a real Jellyfin (NIXIE_LIVE_INDEX=1)";
		}
	}
}

/// <summary>
/// Nixie 1.7.1 (the index on a big server): the library index's real requests, through the real client, timed on a real
/// Jellyfin at scale (the throwaway 25,736-film instance): the first-ever read (its first rows, the titles in; 1.7.3: no people),
/// then a restart with the saved index (shown without a read when the server's check says nothing changed), and the
/// Collections page's first row with it. Writes durations and counts only (no title, no name, no token) to
/// NIXIE_LIVE_INDEX_OUT. Signs out at the end.
/// </summary>
[Trait("Category", "LiveIndex")]
[Collection(SessionInfoCollection.Name)]
public sealed class LiveIndexTests
{
	[LiveIndexFact]
	public async Task TheIndex_OnARealServerAtScale_IsTimed()
	{
		var server = Environment.GetEnvironmentVariable("NIXIE_LIVE_INDEX_SERVER") ?? "";
		Assert.True(Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.IsLoopback, "a server on this PC only");
		var folder = Path.Combine(Path.GetTempPath(), "nixie-live-index-" + Guid.NewGuid().ToString("N"));
		var summary = new JsonObject { ["server"] = "loopback" };
		using var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(5) };
		var auth = await SignIn(http);
		var messages = new Subject<IOutboundWebSocketMessage>();
		var homeCache = new HomeCache(Path.Combine(folder, "HomeCache"), messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<Settings.ISettings>(), homeCache);
		try
		{
			await client.Initialize(server.TrimEnd('/'), auth);

			// the first-ever read: its first rows, and the titles in (the reading line gone); 1.7.3: no people pass behind them
			var firstLog = new CoreListLogger<LibraryIndex>();
			var first = new LibraryIndex(client, homeCache, Path.Combine(folder, "LibraryIndex"), messages, firstLog, TimeProvider.System);
			var watch = Stopwatch.StartNew();
			long firstRows = -1;
			first.Changed += (_, snapshot) =>
			{
				if (firstRows < 0 && snapshot.Entries.Count > 0)
				{
					firstRows = watch.ElapsedMilliseconds;
				}
			};
			var titles = await first.GetAsync();
			var titlesIn = watch.ElapsedMilliseconds;
			await first.Settled;
			var whole = watch.ElapsedMilliseconds;
			Assert.NotNull(titles);
			var peoplePages = firstLog.Entries.Count(e => e.Message.StartsWith("Library index: people page", StringComparison.Ordinal));
			summary["firstRead"] = new JsonObject
			{
				["titles"] = titles!.Entries.Count,
				["firstRowsMs"] = firstRows,
				["titlesInMs"] = titlesIn,
				["settledMs"] = whole,
				["peoplePages"] = peoplePages, // 1.7.3: none
				["inMoreThanOneLibrary"] = titles.Entries.Count(x => x.LibraryIds.Length > 1), // 1.7.3 (P0-2)
				["log"] = Lines(firstLog),
			};
			Assert.Equal(0, peoplePages);
			first.Dispose();

			// a restart: the saved index at once, and no read while the server's check says nothing changed
			var againLog = new CoreListLogger<LibraryIndex>();
			var again = new LibraryIndex(client, homeCache, Path.Combine(folder, "LibraryIndex"), messages, againLog, TimeProvider.System);
			watch.Restart();
			var saved = await again.SavedAsync();
			var savedMs = watch.ElapsedMilliseconds;
			await Task.Delay(TimeSpan.FromSeconds(3)); // the check, in the background
			await again.Settled;
			Assert.NotNull(saved);
			Assert.DoesNotContain(againLog.Entries, e => e.Message.Contains(" page of library ", StringComparison.Ordinal));

			// the Collections page with it: its first row
			var store = new SmartCollectionStore(client, homeCache, Path.Combine(folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
			var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client, Mock.Of<INavigationViewServiceCore>(), Mock.Of<Settings.ISettings>());
			var pageLog = new CoreListLogger<CollectionsViewModel>();
			var page = new CollectionsViewModel(client, commands, again, store, logger: pageLog);
			watch.Restart();
			await page.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));
			var pageMs = watch.ElapsedMilliseconds;
			await page.OnNavigatedFrom();
			summary["restart"] = new JsonObject
			{
				["savedMs"] = savedMs,
				["titles"] = saved!.Entries.Count,
				["collectionsPageMs"] = pageMs,
				["rows"] = page.Rows.Count,
				["madeByNixie"] = page.MadeByNixieCount,
				["readingLine"] = page.IsReadingIndex,
				["log"] = Lines(againLog, pageLog),
			};
			Assert.False(page.IsReadingIndex);
			again.Dispose();
		}
		finally
		{
			await SignOut(http, auth.AccessToken!);
			homeCache.Dispose();
			if (Environment.GetEnvironmentVariable("NIXIE_LIVE_INDEX_OUT") is { Length: > 0 } output)
			{
				await File.WriteAllTextAsync(output, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			}

			try
			{
				Directory.Delete(folder, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	// durations and counts only: the index's own lines carry no title or name
	private static JsonArray Lines(params object[] logs) =>
		[.. logs.SelectMany(l => l switch
		{
			CoreListLogger<LibraryIndex> x => x.Entries.Select(e => e.Message),
			CoreListLogger<CollectionsViewModel> x => x.Entries.Select(e => e.Message),
			_ => [],
		}).Where(m => m.StartsWith("Library index", StringComparison.Ordinal) || m.StartsWith("Collections", StringComparison.Ordinal)).Select(m => (JsonNode)m)];

	private const string Client = "MediaBrowser Client=\"Nixie live index\", Device=\"stream-i\", DeviceId=\"nixie-171-live-index\", Version=\"1.7.1\"";

	private static async Task<AuthenticationResult> SignIn(HttpClient http)
	{
		var file = Environment.GetEnvironmentVariable("NIXIE_LIVE_INDEX_CREDENTIALS") ?? throw new InvalidOperationException("NIXIE_LIVE_INDEX_CREDENTIALS");
		var user = JsonNode.Parse(await File.ReadAllTextAsync(file))!["user"]!;
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName")
		{
			Content = JsonContent.Create(new { Username = (string?)user["username"], Pw = (string?)user["password"] }),
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
