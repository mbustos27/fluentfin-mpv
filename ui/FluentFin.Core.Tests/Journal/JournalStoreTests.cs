using FluentFin.Core.Journal;
using Microsoft.Data.Sqlite;

namespace FluentFin.Core.Tests.Journal;

public sealed class JournalStoreTests : IDisposable
{
	private readonly TempJournal _journal = new();

	private JournalStore Store => _journal.Store;

	public void Dispose() => _journal.Dispose();

	private static DiaryEntry Entry(Guid? item = null, DateTimeOffset? watched = null, string? playSession = null, DiaryDateSource dateSource = DiaryDateSource.Session) => new()
	{
		ItemId = item ?? Guid.NewGuid(),
		SeriesId = Guid.NewGuid(),
		SeriesName = "Severance",
		SeasonNumber = 2,
		EpisodeNumber = 5,
		Title = "Trojan's Horse",
		WatchedAt = watched ?? new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero),
		DateSource = dateSource,
		Source = DiaryEntrySource.AutoLog,
		PlaySessionId = playSession,
	};

	[Fact]
	public void ANewFile_GetsTheLatestSchema_WithEveryTable()
	{
		Store.EnsureOpen();

		Assert.Equal(2, Store.GetSchemaVersion());
		Assert.Equal(JournalStore.SchemaVersion, Store.GetSchemaVersion());
		Assert.True(File.Exists(Path.Combine(_journal.Folder, "journal.db")));

		using var connection = new SqliteConnection($"Data Source={Store.Path};Pooling=False;Mode=ReadOnly");
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
		var tables = new List<string>();
		using (var reader = command.ExecuteReader())
		{
			while (reader.Read())
			{
				tables.Add(reader.GetString(0));
			}
		}

		Assert.Equal(["entries", "pending_actions", "profiles", "ratings", "settings", "statuses", "sync_state"], tables);
	}

	[Fact]
	public void Reopening_KeepsTheData_AndMigratesNothingTwice()
	{
		var added = Store.AddEntry(TempJournal.Alice, Entry())!;
		Store.Dispose();

		using var again = new JournalStore(Store.Path);
		Assert.Equal(2, again.GetSchemaVersion());
		Assert.Equal(added.Id, Assert.Single(again.GetEntries(TempJournal.Alice)).Id);
	}

	[Fact]
	public void AFileFromANewerVersion_IsLeftAlone_AndTheJournalIsOff()
	{
		Directory.CreateDirectory(_journal.Folder);
		using (var connection = new SqliteConnection($"Data Source={Store.Path};Pooling=False"))
		{
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "CREATE TABLE future (x INTEGER); PRAGMA user_version = 99;";
			command.ExecuteNonQuery();
		}

		var before = File.ReadAllBytes(Store.Path);

		var error = Assert.Throws<JournalUnavailableException>(Store.EnsureOpen);
		Assert.Contains("newer version", error.Message);
		// remembered: no second attempt touches it either
		Assert.Throws<JournalUnavailableException>(() => Store.GetEntries(TempJournal.Alice));
		Assert.Equal(before, File.ReadAllBytes(Store.Path));
	}

	[Fact]
	public void AFileThatIsNotADatabase_IsKeptAside_AndANewJournalStarts()
	{
		Directory.CreateDirectory(_journal.Folder);
		File.WriteAllText(Store.Path, "this is not a database, it is a text file that is long enough to have a header page");

		Store.EnsureOpen();

		Assert.Equal(2, Store.GetSchemaVersion());
		var aside = Assert.Single(Directory.GetFiles(_journal.Folder, "journal.db.corrupt-*"));
		Assert.StartsWith("this is not a database", File.ReadAllText(aside));
	}

	[Fact]
	public void Journals_AreSeparate_PerServerAndUser()
	{
		var item = Guid.NewGuid();
		Store.AddEntry(TempJournal.Alice, Entry(item));
		Store.SetRating(TempJournal.Alice, item, RatedItemKind.Episode, 8);

		Assert.Empty(Store.GetEntries(TempJournal.Bob));
		Assert.Empty(Store.GetEntries(TempJournal.AliceElsewhere));
		Assert.Null(Store.GetRating(TempJournal.Bob, item));
		Assert.Null(Store.GetRating(TempJournal.AliceElsewhere, item));
		Assert.False(Store.HasEntryFor(TempJournal.Bob, item));
		Assert.Single(Store.GetEntries(TempJournal.Alice));

		// the same server id in another case is the same journal
		var shouting = new JournalProfile("SERVER-A", TempJournal.Alice.UserId);
		Assert.Single(Store.GetEntries(shouting));
		// alice, bob, alice on the other server (a journal row is made when it is first used)
		Assert.Equal(3, Store.GetProfiles().Count);
	}

	[Fact]
	public void AnEntry_RoundTrips()
	{
		var entry = Entry(playSession: "ps-1") with
		{
			SeasonId = Guid.NewGuid(),
			RuntimeTicks = 123,
			ImageTag = "tag",
			SeriesImageTag = "poster",
			Rewatch = true,
			Review = "Great",
			Spoiler = true,
			OnServer = false,
			WatchedAt = new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.FromHours(-8)),
		};

		var added = Store.AddEntry(TempJournal.Alice, entry)!;
		var read = Assert.Single(Store.GetEntries(TempJournal.Alice));

		Assert.Equal(added, read);
		Assert.Equal(entry with { Id = read.Id, CreatedAt = read.CreatedAt, UpdatedAt = read.UpdatedAt, WatchedAt = read.WatchedAt }, read);
		Assert.Equal(entry.WatchedAt, read.WatchedAt);
		Assert.Equal(TimeSpan.Zero, read.WatchedAt!.Value.Offset);
		Assert.Equal(_journal.Clock.Now, read.CreatedAt);
	}

	[Fact]
	public void APlaySession_IsLoggedOnce_EvenAfterItsEntryWasDeleted()
	{
		var first = Store.AddEntry(TempJournal.Alice, Entry(playSession: "ps-1"));
		Assert.NotNull(first);
		Assert.True(Store.HasPlaySession(TempJournal.Alice, "ps-1"));

		Assert.Null(Store.AddEntry(TempJournal.Alice, Entry(playSession: "ps-1")));

		Store.DeleteEntry(TempJournal.Alice, first.Id);
		Assert.Null(Store.AddEntry(TempJournal.Alice, Entry(playSession: "ps-1")));

		// other journals and entries without a session are not affected
		Assert.NotNull(Store.AddEntry(TempJournal.Bob, Entry(playSession: "ps-1")));
		Assert.NotNull(Store.AddEntry(TempJournal.Alice, Entry()));
		Assert.NotNull(Store.AddEntry(TempJournal.Alice, Entry()));
	}

	[Fact]
	public void Deleting_KeepsATombstone_ThatOnlyCountsWhenAsked()
	{
		var item = Guid.NewGuid();
		var entry = Store.AddEntry(TempJournal.Alice, Entry(item))!;

		Assert.True(Store.DeleteEntry(TempJournal.Alice, entry.Id));
		Assert.False(Store.DeleteEntry(TempJournal.Alice, entry.Id));

		Assert.Empty(Store.GetEntries(TempJournal.Alice));
		Assert.Equal(_journal.Clock.Now, Store.GetEntry(TempJournal.Alice, entry.Id)!.DeletedAt);
		Assert.NotNull(Assert.Single(Store.GetEntries(TempJournal.Alice, includeDeleted: true)).DeletedAt);
		Assert.False(Store.HasEntryFor(TempJournal.Alice, item));
		Assert.True(Store.HasEntryFor(TempJournal.Alice, item, includeDeleted: true));
	}

	[Fact]
	public void HasEntryFor_WithADate_CountsOnlyEntriesFromThen()
	{
		var item = Guid.NewGuid();
		var at = new DateTimeOffset(2026, 11, 10, 20, 0, 0, TimeSpan.Zero);
		Store.AddEntry(TempJournal.Alice, Entry(item, at));
		var undated = Guid.NewGuid();
		Store.AddEntry(TempJournal.Alice, Entry(undated) with { WatchedAt = null, DateSource = DiaryDateSource.Unknown });

		Assert.True(Store.HasEntryFor(TempJournal.Alice, item, at));
		Assert.True(Store.HasEntryFor(TempJournal.Alice, item, at.AddHours(-1)));
		Assert.False(Store.HasEntryFor(TempJournal.Alice, item, at.AddTicks(1)));
		Assert.False(Store.HasEntryFor(TempJournal.Alice, undated, at.AddYears(-50)));
		Assert.True(Store.HasEntryFor(TempJournal.Alice, undated));
	}

	[Fact]
	public void Entries_AreNewestFirst_UndatedLast()
	{
		var old = Store.AddEntry(TempJournal.Alice, Entry(watched: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)))!;
		var undated = Store.AddEntry(TempJournal.Alice, Entry() with { WatchedAt = null, DateSource = DiaryDateSource.Unknown })!;
		var recent = Store.AddEntry(TempJournal.Alice, Entry(watched: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)))!;

		Assert.Equal([recent.Id, old.Id, undated.Id], Store.GetEntries(TempJournal.Alice).Select(x => x.Id));
	}

	[Fact]
	public void EditingTheDate_MakesItTheUsers_KeepingItLeavesTheSource()
	{
		var entry = Store.AddEntry(TempJournal.Alice, Entry(dateSource: DiaryDateSource.LastPlayed))!;

		Assert.True(Store.UpdateEntry(TempJournal.Alice, entry.Id, new DiaryEntryEdit(entry.WatchedAt!.Value.ToOffset(TimeSpan.FromHours(2)), true, "  ", false)));
		var same = Store.GetEntry(TempJournal.Alice, entry.Id)!;
		Assert.Equal(DiaryDateSource.LastPlayed, same.DateSource);
		Assert.True(same.IsDateApproximate);
		Assert.True(same.Rewatch);
		Assert.Null(same.Review);

		var moved = entry.WatchedAt.Value.AddDays(-2);
		Assert.True(Store.UpdateEntry(TempJournal.Alice, entry.Id, new DiaryEntryEdit(moved, false, "Rewatched with friends", true)));
		var edited = Store.GetEntry(TempJournal.Alice, entry.Id)!;
		Assert.Equal(moved, edited.WatchedAt);
		Assert.Equal(DiaryDateSource.Manual, edited.DateSource);
		Assert.False(edited.IsDateApproximate);
		Assert.Equal("Rewatched with friends", edited.Review);
		Assert.True(edited.Spoiler);

		// another journal's entry cannot be edited or deleted by id
		Assert.False(Store.UpdateEntry(TempJournal.Bob, entry.Id, new DiaryEntryEdit(null, false, null, false)));
		Assert.False(Store.DeleteEntry(TempJournal.Bob, entry.Id));
	}

	[Fact]
	public void Ratings_AreSetChangedAndCleared()
	{
		var series = Guid.NewGuid();
		Store.SetRating(TempJournal.Alice, series, RatedItemKind.Series, 7, series, "Severance");
		Assert.Equal(7, Store.GetRating(TempJournal.Alice, series)!.Value);

		Store.SetRating(TempJournal.Alice, series, RatedItemKind.Series, 10);
		var changed = Store.GetRating(TempJournal.Alice, series)!;
		Assert.Equal(10, changed.Value);
		Assert.Equal("Severance", changed.Name);
		Assert.Equal(RatedItemKind.Series, changed.Kind);

		Store.SetRating(TempJournal.Alice, series, RatedItemKind.Series, 0);
		Assert.Null(Store.GetRating(TempJournal.Alice, series));

		// out of range is "not rated", never stored
		Store.SetRating(TempJournal.Alice, series, RatedItemKind.Series, 11);
		Assert.Empty(Store.GetRatings(TempJournal.Alice));
	}

	[Fact]
	public void Settings_HaveTheDecidedDefaults_AndRoundTrip()
	{
		var defaults = Store.GetSettings();
		Assert.True(defaults.AutoLog);
		Assert.Equal(RatingPromptMode.Off, defaults.RatingPrompt);
		Assert.False(defaults.MirrorRatingToJellyfin);

		var changed = new JournalSettings { AutoLog = false, RatingPrompt = RatingPromptMode.FinalesOnly, MirrorRatingToJellyfin = true };
		Store.SaveSettings(changed);

		Assert.Equal(changed, Store.GetSettings());
	}

	[Fact]
	public void SyncState_AndPendingWrites_RoundTrip_PerJournal()
	{
		Assert.Equal(new JournalSyncState(), Store.GetSyncState(TempJournal.Alice));
		var state = new JournalSyncState
		{
			LastSyncAt = _journal.Clock.Now,
			BackfillDoneAt = _journal.Clock.Now.AddMinutes(1),
			PlaybackReportingAvailable = false,
			PlaybackReportingCheckedAt = _journal.Clock.Now,
			LastError = "Jellyfin did not answer",
		};
		Store.SaveSyncState(TempJournal.Alice, state);
		Assert.Equal(state, Store.GetSyncState(TempJournal.Alice));
		Assert.Equal(new JournalSyncState(), Store.GetSyncState(TempJournal.Bob));

		var item = Guid.NewGuid();
		var id = Store.AddPendingAction(TempJournal.Alice, item, PendingJournalAction.MarkPlayed, _journal.Clock.Now, 5);
		Assert.Empty(Store.GetPendingActions(TempJournal.Bob));
		Store.FailPendingAction(id, "refused");
		Assert.Equal(new PendingJournalAction(id, item, PendingJournalAction.MarkPlayed, _journal.Clock.Now, 5, 1), Assert.Single(Store.GetPendingActions(TempJournal.Alice)));
		Store.CompletePendingAction(id);
		Assert.Empty(Store.GetPendingActions(TempJournal.Alice));
	}
}
