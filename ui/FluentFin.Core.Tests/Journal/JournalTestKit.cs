using System.Collections.Concurrent;
using System.Text.Json;
using FluentFin.Core.Journal;
using FluentFin.Core.Mpv;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Core.Tests.Journal;

/// <summary>A clock the test moves; a fixed local time zone (UTC-8, no daylight saving) so month boundaries are certain.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
	public static readonly TimeZoneInfo UtcMinus8 = TimeZoneInfo.CreateCustomTimeZone("journal-test-8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");

	public DateTimeOffset Now { get; set; } = now;

	public override DateTimeOffset GetUtcNow() => Now;

	public override TimeZoneInfo LocalTimeZone => UtcMinus8;

	public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeSession(JournalProfile? current) : IJournalSession
{
	public JournalProfile? Current { get; set; } = current;
}

/// <summary>
/// Jellyfin as the journal sees it. <see cref="State"/> is the user data the server has now (the test changes it the way the
/// server would). Records every call. <see cref="Down"/> makes every call throw what a hung server gives: HttpClient's timeout.
/// </summary>
internal sealed class FakeJournalServer : IJournalServer
{
	private int _userDataReads;

	public ConcurrentDictionary<Guid, JournalEpisode> Items { get; } = new();

	public ConcurrentDictionary<Guid, JournalUserData> State { get; } = new();

	/// <summary>What <see cref="GetPlayedEpisodesAsync"/> pages through (the test orders it, newest played first).</summary>
	public List<JournalEpisode> Played { get; } = [];

	public PlaybackHistory History { get; set; } = PlaybackHistory.Unavailable("HTTP 404");

	public bool Down { get; set; }

	/// <summary>Page requests from this start index on fail.</summary>
	public int? FailPagesFrom { get; set; }

	private readonly ConcurrentQueue<string> _calls = new();

	/// <summary>Every call, and the managed thread it was made on.</summary>
	public ConcurrentQueue<int> Threads { get; } = new();

	public CallLog Calls => new(_calls, Threads);

	/// <summary>Records a call with its thread.</summary>
	public sealed class CallLog(ConcurrentQueue<string> calls, ConcurrentQueue<int> threads) : IEnumerable<string>
	{
		public int Count => calls.Count;

		public void Enqueue(string call)
		{
			threads.Enqueue(Environment.CurrentManagedThreadId);
			calls.Enqueue(call);
		}

		public IEnumerator<string> GetEnumerator() => calls.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public ConcurrentQueue<(Guid ItemId, DateTimeOffset Date)> MarkedPlayed { get; } = new();

	public ConcurrentQueue<(Guid ItemId, int Rating)> Ratings { get; } = new();

	public bool RefuseMarkPlayed { get; set; }

	public int UserDataReads => Volatile.Read(ref _userDataReads);

	public JournalEpisode AddEpisode(string series, int season, int episode, string? title = null, long? runtimeTicks = null, Guid? id = null, Guid? seriesId = null)
	{
		var item = new JournalEpisode(id ?? Guid.NewGuid(), JournalEpisode.EpisodeType, title ?? $"Episode {episode}", seriesId ?? SeriesIds.GetOrAdd(series, _ => Guid.NewGuid()), series,
			Guid.NewGuid(), season, episode, runtimeTicks ?? TimeSpan.FromMinutes(45).Ticks, $"still-{episode}", "poster", null);
		Items[item.Id] = item;
		return item;
	}

	public ConcurrentDictionary<string, Guid> SeriesIds { get; } = new();

	private static TaskCanceledException Timeout() => new("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());

	public async Task WaitForUserDataReads(int count)
	{
		var until = DateTime.UtcNow.AddSeconds(10);
		while (UserDataReads < count)
		{
			if (DateTime.UtcNow > until)
			{
				throw new TimeoutException($"{UserDataReads} user data reads, waited for {count}");
			}

			await Task.Delay(5);
		}
	}

	public Task<JournalUserData?> GetUserDataAsync(Guid itemId, CancellationToken cancellationToken = default)
	{
		Calls.Enqueue($"userdata {itemId:N}");
		Interlocked.Increment(ref _userDataReads);
		return Down ? Task.FromException<JournalUserData?>(Timeout()) : Task.FromResult<JournalUserData?>(State.TryGetValue(itemId, out var data) ? data : new JournalUserData(false, 0, null));
	}

	public Task<JournalEpisode?> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default)
	{
		Calls.Enqueue($"item {itemId:N}");
		if (Down)
		{
			return Task.FromException<JournalEpisode?>(Timeout());
		}

		if (!Items.TryGetValue(itemId, out var item))
		{
			return Task.FromResult<JournalEpisode?>(null);
		}

		return Task.FromResult<JournalEpisode?>(item with { UserData = State.TryGetValue(itemId, out var data) ? data : new JournalUserData(false, 0, null) });
	}

	public Task<JournalPage<JournalEpisode>?> GetPlayedEpisodesAsync(int startIndex, int limit, CancellationToken cancellationToken = default)
	{
		Calls.Enqueue($"played {startIndex} {limit}");
		if (Down)
		{
			return Task.FromException<JournalPage<JournalEpisode>?>(Timeout());
		}

		if (startIndex >= FailPagesFrom)
		{
			return Task.FromResult<JournalPage<JournalEpisode>?>(null);
		}

		var page = Played.Skip(startIndex).Take(limit).ToList();
		return Task.FromResult<JournalPage<JournalEpisode>?>(new JournalPage<JournalEpisode>(page, Played.Count));
	}

	public Task<bool> MarkPlayedAsync(Guid itemId, DateTimeOffset datePlayed, CancellationToken cancellationToken = default)
	{
		Calls.Enqueue($"markplayed {itemId:N}");
		if (Down)
		{
			return Task.FromException<bool>(Timeout());
		}

		if (RefuseMarkPlayed)
		{
			return Task.FromResult(false);
		}

		MarkedPlayed.Enqueue((itemId, datePlayed));
		var before = State.TryGetValue(itemId, out var data) ? data : new JournalUserData(false, 0, null);
		State[itemId] = before with { Played = true, PlayCount = before.PlayCount + 1, LastPlayedDate = datePlayed };
		return Task.FromResult(true);
	}

	public Task<bool> SetRatingAsync(Guid itemId, int rating, CancellationToken cancellationToken = default)
	{
		Calls.Enqueue($"rating {itemId:N} {rating}");
		if (Down)
		{
			return Task.FromException<bool>(Timeout());
		}

		Ratings.Enqueue((itemId, rating));
		return Task.FromResult(true);
	}

	public Task<PlaybackHistory> GetPlaybackHistoryAsync(Guid userId, CancellationToken cancellationToken = default)
	{
		Calls.Enqueue("history");
		return Down ? Task.FromException<PlaybackHistory>(Timeout()) : Task.FromResult(History);
	}
}

/// <summary>A UI thread as far as the journal can tell: one thread with its own synchronization context.</summary>
internal sealed class UiThread : IDisposable
{
	private readonly BlockingCollection<Action> _queue = new();
	private readonly Thread _thread;

	public UiThread()
	{
		using var started = new ManualResetEventSlim();
		_thread = new Thread(() =>
		{
			ThreadId = Environment.CurrentManagedThreadId;
			SynchronizationContext.SetSynchronizationContext(new Context(_queue));
			started.Set();
			foreach (var work in _queue.GetConsumingEnumerable())
			{
				work();
			}
		})
		{ IsBackground = true, Name = "journal test UI thread" };
		_thread.Start();
		started.Wait();
	}

	public int ThreadId { get; private set; }

	/// <summary>Starts <paramref name="work"/> on this thread; completes when it has.</summary>
	public Task Run(Func<Task> work)
	{
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_queue.Add(() => work().ContinueWith(t =>
		{
			if (t.Exception is { } ex)
			{
				done.TrySetException(ex.InnerExceptions);
			}
			else
			{
				done.TrySetResult();
			}
		}, TaskScheduler.Default));
		return done.Task;
	}

	public void Dispose() => _queue.CompleteAdding();

	private sealed class Context(BlockingCollection<Action> queue) : SynchronizationContext
	{
		public override void Post(SendOrPostCallback d, object? state)
		{
			try
			{
				queue.Add(() => d(state));
			}
			catch (InvalidOperationException)
			{
				// the test is over
			}
		}

		public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

		public override SynchronizationContext CreateCopy() => this;
	}
}

/// <summary>A journal in a temp folder of its own, deleted afterwards. Never the app's data folder.</summary>
internal sealed class TempJournal : IDisposable
{
	public static readonly JournalProfile Alice = new("server-a", Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Home", "alice", "http://jf.test:8096");
	public static readonly JournalProfile Bob = new("server-a", Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), "Home", "bob", "http://jf.test:8096");
	public static readonly JournalProfile AliceElsewhere = new("server-b", Alice.UserId, "Cabin", "alice", "http://cabin.test:8096");

	public TempJournal(JournalProfile? signedIn = null, DateTimeOffset? now = null)
	{
		Folder = Path.Combine(Path.GetTempPath(), "FluentFinJournalTests", Guid.NewGuid().ToString("N"));
		Clock = new ManualClock(now ?? new DateTimeOffset(2026, 11, 14, 20, 0, 0, TimeSpan.Zero));
		Store = new JournalStore(Path.Combine(Folder, JournalStore.FileName), NullLogger<JournalStore>.Instance, Clock);
		Session = new FakeSession(signedIn ?? Alice);
		Service = new JournalService(Store, Server, Session, NullLogger<JournalService>.Instance, Clock);
	}

	public string Folder { get; }

	public ManualClock Clock { get; }

	public JournalStore Store { get; }

	public FakeSession Session { get; }

	public FakeJournalServer Server { get; } = new();

	public JournalService Service { get; }

	public JournalSync NewSync() => new(Service, NullLogger<JournalSync>.Instance);

	public void Dispose()
	{
		Store.Dispose();
		SqliteConnection.ClearAllPools();
		try
		{
			Directory.Delete(Folder, true);
		}
		catch (IOException)
		{
		}
	}
}

/// <summary>Bridge events as the backend writes them (docs/BRIDGE.md §5).</summary>
internal static class Events
{
	public static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

	public static BridgeEvent Of(string name, object data) => new(name, JsonSerializer.SerializeToNode(data, Bridge.JsonOptions));

	public static BridgeEvent Hello() => Of(Bridge.Events.Hello, new { protocol = 1, backend = "jellyfin-mpv-shim" });

	public static BridgeEvent State(string state, Guid itemId, string? playSessionId, long? durationTicks = null) =>
		Of(Bridge.Events.State, new { state, connected = true, itemId = itemId.ToString("N"), playSessionId, positionTicks = 0, durationTicks });

	public static BridgeEvent Idle() => Of(Bridge.Events.State, new { state = "idle", connected = true });

	public static BridgeEvent Item(Guid itemId, string type = "Episode", string? seriesName = "Show", int? season = 1, int? episode = 1, long? runTimeTicks = null, string? name = "Pilot") =>
		Of(Bridge.Events.Item, new { itemId = itemId.ToString("N"), name, type, seriesName, seasonNumber = season, episodeNumber = episode, runTimeTicks });

	public static BridgeEvent Position(long positionTicks, long? durationTicks = null) => Of(Bridge.Events.Position, new { positionTicks, durationTicks });

	public static BridgeEvent Ended(Guid itemId, string reason) => Of(Bridge.Events.Ended, new { itemId = itemId.ToString("N"), reason });

	public static BridgeEvent Reported(Guid itemId, string playSessionId, string outcome) =>
		Of(Bridge.Events.Reported, new { itemId = itemId.ToString("N"), playSessionId, outcome });

	/// <summary>Positions every 5 s of media from <paramref name="from"/> to <paramref name="to"/> (normal playback).</summary>
	public static IEnumerable<BridgeEvent> Play(long from, long to, long durationTicks)
	{
		var step = TimeSpan.FromSeconds(5).Ticks;
		for (var position = from; position < to; position += step)
		{
			yield return Position(position, durationTicks);
		}

		yield return Position(to, durationTicks);
	}
}
