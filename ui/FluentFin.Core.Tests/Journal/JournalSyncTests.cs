using FluentFin.Core.Journal;

namespace FluentFin.Core.Tests.Journal;

public sealed class JournalSyncTests : IDisposable
{
	private readonly TempJournal _journal = new();

	public void Dispose() => _journal.Dispose();

	private FakeJournalServer Server => _journal.Server;

	private DateTimeOffset Now => _journal.Clock.Now;

	/// <summary>An episode Jellyfin has as played at <paramref name="lastPlayed"/>, added to the played list (newest first is the test's job).</summary>
	private JournalEpisode Played(string series, int episode, DateTimeOffset? lastPlayed, int playCount = 1, bool played = true)
	{
		var item = Server.AddEpisode(series, 1, episode) with { UserData = new JournalUserData(played, playCount, lastPlayed) };
		Server.Items[item.Id] = item;
		Server.Played.Add(item);
		return item;
	}

	private void SortPlayed() => Server.Played.Sort((a, b) => Nullable.Compare(b.UserData?.LastPlayedDate, a.UserData?.LastPlayedDate));

	private IReadOnlyList<DiaryEntry> Entries() => _journal.Store.GetEntries(TempJournal.Alice);

	[Fact]
	public async Task TheFirstSync_LogsEveryPlayedEpisodeOnce_AtItsLastPlayedDate_AsApproximate()
	{
		var a = Played("Severance", 1, Now.AddDays(-3), playCount: 4);
		var b = Played("Severance", 2, Now.AddDays(-2));
		var undated = Played("Andor", 1, null);
		SortPlayed();

		var result = await _journal.NewSync().SyncAsync();

		Assert.True(result.Completed);
		Assert.True(result.Backfill);
		Assert.Equal(3, result.Added);
		var entries = Entries();
		Assert.Equal([b.Id, a.Id, undated.Id], entries.Select(x => x.ItemId));
		Assert.All(entries, x => Assert.Equal(DiaryEntrySource.Backfill, x.Source));
		Assert.All(entries, x => Assert.True(x.IsDateApproximate));
		Assert.All(entries, x => Assert.False(x.Rewatch));
		Assert.Equal(Now.AddDays(-3), entries.Single(x => x.ItemId == a.Id).WatchedAt);
		Assert.Equal(DiaryDateSource.LastPlayed, entries.Single(x => x.ItemId == a.Id).DateSource);
		Assert.Equal((null, DiaryDateSource.Unknown), (entries.Single(x => x.ItemId == undated.Id).WatchedAt, entries.Single(x => x.ItemId == undated.Id).DateSource));
		Assert.Equal(("Severance", 1, 1, "still-1"), (entries.Single(x => x.ItemId == a.Id).SeriesName, entries.Single(x => x.ItemId == a.Id).SeasonNumber!.Value, entries.Single(x => x.ItemId == a.Id).EpisodeNumber!.Value, entries.Single(x => x.ItemId == a.Id).ImageTag));

		var state = _journal.Store.GetSyncState(TempJournal.Alice);
		Assert.Equal(Now, state.LastSyncAt);
		Assert.Equal(Now, state.BackfillDoneAt);
		Assert.False(state.PlaybackReportingAvailable);
		Assert.Null(state.LastError);

		// a second sync adds nothing
		var again = await _journal.NewSync().SyncAsync();
		Assert.Equal((true, false, 0), (again.Completed, again.Backfill, again.Added));
		Assert.Equal(3, Entries().Count);
	}

	[Fact]
	public async Task ACatchUp_LogsOnlyPlaysSinceTheLastSync_FromOtherClients()
	{
		var old = Played("Severance", 1, Now.AddDays(-30));
		await _journal.NewSync().SyncAsync();

		_journal.Clock.Advance(TimeSpan.FromDays(3));
		// watched on the TV meanwhile: a new episode, and the old one again
		var fresh = Played("Severance", 2, Now.AddDays(-1));
		Server.Played.Remove(old);
		var oldAgain = old with { UserData = new JournalUserData(true, 2, Now.AddHours(-5)) };
		Server.Played.Add(oldAgain);
		// played long ago (before the last sync), sorted after it: never reached
		var ancient = Played("Andor", 1, Now.AddDays(-400));
		SortPlayed();

		var result = await _journal.NewSync().SyncAsync();

		Assert.Equal((true, false, 2), (result.Completed, result.Backfill, result.Added));
		var entries = Entries();
		Assert.Equal(3, entries.Count);
		var catchUp = entries.Where(x => x.Source == DiaryEntrySource.CatchUp).ToList();
		Assert.Equal([oldAgain.Id, fresh.Id], catchUp.Select(x => x.ItemId));
		Assert.True(catchUp.Single(x => x.ItemId == old.Id).Rewatch);
		Assert.False(catchUp.Single(x => x.ItemId == fresh.Id).Rewatch);
		Assert.DoesNotContain(entries, x => x.ItemId == ancient.Id);
	}

	[Fact]
	public async Task ACatchUp_SkipsWhatTheAutoLoggerAlreadyLogged()
	{
		await _journal.NewSync().SyncAsync();
		_journal.Clock.Advance(TimeSpan.FromHours(2));

		// the play started (Jellyfin's LastPlayedDate) 50 minutes before it ended here (the entry's date)
		var episode = Played("Severance", 3, Now.AddMinutes(-50));
		_journal.Store.AddEntry(TempJournal.Alice, new DiaryEntry { ItemId = episode.Id, WatchedAt = Now, DateSource = DiaryDateSource.Session, Source = DiaryEntrySource.AutoLog, PlaySessionId = "ps-1" });

		var result = await _journal.NewSync().SyncAsync();

		Assert.Equal(0, result.Added);
		Assert.Single(Entries());
	}

	[Fact]
	public async Task DeletedEntries_AreNotBroughtBack()
	{
		var episode = Played("Severance", 1, Now.AddDays(-2));
		await _journal.NewSync().SyncAsync();
		var entry = Assert.Single(Entries());
		_journal.Store.DeleteEntry(TempJournal.Alice, entry.Id);

		// within the overlap the catch-up reads it again
		_journal.Clock.Advance(TimeSpan.FromHours(1));
		await _journal.NewSync().SyncAsync();

		Assert.Empty(Entries());
		Assert.Equal(episode.Id, Assert.Single(_journal.Store.GetEntries(TempJournal.Alice, includeDeleted: true)).ItemId);
	}

	[Fact]
	public async Task PlaybackReportingHistory_GivesExactDates()
	{
		var episode = Played("Severance", 1, Now.AddDays(-2));
		var other = Played("Severance", 2, Now.AddDays(-1));
		SortPlayed();
		var started = Now.AddDays(-2).AddMinutes(1);
		Server.History = new PlaybackHistory(true,
		[
			new PlaybackHistoryPlay(episode.Id, Now.AddDays(-90), TimeSpan.FromMinutes(44)),
			new PlaybackHistoryPlay(episode.Id, started, TimeSpan.FromMinutes(44)),
		], null);

		var result = await _journal.NewSync().SyncAsync();

		Assert.True(result.ExactDates);
		var exact = Entries().Single(x => x.ItemId == episode.Id);
		Assert.Equal(started.AddMinutes(44), exact.WatchedAt);
		Assert.Equal(DiaryDateSource.PlaybackReporting, exact.DateSource);
		Assert.False(exact.IsDateApproximate);
		Assert.Equal(DiaryDateSource.LastPlayed, Entries().Single(x => x.ItemId == other.Id).DateSource);
		Assert.True(_journal.Store.GetSyncState(TempJournal.Alice).PlaybackReportingAvailable);
	}

	[Fact]
	public async Task UnplayedItemsAndNonEpisodes_AreSkipped()
	{
		Played("Severance", 1, Now.AddDays(-1), played: false);
		var movie = Server.AddEpisode("x", 1, 1) with { Type = "Movie", UserData = new JournalUserData(true, 1, Now) };
		Server.Played.Add(movie);

		var result = await _journal.NewSync().SyncAsync();

		Assert.Equal(0, result.Added);
	}

	[Fact]
	public async Task ManyEpisodes_ArePagedThrough()
	{
		for (var i = 0; i < JournalSync.PageSize * 2 + 15; i++)
		{
			Played("Long Show", i + 1, Now.AddMinutes(-i));
		}

		var result = await _journal.NewSync().SyncAsync();

		Assert.Equal(JournalSync.PageSize * 2 + 15, result.Added);
		Assert.Equal(3, Server.Calls.Count(x => x.StartsWith("played", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task AFailedPage_KeepsTheLastSync_SoTheNextSyncCoversItAgain()
	{
		for (var i = 0; i < JournalSync.PageSize + 5; i++)
		{
			Played("Long Show", i + 1, Now.AddMinutes(-i));
		}

		Server.FailPagesFrom = JournalSync.PageSize;

		var failed = await _journal.NewSync().SyncAsync();

		Assert.False(failed.Completed);
		Assert.Equal(JournalSync.PageSize, failed.Added);
		var state = _journal.Store.GetSyncState(TempJournal.Alice);
		Assert.Null(state.LastSyncAt);
		Assert.Equal("Jellyfin did not answer", state.LastError);

		Server.FailPagesFrom = null;
		var retried = await _journal.NewSync().SyncAsync();

		Assert.True(retried.Completed);
		Assert.Equal(5, retried.Added);
		Assert.Equal(JournalSync.PageSize + 5, Entries().Count);
		Assert.Null(_journal.Store.GetSyncState(TempJournal.Alice).LastError);
	}

	[Fact]
	public async Task EachJournal_SyncsItsOwnUser()
	{
		Played("Severance", 1, Now.AddDays(-1));
		await _journal.NewSync().SyncAsync();

		_journal.Session.Current = TempJournal.Bob;
		var bob = await _journal.NewSync().SyncAsync();

		Assert.True(bob.Backfill);
		Assert.Single(_journal.Store.GetEntries(TempJournal.Bob));
		Assert.Single(_journal.Store.GetEntries(TempJournal.Alice));
	}

	[Fact]
	public async Task SignedOut_NothingSyncs()
	{
		_journal.Session.Current = null;

		var result = await _journal.NewSync().SyncAsync();

		Assert.False(result.Completed);
		Assert.Empty(Server.Calls);
	}

	[Fact]
	public async Task ASync_SendsTheWritesStillOwed()
	{
		var episode = Played("Severance", 1, null, played: false);
		var entry = _journal.Store.AddEntry(TempJournal.Alice, new DiaryEntry { ItemId = episode.Id, WatchedAt = Now.AddHours(-1), DateSource = DiaryDateSource.Session, Source = DiaryEntrySource.AutoLog, OnServer = false })!;
		_journal.Store.AddPendingAction(TempJournal.Alice, episode.Id, PendingJournalAction.MarkPlayed, entry.WatchedAt!.Value, entry.Id);

		await _journal.NewSync().SyncAsync();

		Assert.Equal([(episode.Id, entry.WatchedAt.Value)], Server.MarkedPlayed);
		Assert.Empty(_journal.Store.GetPendingActions(TempJournal.Alice));
		Assert.True(_journal.Store.GetEntry(TempJournal.Alice, entry.Id)!.OnServer);
	}

	[Fact]
	public async Task AWriteTheServerAlreadyHas_IsNotSentAgain_AndRefusedOnesAreRetriedThenDropped()
	{
		var known = Server.AddEpisode("Severance", 1, 1);
		var at = Now.AddHours(-1);
		Server.State[known.Id] = new JournalUserData(true, 1, at.AddMinutes(-45));
		_journal.Store.AddPendingAction(TempJournal.Alice, known.Id, PendingJournalAction.MarkPlayed, at, null);
		var refused = Server.AddEpisode("Severance", 1, 2);
		_journal.Store.AddPendingAction(TempJournal.Alice, refused.Id, PendingJournalAction.MarkPlayed, at, null);
		Server.RefuseMarkPlayed = true;

		await _journal.Service.DrainPendingAsync(TempJournal.Alice);

		Assert.Empty(Server.MarkedPlayed);
		var left = Assert.Single(_journal.Store.GetPendingActions(TempJournal.Alice));
		Assert.Equal((refused.Id, 1), (left.ItemId, left.Attempts));

		for (var i = 0; i < JournalService.MaxAttempts; i++)
		{
			await _journal.Service.DrainPendingAsync(TempJournal.Alice);
		}

		Assert.Empty(_journal.Store.GetPendingActions(TempJournal.Alice));
	}
}
