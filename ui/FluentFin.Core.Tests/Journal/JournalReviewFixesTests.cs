using System.Globalization;
using System.Reactive.Subjects;
using FluentFin.Core.Journal;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static FluentFin.Core.Tests.Journal.Events;

namespace FluentFin.Core.Tests.Journal;

/// <summary>
/// The first review of the journal (2026-11-15): one group per defect, each test failing without its fix. Built from the
/// reviewer's probes (ReviewProbes.cs, ReviewProbes2.cs).
/// </summary>
public sealed class JournalReviewFixesTests : IDisposable
{
	private static readonly long Runtime = 45 * Minute;
	private static readonly JournalUserData Unplayed = new(false, 0, null);

	private readonly TempJournal _journal = new();

	public void Dispose() => _journal.Dispose();

	private FakeJournalServer Server => _journal.Server;

	private JournalStore Store => _journal.Store;

	private DateTimeOffset Now => _journal.Clock.Now;

	private JournalEpisode PlayedEpisode(string series, int episode, DateTimeOffset? lastPlayed, int playCount = 1)
	{
		var item = Server.AddEpisode(series, 1, episode, runtimeTicks: Runtime) with { UserData = new JournalUserData(true, playCount, lastPlayed) };
		Server.Items[item.Id] = item;
		Server.Played.Add(item);
		return item;
	}

	// ------------------------------------------------------------ 1. an account switch during a sync

	/// <summary>Answers pages as whoever is signed in, and switches the session to <paramref name="to"/> on page <paramref name="switchOnPage"/>.</summary>
	private sealed class SwitchingServer(FakeSession session, JournalProfile to, int switchOnPage, IReadOnlyList<JournalEpisode> episodes) : IJournalServer
	{
		private int _pages;

		public Task<JournalUserData?> GetUserDataAsync(Guid itemId, CancellationToken c = default) => Task.FromResult<JournalUserData?>(null);

		public Task<JournalEpisode?> GetItemAsync(Guid itemId, CancellationToken c = default) => Task.FromResult<JournalEpisode?>(null);

		public Task<JournalPage<JournalEpisode>?> GetPlayedEpisodesAsync(int startIndex, int limit, CancellationToken c = default)
		{
			if (_pages++ == switchOnPage)
			{
				// the real client answers as whoever is signed in by the time the request goes
				session.Current = to;
			}

			return Task.FromResult<JournalPage<JournalEpisode>?>(new JournalPage<JournalEpisode>([.. episodes.Skip(startIndex).Take(limit)], episodes.Count));
		}

		public Task<bool> MarkPlayedAsync(Guid itemId, DateTimeOffset d, CancellationToken c = default) => Task.FromResult(false);

		public Task<bool> SetRatingAsync(Guid itemId, int rating, CancellationToken c = default) => Task.FromResult(false);

		public Task<PlaybackHistory> GetPlaybackHistoryAsync(Guid userId, CancellationToken c = default) => Task.FromResult(PlaybackHistory.Unavailable("HTTP 403"));
	}

	private (JournalSync Sync, FakeSession Session) SwitchingSync(int switchOnPage, int episodes)
	{
		var list = Enumerable.Range(0, episodes)
			.Select(i => new JournalEpisode(Guid.NewGuid(), "Episode", $"E{i}", Guid.NewGuid(), "Show", Guid.NewGuid(), 1, i, Runtime, null, null, new JournalUserData(true, 1, Now.AddMinutes(-i))))
			.ToList();
		var session = new FakeSession(TempJournal.Alice);
		var service = new JournalService(Store, new SwitchingServer(session, TempJournal.Bob, switchOnPage, list), session, null, _journal.Clock);
		return (new JournalSync(service, NullLogger<JournalSync>.Instance), session);
	}

	[Fact]
	public async Task Fix1_ASwitchWhileAPageIsRead_WritesThatPageNowhere_AndSavesNoSyncState()
	{
		var (sync, _) = SwitchingSync(switchOnPage: 0, episodes: 3);

		var result = await sync.SyncAsync();

		Assert.False(result.Completed);
		Assert.Equal(JournalSync.SwitchedError, result.Error);
		Assert.Empty(Store.GetEntries(TempJournal.Alice));
		Assert.Empty(Store.GetEntries(TempJournal.Bob));
		Assert.Equal(new JournalSyncState(), Store.GetSyncState(TempJournal.Alice));
	}

	[Fact]
	public async Task Fix1_ASwitchAfterAPage_KeepsWhatWasReadAsTheUser_AndNothingAfter()
	{
		var (sync, _) = SwitchingSync(switchOnPage: 1, episodes: JournalSync.PageSize + 1);

		var result = await sync.SyncAsync();

		Assert.Equal((false, JournalSync.PageSize), (result.Completed, result.Added));
		Assert.Equal(JournalSync.PageSize, Store.GetEntries(TempJournal.Alice).Count);
		Assert.DoesNotContain(Store.GetEntries(TempJournal.Alice), x => x.EpisodeNumber == JournalSync.PageSize);
		Assert.Null(Store.GetSyncState(TempJournal.Alice).LastSyncAt);
	}

	[Fact]
	public async Task Fix1_QueuedWrites_StopWhenAnAnswerComesAsAnotherUser()
	{
		var session = new FakeSession(TempJournal.Alice);
		var server = new Mock<IJournalServer>();
		server.Setup(x => x.GetUserDataAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(() =>
		{
			session.Current = TempJournal.Bob;
			return Task.FromResult<JournalUserData?>(Unplayed);
		});
		var service = new JournalService(Store, server.Object, session, null, _journal.Clock);
		Store.AddPendingAction(TempJournal.Alice, Guid.NewGuid(), PendingJournalAction.MarkPlayed, Now, null);

		await service.DrainPendingAsync(TempJournal.Alice);

		server.Verify(x => x.MarkPlayedAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
		Assert.Equal(0, Assert.Single(Store.GetPendingActions(TempJournal.Alice)).Attempts);
	}

	// ------------------------------------------------------------ 2. writes on the UI thread

	[Fact]
	public async Task Fix2_TheSyncButton_ReadsAndWritesOffTheUiThread()
	{
		PlayedEpisode("Severance", 1, Now.AddDays(-1));
		PlayedEpisode("Severance", 2, Now.AddDays(-2));
		using var ui = new UiThread();
		var vm = new DiaryViewModel(_journal.Service, _journal.NewSync(), Mock.Of<INavigationServiceCore>(), NullLogger<DiaryViewModel>.Instance)
		{
			TimeZone = ManualClock.UtcMinus8,
			Culture = CultureInfo.GetCultureInfo("en-US"),
		};

		await ui.Run(async () =>
		{
			await vm.OnNavigatedTo(null!);
			await vm.SyncCommand.ExecuteAsync(null);
		});

		Assert.Equal(2, Store.GetEntries(TempJournal.Alice).Count);
		var pageThreads = Server.Calls.Zip(Server.Threads).Where(x => x.First.StartsWith("played", StringComparison.Ordinal)).Select(x => x.Second).ToList();
		Assert.NotEmpty(pageThreads);
		Assert.DoesNotContain(ui.ThreadId, pageThreads);
	}

	[Fact]
	public async Task Fix2_ARatingClick_IsSavedOffTheUiThread()
	{
		Store.SaveSettings(new JournalSettings { MirrorRatingToJellyfin = true });
		var vm = new JournalRatingViewModel(_journal.Service, NullLogger<JournalRatingViewModel>.Instance);
		var episode = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, Name = "Pilot" };
		using var ui = new UiThread();

		await ui.Run(async () =>
		{
			await vm.LoadAsync(episode);
			vm.Value = 7;
			await vm.LastSave;
		});

		Assert.Equal(7, Store.GetRating(TempJournal.Alice, episode.Id!.Value)!.Value);
		var ratingThread = Server.Calls.Zip(Server.Threads).Single(x => x.First.StartsWith("rating", StringComparison.Ordinal)).Second;
		Assert.NotEqual(ui.ThreadId, ratingThread);
	}

	// ------------------------------------------------------------ 3. a hung server (HttpClient's timeout) loses nothing

	/// <summary>The item read times out the way HttpClient does (TaskCanceledException, nobody cancelled); the rest answers.</summary>
	private sealed class TimeoutServer(FakeJournalServer inner) : IJournalServer
	{
		public Task<JournalUserData?> GetUserDataAsync(Guid itemId, CancellationToken c = default) => inner.GetUserDataAsync(itemId, c);

		public Task<JournalEpisode?> GetItemAsync(Guid itemId, CancellationToken c = default) =>
			Task.FromException<JournalEpisode?>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));

		public Task<JournalPage<JournalEpisode>?> GetPlayedEpisodesAsync(int s, int l, CancellationToken c = default) => inner.GetPlayedEpisodesAsync(s, l, c);

		public Task<bool> MarkPlayedAsync(Guid itemId, DateTimeOffset d, CancellationToken c = default) => inner.MarkPlayedAsync(itemId, d, c);

		public Task<bool> SetRatingAsync(Guid itemId, int rating, CancellationToken c = default) => inner.SetRatingAsync(itemId, rating, c);

		public Task<PlaybackHistory> GetPlaybackHistoryAsync(Guid userId, CancellationToken c = default) => inner.GetPlaybackHistoryAsync(userId, c);
	}

	[Fact]
	public async Task Fix3_AFailedReportWhileTheServerHangs_IsStillLoggedAndQueued()
	{
		var inner = new FakeJournalServer();
		var service = new JournalService(Store, new TimeoutServer(inner), _journal.Session, null, _journal.Clock);
		var events = new Subject<BridgeEvent>();
		using var logger = new JournalAutoLogger(events, service, NullLogger<JournalAutoLogger>.Instance);
		logger.Start();
		var id = Guid.NewGuid();

		foreach (var e in new[] { Item(id, runTimeTicks: Runtime), State("opening", id, "ps-t") })
		{
			events.OnNext(e);
		}

		await inner.WaitForUserDataReads(1);
		foreach (var e in Play(0, Runtime, Runtime).Prepend(State("playing", id, "ps-t", Runtime)).Concat([Ended(id, "eof"), Idle(), Reported(id, "ps-t", "failed")]))
		{
			events.OnNext(e);
		}

		await logger.WhenIdle();

		var entry = Assert.Single(Store.GetEntries(TempJournal.Alice));
		Assert.False(entry.OnServer);
		Assert.Equal(entry.Id, Assert.Single(Store.GetPendingActions(TempJournal.Alice)).EntryId);
	}

	[Fact]
	public async Task Fix3_ASyncAgainstAHungServer_FailsQuietly()
	{
		Server.Down = true;

		var result = await _journal.NewSync().SyncAsync();

		Assert.False(result.Completed);
		Assert.Empty(Store.GetEntries(TempJournal.Alice));
	}

	// ------------------------------------------------------------ 4. a restart near the end is one watch

	private void Send(Subject<BridgeEvent> events, params IEnumerable<BridgeEvent> sequence)
	{
		foreach (var e in sequence)
		{
			events.OnNext(e);
		}
	}

	[Fact]
	public async Task Fix4_ATrackChangeAt95Percent_IsOneWatch_NotARewatch()
	{
		var events = new Subject<BridgeEvent>();
		using var logger = new JournalAutoLogger(events, _journal.Service, NullLogger<JournalAutoLogger>.Instance);
		logger.Start();
		var ep = Server.AddEpisode("Show", 1, 1, runtimeTicks: Runtime);
		Server.State[ep.Id] = Unplayed;
		Send(events, Item(ep.Id, runTimeTicks: Runtime), State("opening", ep.Id, "ps-old"));
		await Server.WaitForUserDataReads(1);
		Send(events, State("playing", ep.Id, "ps-old", Runtime));
		Send(events, Play(0, 43 * Minute, Runtime));
		// an audio track change: the stream restarts on a new play session; the old one's Stopped is never sent ("failed")
		_journal.Clock.Advance(TimeSpan.FromSeconds(5));
		Server.State[ep.Id] = new JournalUserData(false, 2, Now);
		Send(events, State("opening", ep.Id, "ps-new"));
		await Server.WaitForUserDataReads(2);
		Send(events, Reported(ep.Id, "ps-old", "failed"));
		Send(events, State("playing", ep.Id, "ps-new", Runtime));
		Send(events, Play(43 * Minute, Runtime, Runtime));
		_journal.Clock.Advance(TimeSpan.FromMinutes(2));
		Server.State[ep.Id] = new JournalUserData(true, 2, Now.AddMinutes(-2));
		Send(events, Ended(ep.Id, "eof"), Idle(), Reported(ep.Id, "ps-new", "sent"));
		await logger.WhenIdle();

		var entry = Assert.Single(Store.GetEntries(TempJournal.Alice));
		Assert.False(entry.Rewatch);
		// the new session's report reached the server, which has it as played: nothing more is sent, the entry is on the server
		Assert.True(entry.OnServer);
		Assert.Empty(Store.GetPendingActions(TempJournal.Alice));
		Assert.Empty(Server.MarkedPlayed);
	}

	[Fact]
	public async Task Fix4_APlayASyncLoggedFirst_IsNotAutoLoggedAgain()
	{
		await _journal.NewSync().SyncAsync();
		var events = new Subject<BridgeEvent>();
		using var logger = new JournalAutoLogger(events, _journal.Service, NullLogger<JournalAutoLogger>.Instance);
		logger.Start();
		var ep = Server.AddEpisode("Show", 1, 1, runtimeTicks: Runtime);
		Server.State[ep.Id] = Unplayed;
		Send(events, Item(ep.Id, runTimeTicks: Runtime), State("opening", ep.Id, "ps-1"));
		await Server.WaitForUserDataReads(1);
		Send(events, State("playing", ep.Id, "ps-1", Runtime));
		Send(events, Play(0, Runtime, Runtime));
		// the server counted the play (its LastPlayedDate is when it started) and a sync gets to it before the report comes
		var startedOnTheServer = Now.AddSeconds(3);
		_journal.Clock.Advance(TimeSpan.FromMinutes(45));
		Server.State[ep.Id] = new JournalUserData(true, 1, startedOnTheServer);
		Server.Played.Add(ep with { UserData = Server.State[ep.Id] });
		Assert.Equal(1, (await _journal.NewSync().SyncAsync()).Added);

		Send(events, Ended(ep.Id, "eof"), Idle(), Reported(ep.Id, "ps-1", "sent"));
		await logger.WhenIdle();

		Assert.Equal(DiaryEntrySource.CatchUp, Assert.Single(Store.GetEntries(TempJournal.Alice)).Source);
	}

	// ------------------------------------------------------------ 5. Playback Reporting dates and the duplicate check

	[Fact]
	public async Task Fix5_MarkingPlayedElsewhere_DoesNotDuplicateAPlayDatedByPlaybackReporting()
	{
		var started = Now.AddDays(-20);
		var ep = PlayedEpisode("Show", 1, started);
		Server.History = new PlaybackHistory(true, [new PlaybackHistoryPlay(ep.Id, started, TimeSpan.FromMinutes(45))], null);
		Assert.Equal(1, (await _journal.NewSync().SyncAsync()).Added);

		_journal.Clock.Advance(TimeSpan.FromHours(3));
		// the whole series marked played in another client: LastPlayedDate is now, the plugin has no new play
		Server.Played.Clear();
		Server.Played.Add(ep with { UserData = new JournalUserData(true, 2, Now) });
		var second = await _journal.NewSync().SyncAsync();

		Assert.Equal(0, second.Added);
		Assert.Equal(started.AddMinutes(45), Assert.Single(Store.GetEntries(TempJournal.Alice)).WatchedAt);
	}

	// ------------------------------------------------------------ 6. an edited date does not bring the play back

	[Fact]
	public async Task Fix6_AnEntryWhoseDateTheUserMoved_IsNotAddedAgainByACatchUp()
	{
		await _journal.NewSync().SyncAsync();
		_journal.Clock.Advance(TimeSpan.FromHours(2));
		var ep = PlayedEpisode("Show", 2, Now.AddMinutes(-50));
		var moved = Store.AddEntry(TempJournal.Alice, new DiaryEntry { ItemId = ep.Id, WatchedAt = Now, DateSource = DiaryDateSource.Session, Source = DiaryEntrySource.AutoLog, PlaySessionId = "ps-9" })!;
		var cleared = PlayedEpisode("Show", 3, Now.AddMinutes(-40));
		var clearedEntry = Store.AddEntry(TempJournal.Alice, new DiaryEntry { ItemId = cleared.Id, WatchedAt = Now, DateSource = DiaryDateSource.Session, Source = DiaryEntrySource.AutoLog, PlaySessionId = "ps-10" })!;
		// "I watched it on Friday", and "I don't know when"
		Store.UpdateEntry(TempJournal.Alice, moved.Id, new DiaryEntryEdit(Now.AddDays(-2), false, null, false));
		Store.UpdateEntry(TempJournal.Alice, clearedEntry.Id, new DiaryEntryEdit(null, false, null, false));

		var result = await _journal.NewSync().SyncAsync();

		Assert.Equal(0, result.Added);
		Assert.Equal(2, Store.GetEntries(TempJournal.Alice).Count);
		Assert.Equal(Now.AddDays(-2), Store.GetEntry(TempJournal.Alice, moved.Id)!.WatchedAt);
	}

	[Fact]
	public void Fix6_ASchema1File_IsMigrated_WithItsDatesAsTheLoggedDates()
	{
		var item = Guid.NewGuid();
		var watched = Now.AddDays(-3);
		Store.AddEntry(TempJournal.Alice, new DiaryEntry { ItemId = item, WatchedAt = watched, DateSource = DiaryDateSource.LastPlayed, Source = DiaryEntrySource.Backfill });
		Store.Dispose();
		SqliteConnection.ClearAllPools();
		using (var connection = new SqliteConnection($"Data Source={Store.Path};Pooling=False"))
		{
			// back to what schema 1 wrote
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "DROP INDEX entries_item_logged; ALTER TABLE entries DROP COLUMN logged_at; PRAGMA user_version = 1;";
			command.ExecuteNonQuery();
		}

		using var migrated = new JournalStore(Store.Path);

		Assert.Equal(2, migrated.GetSchemaVersion());
		Assert.True(migrated.HasEntryFor(TempJournal.Alice, item, watched, includeDeleted: true));
		Assert.False(migrated.HasEntryFor(TempJournal.Alice, item, watched.AddTicks(1), includeDeleted: true));
	}

	// ------------------------------------------------------------ 7. damage behind a valid header

	private void FillWithEntries(JournalStore store, int count = 400)
	{
		for (var i = 0; i < count; i++)
		{
			store.AddEntry(TempJournal.Alice, new DiaryEntry { ItemId = Guid.NewGuid(), WatchedAt = Now, DateSource = DiaryDateSource.Session, Source = DiaryEntrySource.AutoLog, Review = new string('x', 200) });
		}
	}

	/// <summary>Overwrites pages 4-12 (keeping the header); <paramref name="bumpChangeCounter"/> tells an open connection the file changed.</summary>
	private static void DamagePages(string path, bool bumpChangeCounter)
	{
		const int pageSize = 4096;
		using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
		var garbage = Enumerable.Repeat((byte)0xAB, pageSize * 9).ToArray();
		file.Position = pageSize * 3;
		file.Write(garbage);
		if (bumpChangeCounter)
		{
			var counter = new byte[4];
			file.Position = 24;
			file.ReadExactly(counter);
			counter[3]++;
			file.Position = 24;
			file.Write(counter);
		}
	}

	[Fact]
	public void Fix7_AFileDamagedBehindAValidHeader_IsKeptAside_AndANewJournalStarts()
	{
		FillWithEntries(Store);
		Store.Dispose();
		SqliteConnection.ClearAllPools();
		DamagePages(Store.Path, bumpChangeCounter: false);

		using var store = new JournalStore(Store.Path);

		Assert.Empty(store.GetEntries(TempJournal.Alice));
		Assert.Equal(2, store.GetSchemaVersion());
		Assert.Single(Directory.GetFiles(_journal.Folder, "journal.db.corrupt-*"));
	}

	[Fact]
	public void Fix7_DamageFoundWhileOpen_IsAJournalError_AndTheNextUseStartsANewJournal()
	{
		FillWithEntries(Store);
		DamagePages(Store.Path, bumpChangeCounter: true);

		var error = Assert.Throws<JournalUnavailableException>(() => Store.GetEntries(TempJournal.Alice));
		Assert.Contains("damaged", error.Message);

		Assert.Empty(Store.GetEntries(TempJournal.Alice));
		Assert.Single(Directory.GetFiles(_journal.Folder, "journal.db.corrupt-*"));
	}

	[Fact]
	public async Task Fix7_TheDiary_ShowsTheDamage_InsteadOfGoingBlank()
	{
		FillWithEntries(Store);
		DamagePages(Store.Path, bumpChangeCounter: true);
		var vm = new DiaryViewModel(_journal.Service, _journal.NewSync(), Mock.Of<INavigationServiceCore>(), NullLogger<DiaryViewModel>.Instance);

		await vm.OnNavigatedTo(null!);

		Assert.True(vm.HasError);
		Assert.Contains("damaged", vm.ErrorText);

		// the next look finds a new, empty journal
		await vm.LoadAsync();
		Assert.False(vm.HasError);
		Assert.True(vm.IsEmpty);
	}

	// ------------------------------------------------------------ 8. Playback Reporting's server-local times

	[Fact]
	public async Task Fix8_PlaysFromAServerInAnotherZone_AreMovedByWhatJellyfinsDatesSay()
	{
		// read in this PC's zone, the plugin's times are five hours early: Jellyfin's own (UTC) dates show it
		var shift = TimeSpan.FromHours(5);
		var plays = new List<PlaybackHistoryPlay>();
		for (var i = 1; i <= 4; i++)
		{
			var lastPlayed = Now.AddDays(-i);
			var ep = PlayedEpisode("Show", i, lastPlayed);
			plays.Add(new PlaybackHistoryPlay(ep.Id, lastPlayed - shift, TimeSpan.FromMinutes(44)));
		}

		Server.History = new PlaybackHistory(true, plays, null);

		await _journal.NewSync().SyncAsync();

		var entries = Store.GetEntries(TempJournal.Alice);
		Assert.Equal(4, entries.Count);
		Assert.All(entries, x => Assert.Equal(DiaryDateSource.PlaybackReporting, x.DateSource));
		foreach (var play in plays)
		{
			Assert.Equal(play.EndedAt + shift, entries.Single(x => x.ItemId == play.ItemId).WatchedAt);
		}
	}
}
