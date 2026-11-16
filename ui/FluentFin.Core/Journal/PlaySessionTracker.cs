using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Journal;

/// <summary>The bridge's <c>item</c> event (docs/BRIDGE.md §5).</summary>
public sealed record BridgeItemInfo(string? ItemId, string? Name, string? Type, string? SeriesName, int? SeasonNumber, int? EpisodeNumber, long? RunTimeTicks);

/// <summary>The bridge's <c>reported</c> event (docs/BRIDGE.md §5).</summary>
public sealed record BridgeReported(string? ItemId, string? PlaySessionId, string? Outcome)
{
	public const string Sent = "sent";
	public const string Failed = "failed";
	public const string None = "none";
}

/// <summary>What the tracker saw that the auto-logger acts on.</summary>
public abstract record TrackerSignal;

/// <summary>A play session showed up in a <c>state</c> for the first time.</summary>
public sealed record PlayOpened(string PlaySessionId, Guid ItemId, DateTimeOffset At) : TrackerSignal;

/// <summary>
/// The backend is done with a play session's Stopped report (its <c>reported</c>), with everything seen of that session:
/// where it started and ended, how much of it actually played (<see cref="WatchedTicks"/>: position steps of normal playback,
/// not seeks), and how its item ended.
/// </summary>
public sealed record PlayCompleted(
	string PlaySessionId,
	Guid ItemId,
	string Outcome,
	BridgeItemInfo? Item,
	DateTimeOffset OpenedAt,
	DateTimeOffset? EndedAt,
	string? EndedReason,
	long? StartPositionTicks,
	long? LastPositionTicks,
	long? DurationTicks,
	long WatchedTicks) : TrackerSignal
{
	/// <summary>The runtime: mpv's duration when known, else the server's runtime from the <c>item</c> event.</summary>
	public long? RuntimeTicks => DurationTicks is > 0 ? DurationTicks : Item?.RunTimeTicks is > 0 ? Item.RunTimeTicks : null;
}

/// <summary>
/// Follows play sessions through the backend's events (docs/BRIDGE.md §5) and says when one is closed by its <c>reported</c>.
/// No I/O and no clock of its own (times come in), so the auto-log rules are tested on event sequences. A session is the
/// one the latest <c>state</c> shows; positions belong to it until its item's <c>ended</c>. A <c>hello</c> (a new backend
/// process) forgets every session of the old one: a dead backend never reports them (the next sync logs what the server
/// counted). Thread-safe: events of two backend processes can come from two reader threads.
/// </summary>
public sealed class PlaySessionTracker
{
	/// <summary>A position step larger than this (media time between two ~500 ms events) is a seek, not watching.</summary>
	public static readonly long MaxWatchStepTicks = TimeSpan.FromSeconds(10).Ticks;

	private const int MaxRemembered = 256;

	private readonly Lock _lock = new();
	private readonly Dictionary<string, Track> _sessions = new(StringComparer.Ordinal);
	private readonly Dictionary<Guid, BridgeItemInfo> _items = [];
	private readonly Queue<Guid> _itemOrder = new();
	private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
	private readonly Queue<string> _closedOrder = new();
	private string? _current;

	public IReadOnlyList<TrackerSignal> Handle(BridgeEvent e, DateTimeOffset now)
	{
		lock (_lock)
		{
			try
			{
				return e.Name switch
				{
					Bridge.Events.Hello => Reset(),
					Bridge.Events.State => OnState(e, now),
					Bridge.Events.Item => OnItem(e),
					Bridge.Events.Position => OnPosition(e),
					Bridge.Events.Ended => OnEnded(e, now),
					Bridge.Events.Reported => OnReported(e),
					_ => [],
				};
			}
			catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
			{
				// an event this version cannot read changes nothing
				return [];
			}
		}
	}

	/// <summary>Sessions waiting for their <c>reported</c> (for tests and logs).</summary>
	public int OpenCount
	{
		get
		{
			lock (_lock)
			{
				return _sessions.Count;
			}
		}
	}

	private IReadOnlyList<TrackerSignal> Reset()
	{
		// "at most one reported per play session" holds per backend process (docs/BRIDGE.md §5); across processes the
		// journal's store is the dedupe
		_sessions.Clear();
		_closed.Clear();
		_closedOrder.Clear();
		_current = null;
		return [];
	}

	private IReadOnlyList<TrackerSignal> OnState(BridgeEvent e, DateTimeOffset now)
	{
		if (e.DataAs<BridgeState>() is not { } state || !state.IsActive || state.PlaySessionId is not { Length: > 0 } playSessionId || !Guid.TryParse(state.ItemId, out var itemId))
		{
			return [];
		}

		_current = playSessionId;
		if (_sessions.TryGetValue(playSessionId, out var known))
		{
			if (state.DurationTicks is > 0)
			{
				known.DurationTicks = state.DurationTicks;
			}

			return [];
		}

		if (_closed.Contains(playSessionId))
		{
			// its reported came first (snapshots and reports come from different threads in the backend)
			return [];
		}

		_sessions[playSessionId] = new Track(playSessionId, itemId, now) { DurationTicks = state.DurationTicks is > 0 ? state.DurationTicks : null };
		return [new PlayOpened(playSessionId, itemId, now)];
	}

	private IReadOnlyList<TrackerSignal> OnItem(BridgeEvent e)
	{
		if (e.DataAs<BridgeItemInfo>() is { } item && Guid.TryParse(item.ItemId, out var itemId))
		{
			if (_items.TryAdd(itemId, item))
			{
				_itemOrder.Enqueue(itemId);
				while (_itemOrder.Count > MaxRemembered && _itemOrder.TryDequeue(out var old))
				{
					_items.Remove(old);
				}
			}
			else
			{
				_items[itemId] = item;
			}
		}

		return [];
	}

	private IReadOnlyList<TrackerSignal> OnPosition(BridgeEvent e)
	{
		if (_current is null || !_sessions.TryGetValue(_current, out var track) || track.EndedReason is not null || e.Data is not JsonObject data)
		{
			return [];
		}

		var position = data["positionTicks"]?.GetValue<long>();
		var duration = data["durationTicks"]?.GetValueKind() == JsonValueKind.Number ? data["durationTicks"]!.GetValue<long>() : (long?)null;
		if (duration is > 0)
		{
			track.DurationTicks = duration;
		}

		if (position is not { } ticks || ticks < 0)
		{
			return [];
		}

		track.StartPositionTicks ??= ticks;
		if (track.LastPositionTicks is { } last && ticks > last && ticks - last <= MaxWatchStepTicks)
		{
			track.WatchedTicks += ticks - last;
		}

		track.LastPositionTicks = ticks;
		return [];
	}

	private IReadOnlyList<TrackerSignal> OnEnded(BridgeEvent e, DateTimeOffset now)
	{
		if (e.DataAs<BridgeEnded>() is not { } ended || !Guid.TryParse(ended.ItemId, out var itemId))
		{
			return [];
		}

		// the newest session of that item that has not ended
		var track = _sessions.Values.Where(x => x.ItemId == itemId && x.EndedReason is null).MaxBy(x => x.Order);
		if (track is not null)
		{
			track.EndedReason = ended.Reason ?? "";
			track.EndedAt = now;
		}

		return [];
	}

	private IReadOnlyList<TrackerSignal> OnReported(BridgeEvent e)
	{
		if (e.DataAs<BridgeReported>() is not { PlaySessionId: { Length: > 0 } playSessionId } reported)
		{
			return [];
		}

		if (_closed.Add(playSessionId))
		{
			_closedOrder.Enqueue(playSessionId);
			while (_closedOrder.Count > MaxRemembered && _closedOrder.TryDequeue(out var old))
			{
				_closed.Remove(old);
			}
		}

		if (!_sessions.Remove(playSessionId, out var track))
		{
			// a session no state showed (one that failed before any snapshot): nothing seen of it to log
			return [];
		}

		if (_current == playSessionId)
		{
			_current = null;
		}

		_items.TryGetValue(track.ItemId, out var item);
		return [new PlayCompleted(playSessionId, track.ItemId, reported.Outcome ?? "", item, track.OpenedAt, track.EndedAt, track.EndedReason,
			track.StartPositionTicks, track.LastPositionTicks, track.DurationTicks, track.WatchedTicks)];
	}

	private sealed class Track(string playSessionId, Guid itemId, DateTimeOffset openedAt)
	{
		private static long _orders;

		public string PlaySessionId { get; } = playSessionId;

		public Guid ItemId { get; } = itemId;

		public DateTimeOffset OpenedAt { get; } = openedAt;

		public long Order { get; } = Interlocked.Increment(ref _orders);

		public long? StartPositionTicks { get; set; }

		public long? LastPositionTicks { get; set; }

		public long? DurationTicks { get; set; }

		public long WatchedTicks { get; set; }

		public string? EndedReason { get; set; }

		public DateTimeOffset? EndedAt { get; set; }
	}
}
