using System.Collections.Concurrent;
using FluentFin.Core.Mpv;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Journal;

/// <summary>
/// Writes a diary entry when an episode finishes in the mpv player (docs/JOURNAL.md, decision 3; the rules are
/// <see cref="AutoLogRules"/>). Only listens to the backend's events (docs/BRIDGE.md §5): it sends nothing to the backend and
/// never reports playback (the backend is the one reporter); its only writes to Jellyfin are the mark-played it queues when a
/// Stopped report failed. The event handler only records and queues: the reads from the server and the store writes run one
/// at a time on the thread pool, so the backend's reader thread is never held up. Never throws into the event stream.
/// </summary>
public sealed class JournalAutoLogger : IDisposable
{
	/// <summary>How long a decision waits for the "before" user data read when the session opened.</summary>
	public static readonly TimeSpan BaselineWait = TimeSpan.FromSeconds(10);

	private readonly IObservable<BridgeEvent> _events;
	private readonly JournalService _journal;
	private readonly ILogger<JournalAutoLogger> _logger;
	private readonly PlaySessionTracker _tracker = new();
	private readonly ConcurrentDictionary<string, Opened> _opened = new(StringComparer.Ordinal);
	private readonly Lock _lock = new();
	private Task _tail = Task.CompletedTask;
	private IDisposable? _subscription;

	public JournalAutoLogger(IObservable<BridgeEvent> events, JournalService journal, ILogger<JournalAutoLogger> logger)
	{
		_events = events;
		_journal = journal;
		_logger = logger;
	}

	/// <summary>Starts listening (once; later calls do nothing).</summary>
	public void Start()
	{
		lock (_lock)
		{
			_subscription ??= _events.Subscribe(OnEvent);
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			_subscription?.Dispose();
			_subscription = null;
		}
	}

	/// <summary>Completes when everything queued so far has been handled (for tests and for closing).</summary>
	public Task WhenIdle()
	{
		lock (_lock)
		{
			return _tail;
		}
	}

	private void OnEvent(BridgeEvent e)
	{
		try
		{
			if (e.Name == Bridge.Events.Hello)
			{
				// a new backend process: the sessions of the old one never get their reported
				_opened.Clear();
			}

			foreach (var signal in _tracker.Handle(e, _journal.Clock.GetUtcNow()))
			{
				switch (signal)
				{
					case PlayOpened opened:
						OnOpened(opened);
						break;
					case PlayCompleted completed:
						Enqueue(() => ProcessAsync(completed));
						break;
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Journal: the mpv event {Event} could not be followed", e.Name);
		}
	}

	private void OnOpened(PlayOpened opened)
	{
		var profile = _journal.Session.Current;
		var baseline = profile is null ? Task.FromResult<JournalUserData?>(null) : Task.Run(() => ReadBaselineAsync(opened.ItemId));
		_opened[opened.PlaySessionId] = new Opened(profile, baseline);
	}

	private async Task<JournalUserData?> ReadBaselineAsync(Guid itemId)
	{
		try
		{
			return _journal.GetSettings().AutoLog ? await _journal.Server.GetUserDataAsync(itemId) : null;
		}
		catch (Exception ex)
		{
			_logger.LogDebug("Journal: no user data before the play of {ItemId}: {Error}", itemId, ex.GetType().Name);
			return null;
		}
	}

	private void Enqueue(Func<Task> work)
	{
		lock (_lock)
		{
			_tail = _tail.ContinueWith(async _ =>
			{
				try
				{
					await work();
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Journal: logging a finished play failed");
				}
			}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
		}
	}

	private async Task ProcessAsync(PlayCompleted play)
	{
		_opened.TryRemove(play.PlaySessionId, out var opened);
		var profile = opened?.Profile ?? _journal.Session.Current;
		if (profile is null)
		{
			return;
		}

		JournalSettings settings;
		try
		{
			settings = _journal.GetSettings();
			if (!settings.AutoLog || _journal.Store.HasPlaySession(profile, play.PlaySessionId))
			{
				return;
			}
		}
		catch (JournalUnavailableException ex)
		{
			_logger.LogWarning("Journal: not logging {ItemId}: {Message}", play.ItemId, ex.Message);
			return;
		}

		if (play.Item?.Type is { } type && !string.Equals(type, JournalEpisode.EpisodeType, StringComparison.OrdinalIgnoreCase))
		{
			// TV first (decision 1): movies are a later increment
			return;
		}

		if (play.Outcome is not (BridgeReported.Sent or BridgeReported.Failed) || AutoLogRules.IsShortPlay(play))
		{
			_logger.LogDebug("Journal: not logging play session {PlaySession} of {ItemId} ({Outcome}, watched {Watched})", play.PlaySessionId, play.ItemId, play.Outcome, TimeSpan.FromTicks(play.WatchedTicks));
			return;
		}

		JournalUserData? before = null;
		if (opened is not null)
		{
			try
			{
				before = await opened.Baseline.WaitAsync(BaselineWait);
			}
			catch (TimeoutException)
			{
			}
		}

		// the server is asked only while FluentFin is still signed in as the server and user the session was played as
		var canAsk = _journal.Session.Current == profile;
		var item = canAsk ? await _journal.Server.GetItemAsync(play.ItemId) : null;
		if (item is not null && !item.IsEpisode)
		{
			return;
		}

		if (item is null && play.Item?.Type is null)
		{
			_logger.LogDebug("Journal: not logging {ItemId}: neither the server nor the backend said what it is", play.ItemId);
			return;
		}

		var after = play.Outcome == BridgeReported.Sent && canAsk ? item?.UserData ?? await _journal.Server.GetUserDataAsync(play.ItemId) : null;
		var decision = AutoLogRules.Decide(play, before, after, canAsk);
		if (!decision.Log)
		{
			_logger.LogInformation("Journal: play session {PlaySession} of {ItemId} not logged: {Reason}", play.PlaySessionId, play.ItemId, decision.Reason);
			return;
		}

		var store = _journal.Store;
		var watchedAt = play.EndedAt ?? _journal.Clock.GetUtcNow();
		var entry = store.AddEntry(profile, new DiaryEntry
		{
			ItemId = play.ItemId,
			SeriesId = item?.SeriesId,
			SeasonId = item?.SeasonId,
			SeriesName = item?.SeriesName ?? play.Item?.SeriesName,
			SeasonNumber = item?.SeasonNumber ?? play.Item?.SeasonNumber,
			EpisodeNumber = item?.EpisodeNumber ?? play.Item?.EpisodeNumber,
			Title = item?.Name ?? play.Item?.Name,
			RuntimeTicks = item?.RuntimeTicks ?? play.RuntimeTicks,
			ImageTag = item?.ImageTag,
			SeriesImageTag = item?.SeriesImageTag,
			WatchedAt = watchedAt,
			DateSource = DiaryDateSource.Session,
			Source = DiaryEntrySource.AutoLog,
			Rewatch = store.HasEntryFor(profile, play.ItemId) || before?.Played == true,
			PlaySessionId = play.PlaySessionId,
			OnServer = decision.OnServer,
		});

		if (entry is null)
		{
			// the same play session was logged meanwhile
			return;
		}

		_logger.LogInformation("Journal: logged {ItemId} (play session {PlaySession}): {Reason}", play.ItemId, play.PlaySessionId, decision.Reason);
		if (!decision.OnServer)
		{
			store.AddPendingAction(profile, play.ItemId, PendingJournalAction.MarkPlayed, watchedAt, entry.Id);
		}

		_journal.NotifyChanged(profile);
		if (canAsk && play.Outcome == BridgeReported.Sent)
		{
			// the server answers again: anything still owed can go now
			await _journal.DrainPendingAsync(profile);
		}
	}

	private sealed record Opened(JournalProfile? Profile, Task<JournalUserData?> Baseline);
}
