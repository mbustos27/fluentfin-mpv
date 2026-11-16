using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Journal;

public sealed record JournalSyncResult(bool Completed, int Added, bool Backfill, bool ExactDates, string? Error)
{
	public static JournalSyncResult Failed(string error, bool backfill = false) => new(false, 0, backfill, false, error);
}

/// <summary>
/// Brings the journal up to date with Jellyfin (docs/JOURNAL.md, decision 2), with no server plugin:
/// <list type="bullet">
/// <item>The first sync of a journal is the <b>backfill</b>: one entry per played episode, dated by Jellyfin's
/// <c>LastPlayedDate</c> (the last play only, so "approximate").</item>
/// <item>Every later sync is a <b>catch-up</b>: played episodes whose <c>LastPlayedDate</c> is newer than the last sync
/// (less a day of overlap for clock differences), so plays from other clients land in the diary. A play is skipped when
/// the journal has an entry of the episode logged at or after the date this sync would write, less 12 hours.</item>
/// <item>When the Playback Reporting plugin's history is readable, its last play of an episode gives the exact date.</item>
/// </list>
/// Runs on the thread pool, a page (200 episodes) per transaction. Deleted entries stay as tombstones, so a sync never
/// brings back what the user deleted. It stops, writing nothing more and not saving its state, as soon as FluentFin is
/// signed in as someone else (the server's answers would be that user's). A failed page keeps the last sync time: the next
/// sync covers the same ground again. One sync at a time; a second request while one runs does nothing.
/// </summary>
public sealed class JournalSync(JournalService journal, ILogger<JournalSync> logger)
{
	public const int PageSize = 200;

	/// <summary>A catch-up re-reads this much before the last sync (the server's and this PC's clocks may differ).</summary>
	public static readonly TimeSpan Overlap = TimeSpan.FromDays(1);

	/// <summary>An entry of the item logged within this before a play's date (or after it) is that play.</summary>
	public static readonly TimeSpan SamePlayWindow = TimeSpan.FromHours(12);

	public const string SwitchedError = "FluentFin was signed in as someone else during the sync";

	private readonly SemaphoreSlim _gate = new(1, 1);

	public bool IsRunning => _gate.CurrentCount == 0;

	/// <summary>Starts a sync; its result is logged. For the start of a session.</summary>
	public void SyncInBackground(string reason)
	{
		_ = Task.Run(async () =>
		{
			var result = await SyncAsync();
			logger.LogInformation("Journal sync ({Reason}): {Result}", reason, result);
		});
	}

	/// <summary>Never throws (but for cancellation); runs on the thread pool whoever calls it.</summary>
	public async Task<JournalSyncResult> SyncAsync(CancellationToken cancellationToken = default)
	{
		if (journal.CurrentProfile is not { } profile)
		{
			return JournalSyncResult.Failed("not signed in");
		}

		if (!await _gate.WaitAsync(0, cancellationToken))
		{
			return JournalSyncResult.Failed("a sync is already running");
		}

		try
		{
			// a backfill writes thousands of rows: never on the caller's (UI) thread
			return await Task.Run(() => RunAsync(profile, cancellationToken), cancellationToken).ConfigureAwait(false);
		}
		catch (JournalUnavailableException ex)
		{
			logger.LogWarning("Journal sync: {Message}", ex.Message);
			return JournalSyncResult.Failed(ex.Message);
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			logger.LogWarning(ex, "Journal sync failed");
			return JournalSyncResult.Failed("the sync failed");
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task<JournalSyncResult> RunAsync(JournalProfile profile, CancellationToken cancellationToken)
	{
		var store = journal.Store;
		var server = journal.Server;
		var state = store.GetSyncState(profile);
		var startedAt = journal.Clock.GetUtcNow();
		var backfill = state.LastSyncAt is null;
		var stopBefore = state.LastSyncAt - Overlap;
		var added = 0;

		// every answer is checked against who is signed in once it arrives: the server answers as the current user
		bool SignedIn() => journal.Session.Current == profile;

		JournalSyncResult Switched()
		{
			logger.LogInformation("Journal sync of {Profile} stopped after {Added} entries: {Reason}", profile, added, SwitchedError);
			if (added > 0)
			{
				journal.NotifyChanged(profile);
			}

			return new JournalSyncResult(false, added, backfill, false, SwitchedError);
		}

		var history = await server.GetPlaybackHistoryAsync(profile.UserId, cancellationToken).ConfigureAwait(false);
		if (!SignedIn())
		{
			return Switched();
		}

		var lastPlays = history.Available
			? history.Plays.GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.MaxBy(x => x.EndedAt)!)
			: [];
		TimeSpan? clockShift = null;

		var start = 0;
		while (true)
		{
			var page = await server.GetPlayedEpisodesAsync(start, PageSize, cancellationToken).ConfigureAwait(false);
			if (!SignedIn())
			{
				return Switched();
			}

			if (page is null)
			{
				store.SaveSyncState(profile, state with { LastError = "Jellyfin did not answer", PlaybackReportingAvailable = history.Available, PlaybackReportingCheckedAt = startedAt });
				if (added > 0)
				{
					journal.NotifyChanged(profile);
				}

				return new JournalSyncResult(false, added, backfill, history.Available, "Jellyfin did not answer");
			}

			// Playback Reporting keeps the server's local time; read in this PC's zone, then moved by what Jellyfin's own
			// dates of the same plays say the difference is (a server in another zone)
			clockShift ??= lastPlays.Count == 0 ? TimeSpan.Zero : PlaybackReportingQuery.ClockShift(page.Items
				.Where(x => x.UserData?.LastPlayedDate is not null && lastPlays.ContainsKey(x.Id))
				.Select(x => (lastPlays[x.Id].StartedAt, x.UserData!.LastPlayedDate!.Value)));

			var candidates = new List<SyncCandidate>(page.Items.Count);
			var reachedLastSync = false;
			foreach (var episode in page.Items)
			{
				var lastPlayed = episode.UserData?.LastPlayedDate;
				if (!backfill && (lastPlayed is null || lastPlayed < stopBefore))
				{
					// sorted by last played, newest first: everything from here on was there at the last sync
					reachedLastSync = true;
					break;
				}

				if (!episode.IsEpisode || episode.UserData is { Played: false })
				{
					continue;
				}

				var (watchedAt, dateSource) = lastPlays.TryGetValue(episode.Id, out var play)
					? (play.EndedAt + clockShift.Value, DiaryDateSource.PlaybackReporting)
					: lastPlayed is { } date ? (date, DiaryDateSource.LastPlayed) : ((DateTimeOffset?)null, DiaryDateSource.Unknown);

				candidates.Add(new SyncCandidate(new DiaryEntry
				{
					ItemId = episode.Id,
					SeriesId = episode.SeriesId,
					SeasonId = episode.SeasonId,
					SeriesName = episode.SeriesName,
					SeasonNumber = episode.SeasonNumber,
					EpisodeNumber = episode.EpisodeNumber,
					Title = episode.Name,
					RuntimeTicks = episode.RuntimeTicks,
					ImageTag = episode.ImageTag,
					SeriesImageTag = episode.SeriesImageTag,
					WatchedAt = watchedAt,
					DateSource = dateSource,
					Source = backfill ? DiaryEntrySource.Backfill : DiaryEntrySource.CatchUp,
				},
				// the backfill knows one play per episode: any entry is it. A catch-up compares with the date it writes.
				backfill ? null : (watchedAt ?? lastPlayed) - SamePlayWindow));
			}

			if (!SignedIn())
			{
				return Switched();
			}

			// a catch-up play of an episode already in the diary is a rewatch
			added += store.AddSyncedEntries(profile, candidates, markRewatches: !backfill);

			start += page.Items.Count;
			if (reachedLastSync || page.Items.Count == 0 || page.Items.Count < PageSize || (page.TotalCount is { } total && start >= total))
			{
				break;
			}
		}

		if (!SignedIn())
		{
			return Switched();
		}

		store.SaveSyncState(profile, new JournalSyncState
		{
			LastSyncAt = startedAt,
			BackfillDoneAt = state.BackfillDoneAt ?? journal.Clock.GetUtcNow(),
			PlaybackReportingAvailable = history.Available,
			PlaybackReportingCheckedAt = startedAt,
			LastError = null,
		});

		await journal.DrainPendingAsync(profile, cancellationToken).ConfigureAwait(false);
		if (added > 0)
		{
			journal.NotifyChanged(profile);
		}

		return new JournalSyncResult(true, added, backfill, history.Available, null);
	}
}
