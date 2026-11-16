using System.Reactive.Subjects;
using FluentFin.Core.Journal;
using FluentFin.Core.Mpv;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using static FluentFin.Core.Tests.Journal.Events;

namespace FluentFin.Core.Tests.Journal;

/// <summary>The auto-logger on the backend's event stream, with a real store (temp folder) and a fake Jellyfin.</summary>
public sealed class JournalAutoLoggerTests : IDisposable
{
	private static readonly long Runtime = 45 * Minute;
	private static readonly JournalUserData Unplayed = new(false, 0, null);

	private readonly TempJournal _journal = new();
	private readonly Subject<BridgeEvent> _events = new();
	private readonly JournalAutoLogger _logger;
	private readonly JournalEpisode _episode;

	public JournalAutoLoggerTests()
	{
		_logger = new JournalAutoLogger(_events, _journal.Service, NullLogger<JournalAutoLogger>.Instance);
		_logger.Start();
		_episode = _journal.Server.AddEpisode("Severance", 2, 5, "Trojan's Horse", Runtime);
	}

	public void Dispose()
	{
		_logger.Dispose();
		_journal.Dispose();
	}

	private FakeJournalServer Server => _journal.Server;

	private void Send(params IEnumerable<BridgeEvent> events)
	{
		foreach (var e in events)
		{
			_events.OnNext(e);
		}
	}

	/// <summary>
	/// One play session as the backend reports it: the server's user data is <paramref name="before"/> when it opens and
	/// <paramref name="after"/> once its Stopped report was handled. Positions from <paramref name="from"/> to <paramref name="to"/>.
	/// </summary>
	private async Task PlayAsync(string session, JournalUserData before, JournalUserData after, string outcome = "sent", string ended = "eof",
		long from = 0, long? to = null, Guid? item = null, string type = "Episode", bool waitForBaseline = true)
	{
		var id = item ?? _episode.Id;
		// a play opens after the one before it ended (a session ending as another opens is the same play restarted)
		_journal.Clock.Advance(TimeSpan.FromSeconds(1));
		Server.State[id] = before;
		var reads = Server.UserDataReads;
		Send(Item(id, type, "Severance", 2, 5, Runtime, "Trojan's Horse"), State("opening", id, session));
		if (waitForBaseline)
		{
			await Server.WaitForUserDataReads(reads + 1);
		}

		Send(State("playing", id, session, Runtime));
		Send(Play(from, to ?? Runtime, Runtime));
		_journal.Clock.Advance(TimeSpan.FromMinutes(45));
		Server.State[id] = after;
		Send(Ended(id, ended), Idle(), Reported(id, session, outcome));
		await _logger.WhenIdle();
	}

	private IReadOnlyList<DiaryEntry> Entries(JournalProfile? profile = null) => _journal.Store.GetEntries(profile ?? TempJournal.Alice);

	[Fact]
	public async Task AFinishedEpisode_IsLoggedOnce_AtItsEnd()
	{
		var changed = new List<JournalProfile>();
		using var _ = _journal.Service.Changed.Subscribe(changed.Add);

		await PlayAsync("ps-1", Unplayed, new JournalUserData(true, 1, _journal.Clock.Now));

		var entry = Assert.Single(Entries());
		Assert.Equal(_episode.Id, entry.ItemId);
		Assert.Equal(("Severance", 2, 5, "Trojan's Horse"), (entry.SeriesName, entry.SeasonNumber!.Value, entry.EpisodeNumber!.Value, entry.Title));
		Assert.Equal((_episode.SeriesId, _episode.SeasonId, "still-5", "poster"), (entry.SeriesId, entry.SeasonId, entry.ImageTag, entry.SeriesImageTag));
		Assert.Equal(_journal.Clock.Now, entry.WatchedAt);
		Assert.Equal(DiaryDateSource.Session, entry.DateSource);
		Assert.Equal(DiaryEntrySource.AutoLog, entry.Source);
		Assert.Equal("ps-1", entry.PlaySessionId);
		Assert.True(entry.OnServer);
		Assert.False(entry.Rewatch);
		Assert.Equal([TempJournal.Alice], changed);
		Assert.Empty(Server.MarkedPlayed);

		// a new backend process that reports the same session again (a server reusing ids) logs nothing more
		Send(Hello());
		await PlayAsync("ps-1", new JournalUserData(true, 1, null), new JournalUserData(true, 2, null));
		Assert.Single(Entries());
	}

	[Fact]
	public async Task AnEpisodeStoppedHalfway_IsNotLogged()
	{
		await PlayAsync("ps-1", Unplayed, new JournalUserData(false, 1, _journal.Clock.Now), ended: "stopped", to: 20 * Minute);

		Assert.Empty(Entries());
	}

	[Fact]
	public async Task OpeningAndJumpingToTheEnd_IsNotLogged_ThoughTheServerCountsIt()
	{
		Server.State[_episode.Id] = Unplayed;
		Send(State("opening", _episode.Id, "ps-1"));
		await Server.WaitForUserDataReads(1);
		Send(State("playing", _episode.Id, "ps-1", Runtime), Position(0, Runtime), Position(Minute, Runtime));
		Send(Play(43 * Minute, Runtime, Runtime));
		Server.State[_episode.Id] = new JournalUserData(true, 1, null);
		Send(Ended(_episode.Id, "eof"), Reported(_episode.Id, "ps-1", "sent"));
		await _logger.WhenIdle();

		Assert.Empty(Entries());
	}

	[Fact]
	public async Task AFailedReport_AtTheEnd_IsLoggedLocally_AndTheServerIsToldOnceItAnswers()
	{
		await PlayAsync("ps-1", Unplayed, Unplayed, outcome: "failed");

		var entry = Assert.Single(Entries());
		Assert.False(entry.OnServer);
		var pending = Assert.Single(_journal.Store.GetPendingActions(TempJournal.Alice));
		Assert.Equal((_episode.Id, PendingJournalAction.MarkPlayed, entry.WatchedAt!.Value, (long?)entry.Id), (pending.ItemId, pending.Action, pending.At, pending.EntryId));
		Assert.Empty(Server.MarkedPlayed);

		// the next play whose report the server takes: what is owed goes now, at the first episode's date
		var next = Server.AddEpisode("Severance", 2, 6, runtimeTicks: Runtime);
		await PlayAsync("ps-2", Unplayed, new JournalUserData(true, 1, null), item: next.Id);

		Assert.Equal([(_episode.Id, entry.WatchedAt.Value)], Server.MarkedPlayed);
		Assert.Empty(_journal.Store.GetPendingActions(TempJournal.Alice));
		Assert.True(_journal.Store.GetEntry(TempJournal.Alice, entry.Id)!.OnServer);
		Assert.Equal(2, Entries().Count);
	}

	[Fact]
	public async Task AFailedReport_InTheMiddle_LogsAndQueuesNothing()
	{
		await PlayAsync("ps-1", Unplayed, Unplayed, outcome: "failed", ended: "stopped", to: 25 * Minute);

		Assert.Empty(Entries());
		Assert.Empty(_journal.Store.GetPendingActions(TempJournal.Alice));
	}

	[Fact]
	public async Task ASessionThatNeverReachedTheServer_IsNotLogged()
	{
		await PlayAsync("ps-1", Unplayed, new JournalUserData(true, 1, null), outcome: "none");

		Assert.Empty(Entries());
	}

	[Fact]
	public async Task AMovie_IsNotLogged()
	{
		var movie = Guid.NewGuid();
		Server.Items[movie] = new JournalEpisode(movie, "Movie", "Heat", null, null, null, null, null, Runtime, null, null, null);

		await PlayAsync("ps-1", Unplayed, new JournalUserData(true, 1, null), item: movie, type: "Movie");

		Assert.Empty(Entries());
	}

	[Fact]
	public async Task WithAutoLogOff_NothingIsLogged_AndTheServerIsNotAsked()
	{
		_journal.Store.SaveSettings(new JournalSettings { AutoLog = false });

		await PlayAsync("ps-1", Unplayed, new JournalUserData(true, 1, null), waitForBaseline: false);
		await Task.Delay(50);

		Assert.Empty(Entries());
		Assert.DoesNotContain(Server.Calls, x => x.StartsWith("userdata", StringComparison.Ordinal) || x.StartsWith("item", StringComparison.Ordinal));
	}

	[Fact]
	public async Task AfterASwitchToAnotherUser_ThePlayIsLoggedForTheOneWhoWatched_WithoutAskingTheServer()
	{
		Server.State[_episode.Id] = Unplayed;
		Send(Item(_episode.Id, "Episode", "Severance", 2, 5, Runtime), State("opening", _episode.Id, "ps-1"));
		await Server.WaitForUserDataReads(1);
		Send(State("playing", _episode.Id, "ps-1", Runtime));
		Send(Play(0, Runtime, Runtime));
		_journal.Session.Current = TempJournal.Bob;
		var callsBefore = Server.Calls.Count;
		Send(Ended(_episode.Id, "eof"), Reported(_episode.Id, "ps-1", "sent"));
		await _logger.WhenIdle();

		var entry = Assert.Single(Entries(TempJournal.Alice));
		Assert.Empty(Entries(TempJournal.Bob));
		Assert.True(entry.OnServer);
		// what the backend's item event said, since the server was not asked as bob
		Assert.Equal(("Severance", 2, 5), (entry.SeriesName, entry.SeasonNumber!.Value, entry.EpisodeNumber!.Value));
		Assert.Equal(callsBefore, Server.Calls.Count);
	}

	[Fact]
	public async Task AnEpisodeAlreadyInTheDiary_IsARewatch()
	{
		await PlayAsync("ps-1", Unplayed, new JournalUserData(true, 1, null));
		await PlayAsync("ps-2", new JournalUserData(false, 1, null), new JournalUserData(true, 2, null));

		var entries = Entries();
		Assert.Equal(2, entries.Count);
		Assert.True(entries.Single(x => x.PlaySessionId == "ps-2").Rewatch);
		Assert.False(entries.Single(x => x.PlaySessionId == "ps-1").Rewatch);
	}

	[Fact]
	public async Task ARewatchOfSomethingPlayedElsewhere_IsARewatch_AndAnAbandonedOneIsNotLogged()
	{
		var before = new JournalUserData(true, 3, _journal.Clock.Now.AddYears(-1));

		await PlayAsync("ps-1", before, before with { PlayCount = 4 }, ended: "stopped", to: 18 * Minute);
		Assert.Empty(Entries());

		await PlayAsync("ps-2", before with { PlayCount = 4 }, before with { PlayCount = 5 });
		Assert.True(Assert.Single(Entries()).Rewatch);
	}

	[Fact]
	public async Task WhenTheServerDoesNotAnswer_TheSessionsEndDecides()
	{
		Server.Down = true;

		await PlayAsync("ps-1", Unplayed, Unplayed);

		var entry = Assert.Single(Entries());
		Assert.Equal("Trojan's Horse", entry.Title);
		Assert.Null(entry.SeriesId);
	}

	[Fact]
	public async Task AJournalThatCannotBeOpened_LogsNothing_AndNeverBreaksTheEventStream()
	{
		var folder = Path.Combine(_journal.Folder, "newer");
		Directory.CreateDirectory(folder);
		var path = Path.Combine(folder, JournalStore.FileName);
		using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
		{
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "PRAGMA user_version = 99;";
			command.ExecuteNonQuery();
		}

		using var store = new JournalStore(path);
		var service = new JournalService(store, Server, _journal.Session, null, _journal.Clock);
		var events = new Subject<BridgeEvent>();
		using var logger = new JournalAutoLogger(events, service, NullLogger<JournalAutoLogger>.Instance);
		logger.Start();

		var received = 0;
		using var counter = events.Subscribe(_ => received++);
		events.OnNext(State("playing", _episode.Id, "ps-1", Runtime));
		foreach (var e in Play(0, Runtime, Runtime))
		{
			events.OnNext(e);
		}

		events.OnNext(Ended(_episode.Id, "eof"));
		events.OnNext(Reported(_episode.Id, "ps-1", "sent"));
		await logger.WhenIdle();

		Assert.True(received > 500);
		Assert.Throws<JournalUnavailableException>(() => store.GetEntries(TempJournal.Alice));
	}
}
