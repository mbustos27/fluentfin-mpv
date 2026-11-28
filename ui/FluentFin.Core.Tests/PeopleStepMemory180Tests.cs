using System.Diagnostics;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 bug hunt (BH-180-m2): the people step held every page whole (each title's whole cast and crew, with image tags
/// and blurhashes) until it ended, and each save built a tree of the whole index: 760 MB at the peak of a first start on the
/// fake 25,000-title server, 883 MB with R. Here at 25,000 titles with casts of real shape (40 people a title, each with a
/// role, an image tag and a blurhash: the QA Jellyfin 12.4's real casts are 17 a title, about 284 bytes a person in the lean
/// answer, image tags included): what a page leaves behind, and the step's peak and time, with and without R.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class PeopleStepMemory180Tests(ITestOutputHelper output) : IDisposable
{
	private const int Films = 25_000;
	private const int CastSize = 40;
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-people180-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly List<WeakReference<BaseItemDto>> _given = [];
	private int _peoplePages;

	static PeopleStepMemory180Tests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public void Dispose() => TestFolder.Delete(_folder);

	private static BaseItemDto Film(int i) => new()
	{
		Id = Guid.Parse($"{i:D8}-0181-0000-0000-000000000000"),
		Name = $"A Film Title of Real Length {i}",
		SortName = $"film title of real length {i}",
		Type = BaseItemDto_Type.Movie,
		ProductionYear = 1950 + (i % 75),
		CommunityRating = 5 + (i % 50 / 10f),
		Genres = ["Drama", i % 2 == 0 ? "Thriller" : "Comedy"],
		Studios = [new NameGuidPair { Name = $"Studio Name {i % 300}" }],
		ProductionLocations = ["United States of America"],
		DateCreated = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
		ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = $"{100000 + i}" } },
		ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = $"{i:x8}0123456789abcdef01234567" } },
		UserData = new UserItemDataDto { Played = false },
	};

	// a title's whole cast and crew as the server sends it (the field has no limit): a director, actors, then crew
	private static BaseItemDto Cast(Guid id, int i) => new()
	{
		Id = id,
		Type = BaseItemDto_Type.Movie,
		People = [.. Enumerable.Range(0, CastSize).Select(k => new BaseItemPerson
		{
			Name = $"Person Name {(i * 31 + k) % 60_000}",
			Id = Guid.NewGuid(),
			Role = k is > 0 and < 30 ? $"Character Name {k}" : null,
			Type = k == 0 ? BaseItemPerson_Type.Director : k < 30 ? BaseItemPerson_Type.Actor : BaseItemPerson_Type.Producer,
			PrimaryImageTag = $"{(i * 31 + k):x8}9abcdef0123456789abcdef0",
			ImageBlurHashes = new BaseItemPerson_ImageBlurHashes { Primary = new BaseItemPerson_ImageBlurHashes_Primary { AdditionalData = new Dictionary<string, object> { [$"{(i * 31 + k):x8}9abcdef0123456789abcdef0"] = "WEHV6nWB2yk8pyo0adR*.7kCMdnjS#M|%1%2Sis.slNHRjt7ayof" } } },
		})],
	};

	private (Mock<IJellyfinClient> Client, List<BaseItemDto> Films) Server(Func<int, CancellationToken, Task>? peopleWait = null)
	{
		var films = Enumerable.Range(0, Films).Select(Film).ToList();
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns("http://server");
		client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000181"));
		client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies }]);
		client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync($"{Films}:newest");
		// Moq keeps every invocation with what it returned: cleared at each page, or the mock, not the app, would hold every page
		client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) =>
			{
				client.Invocations.Clear();
				return new BaseItemDtoQueryResult { Items = [.. films.Skip(start).Take(limit)], TotalRecordCount = total ? films.Count : null };
			});
		client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool total, CancellationToken token) =>
			{
				client.Invocations.Clear();
				Interlocked.Increment(ref _peoplePages);
				if (peopleWait is not null)
				{
					await peopleWait(start, token);
				}

				List<BaseItemDto> page = [.. films.Skip(start).Take(limit).Select((x, k) => Cast(x.Id!.Value, start + k))];
				lock (_given)
				{
					_given.AddRange(page.Select(x => new WeakReference<BaseItemDto>(x)));
				}

				return new BaseItemDtoQueryResult { Items = page, TotalRecordCount = total ? films.Count : null };
			});
		client.Setup(x => x.GetPeopleByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) =>
			{
				client.Invocations.Clear();
				return (IReadOnlyList<BaseItemDto>?)[.. ids.Select((id, k) => Cast(id, k))];
			});
		return (client, films);
	}

	private int Given()
	{
		lock (_given)
		{
			return _given.Count;
		}
	}

	private LibraryIndex Index(Mock<IJellyfinClient> client, HomeCache homeCache) =>
		new(client.Object, homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);

	private static long Collected()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		return GC.GetTotalMemory(forceFullCollection: true);
	}

	// the most managed memory seen while `run` runs (sampled every 10 ms)
	private static async Task<(long Peak, TimeSpan Took)> Peak(Func<Task> run)
	{
		var peak = GC.GetTotalMemory(false);
		using var done = new CancellationTokenSource();
		var sampler = Task.Run(async () =>
		{
			while (!done.IsCancellationRequested)
			{
				peak = Math.Max(peak, GC.GetTotalMemory(false));
				await Task.Delay(10);
			}
		});
		var watch = Stopwatch.StartNew();
		await run();
		watch.Stop();
		await done.CancelAsync();
		await sampler;
		return (peak, watch.Elapsed);
	}

	// BH-180-m2's check: halfway through the people step, the pages that came are let go (only their directors and first three
	// billed are kept, as names): a page held whole keeps its 40 people, roles, image tags and blurhashes until the end
	[Fact]
	public async Task HalfwayThroughThePeopleStep_ThePagesThatCameAreLetGo()
	{
		var half = new TaskCompletionSource();
		var (client, _) = Server((start, token) => start >= Films / 2 ? half.Task.WaitAsync(token) : Task.CompletedTask);
		using var homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		using var index = Index(client, homeCache);
		Assert.NotNull(await index.GetAsync());
		for (var i = 0; i < 1_600 && Given() < Films / 2 - 1_000; i++)
		{
			await Task.Delay(25); // the pages before the half in; those from it waiting
		}

		await Task.Delay(200); // the last of them handed over
		client.Invocations.Clear();
		Collected();
		int given, held;
		lock (_given)
		{
			given = _given.Count;
			held = _given.Count(x => x.TryGetTarget(out _));
		}

		output.WriteLine($"halfway: {given:N0} titles' people pages came, {held:N0} still held");
		half.SetResult();
		await index.Settled.WaitAsync(TimeSpan.FromSeconds(60));
		Assert.True(given >= Films / 2 - 1_000, $"{given} came");
		Assert.InRange(held, 0, 2_000); // at most the two pages on their way
		Assert.Equal(Films, index.Current!.Entries.Count(x => x.People.Length == 4)); // a director and the first three billed, all kept
	}

	// the numbers for BH-180-m2: a first start's people step at 25,000 titles with real casts, its peak above the titles and
	// its time; and the same with R pressed halfway through it
	[Fact]
	public async Task ThePeopleStep_AtRealSize_PeakAndTime()
	{
		var (client, _) = Server();
		using var homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		using var index = Index(client, homeCache);
		Assert.NotNull(await index.GetAsync()); // the titles
		var titles = Collected();
		var (peak, took) = await Peak(() => index.Settled);
		Assert.Equal(Films, index.Current!.Entries.Count(x => x.People.Length > 0));
		output.WriteLine($"people step: {Films:N0} titles, {CastSize} people each: {took.TotalSeconds:0.0} s; peak {(peak - titles) / 1048576.0:0} MB above the titles ({titles / 1048576.0:0} MB)");

		// R halfway through a first start's people step
		var half = new TaskCompletionSource();
		lock (_given)
		{
			_given.Clear();
		}

		var (rClient, _) = Server((start, token) => start >= Films / 2 ? half.Task.WaitAsync(token) : Task.CompletedTask);
		using var rHomeCache = new HomeCache(Path.Combine(_folder, "HomeCacheR"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		using var rIndex = new LibraryIndex(rClient.Object, rHomeCache, Path.Combine(_folder, "LibraryIndexR"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System)
		{
			WholeReadEvery = TimeSpan.Zero, // 1.9 (the index's load): R reads every title again, as before (the day's limit: IndexOnABigServerTests.Load190.cs)
		};
		Assert.NotNull(await rIndex.GetAsync());
		var rTitles = Collected();
		var (rPeak, rTook) = await Peak(async () =>
		{
			for (var i = 0; i < 1_600 && Given() < Films / 2 - 1_000; i++)
			{
				await Task.Delay(25);
			}

			var refreshed = rIndex.RefreshAsync(); // R
			half.SetResult();
			await refreshed;
			await rIndex.Settled;
		});
		Assert.Equal(Films, rIndex.Current!.Entries.Count(x => x.People.Length > 0));
		output.WriteLine($"with R halfway: {rTook.TotalSeconds:0.0} s; peak {(rPeak - rTitles) / 1048576.0:0} MB above the titles");
	}
}
