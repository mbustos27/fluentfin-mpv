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
/// A fact that runs only when asked for (<c>NIXIE_LIVE_INDEX=1</c>), against a Jellyfin on this PC (<c>NIXIE_LIVE_INDEX_SERVER</c>,
/// loopback only) whose generated test account is in <c>NIXIE_LIVE_INDEX_CREDENTIALS</c> (a credentials file, read here and
/// never printed): the throwaway at scale, or (1.8.1, BH-175-q4) one of our QA servers (10.11 at 127.0.0.1:8196, 12 at
/// :8296; docs\QA-SERVERS.md: its test user, read-only, never reset, seeded or stopped by this). Never anyone's own server.
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
/// Jellyfin at scale (the throwaway 25,736-film instance): the first-ever read (its first rows, the titles in; 1.8: the people
/// step behind them, and a top-up of a hundred titles' people by id),
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
		// 1.8.1 (BH-175-q4): which Jellyfin answered (MinDateLastSaved was checked on 12.4 only)
		summary["version"] = (string?)JsonNode.Parse(await http.GetStringAsync("/System/Info/Public"))?["Version"];
		var auth = await SignIn(http);
		var messages = new Subject<IOutboundWebSocketMessage>();
		var homeCache = new HomeCache(Path.Combine(folder, "HomeCache"), messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<Settings.ISettings>(), homeCache);
		try
		{
			await client.Initialize(server.TrimEnd('/'), auth);

			// the first-ever read: its first rows, and the titles in (the reading line gone); 1.8: the people step behind them
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
			// 1.8 (BH-180-m2): the most managed memory the first read and its people step took, above what was before it
			var before = GC.GetTotalMemory(forceFullCollection: true);
			var peak = before;
			using var sampled = new CancellationTokenSource();
			var sampler = Task.Run(async () =>
			{
				while (!sampled.IsCancellationRequested)
				{
					peak = Math.Max(peak, GC.GetTotalMemory(false));
					await Task.Delay(10);
				}
			});
			var titles = await first.GetAsync();
			var titlesIn = watch.ElapsedMilliseconds;
			await first.Settled;
			var whole = watch.ElapsedMilliseconds;
			await sampled.CancelAsync();
			await sampler;
			Assert.NotNull(titles);
			var peoplePages = firstLog.Entries.Count(e => e.Message.StartsWith("Library index: people page", StringComparison.Ordinal));
			summary["firstRead"] = new JsonObject
			{
				["titles"] = titles!.Entries.Count,
				["firstRowsMs"] = firstRows,
				["titlesInMs"] = titlesIn,
				["settledMs"] = whole,
				["peoplePages"] = peoplePages, // 1.8 (P1-2): every library's people, once, after the titles
				["peopleStepMs"] = whole - titlesIn,
				["peakMB"] = Math.Round((peak - before) / 1048576.0), // 1.8 (BH-180-m2)
				["peoplePerTitle"] = first.Current is { } read && read.Entries.Count > 0 ? Math.Round(read.Entries.Average(x => x.People.Length), 1) : 0,
				["titlesWithPeople"] = first.Current?.Entries.Count(x => x.People.Length > 0),
				["inMoreThanOneLibrary"] = titles.Entries.Count(x => x.LibraryIds.Length > 1), // 1.7.3 (P0-2)
				["log"] = Lines(firstLog),
			};
			Assert.True(peoplePages > 0);
			Assert.True(titles.PeopleDue); // the titles came before their people (rule 1)
			Assert.True(first.Current!.HasPeople);
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
			// 1.8: the published collections of the saved index, as a page builds them: the census line (counts, kinds, ms only)
			var censusLog = new CoreListLogger<LibraryIndex>();
			watch.Restart();
			var made = SmartCollectionCatalog.BuildAll(SmartCollectionCatalog.PublishedBuilders, saved!, SmartCollectionCatalog.Now(), null, null, Core.Outside.OutsideListsSnapshot.Empty, censusLog, census: true);
			var buildMs = watch.ElapsedMilliseconds;
			summary["restart"] = new JsonObject
			{
				["buildAllMs"] = buildMs,
				["census"] = censusLog.Entries.FirstOrDefault().Message,
				["anchoredSlots"] = made.Count(x => x.Kind is SmartCollectionKind.NextIn or SmartCollectionKind.MoreLike or SmartCollectionKind.MoreFrom),
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

			// 1.7.5 (fx1, BH-171-m5): a library change read as the titles each library saved since the last read (MinDateLastSaved;
			// nothing added here: what that read costs when the check finds a change), against every title above. The review of
			// 1.7.5: the server honours MinDateLastSaved (every title saved since 1970, none since tomorrow)
			var views = (await client.GetUserViewsOrNull())!.Where(v => JellyfinClient.IndexKinds(v) is not null).ToList();
			var biggest = views.MaxBy(v => saved.Entries.Count(x => x.InLibrary(v.Id!.Value)))!;
			var all = await client.GetLibraryIndexPageSavedSince(biggest, DateTimeOffset.UnixEpoch, 0, 1, true, CancellationToken.None);
			var none = await client.GetLibraryIndexPageSavedSince(biggest, DateTimeOffset.UtcNow.AddDays(1), 0, 1, true, CancellationToken.None);
			var lately = await client.GetLibraryIndexPageSavedSince(biggest, saved.ReadAt - IndexRead.SavedSinceMargin, 0, 1, true, CancellationToken.None);
			Assert.Equal(saved.Entries.Count(x => x.InLibrary(biggest.Id!.Value)), all?.TotalRecordCount);
			Assert.Equal(0, none?.TotalRecordCount);
			// 1.8.1 (BH-175-m3; the review): each indexed library's own count, which a change's read compares with the saved index
			var counts = new List<int?>();
			foreach (var view in views)
			{
				counts.Add(await client.GetLibraryTitleCount(view, CancellationToken.None));
			}

			Assert.Equal(views.Select(v => (int?)saved.Entries.Count(x => x.InLibrary(v.Id!.Value))), counts);
			var addedLog = new CoreListLogger<LibraryIndex>();
			var count = IndexRead.CountOf(saved.Check);
			watch.Restart();
			var (merged, why) = await new IndexRead(client, TimeProvider.System, addedLog).ChangedAsync(saved, saved.Check!, CancellationToken.None); // 1.9
			summary["addedSince"] = new JsonObject
			{
				["ms"] = watch.ElapsedMilliseconds,
				["libraries"] = views.Count,
				["savedSince1970"] = all?.TotalRecordCount,
				["savedSinceTomorrow"] = none?.TotalRecordCount,
				["savedSinceTheReadLessADay"] = lately?.TotalRecordCount,
				["libraryCounts"] = new JsonArray([.. counts.Select(x => (JsonNode?)x)]), // 1.8.1 (BH-175-m3)
				["checkCount"] = IndexRead.CountOf(saved.Check),
				["titles"] = merged?.Entries.Count,
				["why"] = why,
				["everyTitleMs"] = titlesIn,
				["log"] = Lines(addedLog),
			};
			Assert.NotNull(merged);
			Assert.Equal(saved.Entries.Count, merged!.Entries.Count);

			// 1.8 (P1-2): the people step's top-up, as after a library change: a hundred titles' people by id
			var peopleLog = new CoreListLogger<LibraryIndex>();
			var peopleRead = new IndexRead(client, TimeProvider.System, peopleLog);
			Assert.True(await peopleRead.LibrariesOfAsync(saved));
			watch.Restart();
			var topped = (await peopleRead.PeopleAsync(saved with { PeopleToAsk = [.. saved.Entries.Take(100).Select(x => x.Id)] }, null, CancellationToken.None)).Index;
			summary["peopleById"] = new JsonObject
			{
				["ms"] = watch.ElapsedMilliseconds,
				["titles"] = 100,
				["log"] = Lines(peopleLog),
			};
			Assert.NotNull(topped);
			Assert.Empty(topped!.PeopleToAsk);
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
		// 1.8: NIXIE_LIVE_INDEX_ACCOUNT picks another of the file's test accounts ("watcher": one that has watched, for the anchors)
		var account = Environment.GetEnvironmentVariable("NIXIE_LIVE_INDEX_ACCOUNT") is { Length: > 0 } named ? named : "user";
		var user = JsonNode.Parse(await File.ReadAllTextAsync(file))![account]!;
		using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName")
		{
			// 1.7.5: the names as Jellyfin's DTO has them (JsonContent camel-cased them), with a length (JsonContent's is chunked):
			// the fake server's big shape reads it too
			Content = new StringContent(new JsonObject { ["Username"] = (string?)user["username"], ["Pw"] = (string?)user["password"] }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
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
