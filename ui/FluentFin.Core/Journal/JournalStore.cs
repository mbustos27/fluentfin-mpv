using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Core.Journal;

/// <summary>A play a sync found, and from when an entry of the same item means it is already in the journal.</summary>
/// <param name="DuplicateFrom">An entry of the item first dated at or after this (deleted ones too) is this play; null: any entry (the backfill).</param>
public sealed record SyncCandidate(DiaryEntry Entry, DateTimeOffset? DuplicateFrom);

/// <summary>
/// The journal's SQLite file, <c>journal.db</c> in the app's data folder (docs/JOURNAL.md). One connection for the store's
/// life, every call under one lock: calls are small and the file is private to this process. Times are stored as fixed-width
/// UTC text (so they sort as text), ids as 32 lowercase hex digits. Every table is keyed by the journal (server + user).
/// <para>
/// Opened on first use and checked (<c>PRAGMA quick_check</c>): a damaged file, or one that is not a database, is moved
/// aside and a new one started; a file of a newer schema is left alone and the journal is off. Every failure of SQLite
/// reaches callers as <see cref="JournalUnavailableException"/>; damage found later closes the file, so the next call
/// checks it again (and starts a new journal if it is damaged).
/// </para>
/// </summary>
public sealed class JournalStore : IDisposable
{
	public const string FileName = "journal.db";

	/// <summary>The schema this build writes. A file with a higher <c>user_version</c> came from a newer build: not touched.</summary>
	public static int SchemaVersion => Migrations[^1].Version;

	/// <summary>
	/// An auto-logged play is the one a sync already logged when that entry is dated (by the server's clock: the start of
	/// the play) no earlier than this before the session opened (by this PC's clock).
	/// </summary>
	public static readonly TimeSpan SyncedPlaySlack = TimeSpan.FromMinutes(10);

	/// <summary>
	/// Each step brings the file from the version before it to its own; applied in order, each in a transaction with its
	/// <c>user_version</c>. Never edit a step that has shipped: add one.
	/// </summary>
	internal static readonly (int Version, string Sql)[] Migrations =
	[
		(1, """
			CREATE TABLE profiles (
				id INTEGER PRIMARY KEY,
				server_id TEXT NOT NULL,
				user_id TEXT NOT NULL,
				server_name TEXT,
				user_name TEXT,
				created_at TEXT NOT NULL,
				UNIQUE (server_id, user_id)
			);
			CREATE TABLE entries (
				id INTEGER PRIMARY KEY,
				profile_id INTEGER NOT NULL REFERENCES profiles(id),
				item_id TEXT NOT NULL,
				series_id TEXT,
				season_id TEXT,
				series_name TEXT,
				season_number INTEGER,
				episode_number INTEGER,
				title TEXT,
				runtime_ticks INTEGER,
				image_tag TEXT,
				series_image_tag TEXT,
				watched_at TEXT,
				date_source TEXT NOT NULL,
				source TEXT NOT NULL,
				rewatch INTEGER NOT NULL DEFAULT 0,
				review TEXT,
				spoiler INTEGER NOT NULL DEFAULT 0,
				play_session_id TEXT,
				on_server INTEGER NOT NULL DEFAULT 1,
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				deleted_at TEXT
			);
			CREATE UNIQUE INDEX entries_play_session ON entries (profile_id, play_session_id) WHERE play_session_id IS NOT NULL;
			CREATE INDEX entries_watched ON entries (profile_id, watched_at);
			CREATE INDEX entries_item ON entries (profile_id, item_id);
			CREATE TABLE ratings (
				profile_id INTEGER NOT NULL REFERENCES profiles(id),
				item_id TEXT NOT NULL,
				kind TEXT NOT NULL,
				rating INTEGER NOT NULL CHECK (rating BETWEEN 1 AND 10),
				series_id TEXT,
				name TEXT,
				updated_at TEXT NOT NULL,
				PRIMARY KEY (profile_id, item_id)
			);
			CREATE TABLE statuses (
				profile_id INTEGER NOT NULL REFERENCES profiles(id),
				series_id TEXT NOT NULL,
				status TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				PRIMARY KEY (profile_id, series_id)
			);
			CREATE TABLE sync_state (
				profile_id INTEGER PRIMARY KEY REFERENCES profiles(id),
				last_sync_at TEXT,
				backfill_done_at TEXT,
				playback_reporting INTEGER,
				playback_reporting_checked_at TEXT,
				last_error TEXT
			);
			CREATE TABLE pending_actions (
				id INTEGER PRIMARY KEY,
				profile_id INTEGER NOT NULL REFERENCES profiles(id),
				item_id TEXT NOT NULL,
				action TEXT NOT NULL,
				at TEXT NOT NULL,
				entry_id INTEGER,
				attempts INTEGER NOT NULL DEFAULT 0,
				last_error TEXT,
				created_at TEXT NOT NULL
			);
			CREATE TABLE settings (
				key TEXT PRIMARY KEY,
				value TEXT NOT NULL
			);
			"""),
		// the date a play was logged with, kept when the user edits the date: what "already in the journal" is checked against
		(2, """
			ALTER TABLE entries ADD COLUMN logged_at TEXT;
			UPDATE entries SET logged_at = watched_at;
			CREATE INDEX entries_item_logged ON entries (profile_id, item_id, logged_at);
			"""),
	];

	private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

	private readonly string _path;
	private readonly ILogger _logger;
	private readonly TimeProvider _clock;
	private readonly Lock _lock = new();
	private readonly Dictionary<JournalProfile, long> _profileIds = [];
	private SqliteConnection? _connection;
	private JournalUnavailableException? _unavailable;
	private bool _disposed;

	public JournalStore(string path, ILogger<JournalStore>? logger = null, TimeProvider? clock = null)
	{
		_path = path;
		_logger = (ILogger?)logger ?? NullLogger.Instance;
		_clock = clock ?? TimeProvider.System;
	}

	public string Path => _path;

	// ---------------------------------------------------------------- opening, checking, migrating

	/// <summary>Opens (checks and migrates) the file now; throws <see cref="JournalUnavailableException"/> when it cannot be used.</summary>
	public void EnsureOpen() => Run(_ => 0);

	/// <summary>The schema version of the open file.</summary>
	public int GetSchemaVersion() => Run(connection => (int)(long)Scalar(connection, "PRAGMA user_version")!);

	/// <summary>
	/// Runs one store call under the lock on the open connection. A SQLite failure becomes
	/// <see cref="JournalUnavailableException"/>; damage closes the file, so the next call checks it again at open.
	/// </summary>
	private T Run<T>(Func<SqliteConnection, T> work)
	{
		lock (_lock)
		{
			var connection = Connection();
			try
			{
				return work(connection);
			}
			catch (SqliteException ex) when (IsDamage(ex))
			{
				_logger.LogWarning("The journal file is damaged (SQLite error {Code}); closing it, it is checked again on the next use", ex.SqliteErrorCode);
				Close();
				throw new JournalUnavailableException("The journal file is damaged. It is kept aside and a new journal is started the next time the journal is used.", ex);
			}
			catch (SqliteException ex)
			{
				throw new JournalUnavailableException($"The journal could not be read or written: {ex.Message}", ex);
			}
		}
	}

	private void Run(Action<SqliteConnection> work) => Run(connection =>
	{
		work(connection);
		return 0;
	});

	private static bool IsDamage(SqliteException ex) => ex.SqliteErrorCode is SQLitePCL.raw.SQLITE_NOTADB or SQLitePCL.raw.SQLITE_CORRUPT;

	private SqliteConnection Connection()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_connection is not null)
		{
			return _connection;
		}

		if (_unavailable is not null)
		{
			throw _unavailable;
		}

		try
		{
			_connection = OpenOrRecover();
		}
		catch (JournalUnavailableException ex)
		{
			// a newer schema stays newer: not tried again
			_unavailable = ex;
			throw;
		}
		catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or DamagedFileException)
		{
			// not remembered: a locked or missing folder may be fine next time
			throw new JournalUnavailableException($"The journal could not be opened: {ex.Message}", ex);
		}

		return _connection;
	}

	private SqliteConnection OpenOrRecover()
	{
		try
		{
			return OpenAndMigrate();
		}
		catch (Exception ex) when (ex is DamagedFileException || (ex is SqliteException sqlite && IsDamage(sqlite)))
		{
			// keep the damaged file for recovery and start a new journal (as LocalSettingsService does with its file)
			var aside = $"{_path}.corrupt-{_clock.GetUtcNow():yyyyMMddHHmmss}";
			_logger.LogWarning("The journal file is damaged ({Reason}); keeping it as {Aside} and starting a new one",
				ex is SqliteException s ? $"SQLite error {s.SqliteErrorCode}" : ex.Message, System.IO.Path.GetFileName(aside));
			File.Move(_path, aside, overwrite: true);
			return OpenAndMigrate();
		}
	}

	private SqliteConnection OpenAndMigrate()
	{
		if (System.IO.Path.GetDirectoryName(_path) is { Length: > 0 } folder)
		{
			Directory.CreateDirectory(folder);
		}

		var connection = new SqliteConnection(new SqliteConnectionStringBuilder
		{
			DataSource = _path,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Pooling = false,
			ForeignKeys = true,
		}.ToString());
		try
		{
			connection.Open();
			var version = (int)(long)Scalar(connection, "PRAGMA user_version")!;
			if (version > SchemaVersion)
			{
				throw new JournalUnavailableException($"The journal file is from a newer version of the app (schema {version}, this one knows {SchemaVersion}); it is left as it is and the journal is off");
			}

			// the header alone says nothing about the pages behind it (a journal is small: this reads it all in milliseconds)
			if (Scalar(connection, "PRAGMA quick_check(1)") is not "ok")
			{
				throw new DamagedFileException("quick_check found damage");
			}

			foreach (var (step, sql) in Migrations)
			{
				if (step <= version)
				{
					continue;
				}

				using var transaction = connection.BeginTransaction();
				Execute(connection, sql, transaction);
				// a pragma takes no parameters; the number is ours
				Execute(connection, $"PRAGMA user_version = {step.ToString(CultureInfo.InvariantCulture)}", transaction);
				transaction.Commit();
				_logger.LogInformation("Journal schema {From} -> {To}", version, step);
				version = step;
			}

			return connection;
		}
		catch
		{
			connection.Dispose();
			throw;
		}
	}

	private void Close()
	{
		_connection?.Dispose();
		_connection = null;
		_profileIds.Clear();
	}

	public void Dispose()
	{
		lock (_lock)
		{
			_disposed = true;
			Close();
		}
	}

	private sealed class DamagedFileException(string message) : Exception(message);

	// ---------------------------------------------------------------- profiles

	private long ProfileId(SqliteConnection connection, JournalProfile profile, SqliteTransaction? transaction = null)
	{
		if (_profileIds.TryGetValue(profile, out var id))
		{
			return id;
		}

		var found = Scalar(connection, transaction, "SELECT id FROM profiles WHERE server_id = $server AND user_id = $user", ("$server", profile.ServerId), ("$user", Id(profile.UserId)));
		if (found is long existing)
		{
			Execute(connection, "UPDATE profiles SET server_name = coalesce($serverName, server_name), user_name = coalesce($userName, user_name) WHERE id = $id",
				transaction, ("$serverName", profile.ServerName), ("$userName", profile.UserName), ("$id", existing));
			_profileIds[profile] = existing;
			return existing;
		}

		var created = (long)Scalar(connection, transaction, "INSERT INTO profiles (server_id, user_id, server_name, user_name, created_at) VALUES ($server, $user, $serverName, $userName, $now) RETURNING id",
			("$server", profile.ServerId), ("$user", Id(profile.UserId)), ("$serverName", profile.ServerName), ("$userName", profile.UserName), ("$now", Now()))!;
		if (transaction is null)
		{
			_profileIds[profile] = created;
		}

		return created;
	}

	/// <summary>The journals in the file (for tests and a later "switch journal" view).</summary>
	public IReadOnlyList<(string ServerId, Guid UserId)> GetProfiles() =>
		Run(connection => Query(connection, "SELECT server_id, user_id FROM profiles ORDER BY id", r => (r.GetString(0), Guid.Parse(r.GetString(1)))));

	// ---------------------------------------------------------------- entries

	/// <summary>
	/// Adds an entry; null when its play session is already in this journal (also as a deleted entry). The date it is added
	/// with is kept as its logged date, whatever the user later makes of <see cref="DiaryEntry.WatchedAt"/>.
	/// </summary>
	public DiaryEntry? AddEntry(JournalProfile profile, DiaryEntry entry) => Run(connection =>
	{
		using var transaction = connection.BeginTransaction();
		var added = Insert(connection, transaction, ProfileId(connection, profile, transaction), entry);
		transaction.Commit();
		RememberProfile(profile, connection);
		return added;
	});

	/// <summary>
	/// Adds the entry for a finished play session unless that play is already in the journal (deleted entries count), in one
	/// transaction with the checks, so a sync running at the same time cannot add it too:
	/// <list type="bullet">
	/// <item>its play session is there;</item>
	/// <item>an auto-logged entry of the item is dated at or after <paramref name="sessionOpenedAt"/>: an earlier session of
	/// this same play (a track or quality change restarts the stream on a new play session, docs/BRIDGE.md §5);</item>
	/// <item>a synced entry of the item is dated at or after <paramref name="sessionOpenedAt"/> less <see cref="SyncedPlaySlack"/>:
	/// a sync logged this play from the server first.</item>
	/// </list>
	/// The entry is a rewatch when it says so or the item has an earlier entry. Null when not added.
	/// </summary>
	public DiaryEntry? AddPlayedSession(JournalProfile profile, DiaryEntry entry, DateTimeOffset sessionOpenedAt) => Run(connection =>
	{
		using var transaction = connection.BeginTransaction();
		var profileId = ProfileId(connection, profile, transaction);
		var item = Id(entry.ItemId);
		var logged = Scalar(connection, transaction, """
			SELECT 1 FROM entries WHERE profile_id = $profile AND item_id = $item AND (
				(source = 'auto' AND logged_at >= $opened) OR (source <> 'auto' AND logged_at >= $syncedFrom))
			LIMIT 1
			""", ("$profile", profileId), ("$item", item), ("$opened", Time(sessionOpenedAt)), ("$syncedFrom", Time(sessionOpenedAt - SyncedPlaySlack))) is not null;
		if (logged)
		{
			return null;
		}

		var rewatch = entry.Rewatch || HasEntry(connection, transaction, profileId, item);
		var added = Insert(connection, transaction, profileId, entry with { Rewatch = rewatch });
		transaction.Commit();
		RememberProfile(profile, connection);
		return added;
	});

	/// <summary>
	/// Adds a page of plays a sync found, in one transaction (with the checks: see <see cref="SyncCandidate"/>). With
	/// <paramref name="markRewatches"/> a play of an item that already has an entry is a rewatch. How many were added.
	/// </summary>
	public int AddSyncedEntries(JournalProfile profile, IReadOnlyList<SyncCandidate> candidates, bool markRewatches) => Run(connection =>
	{
		if (candidates.Count == 0)
		{
			return 0;
		}

		using var transaction = connection.BeginTransaction();
		var profileId = ProfileId(connection, profile, transaction);
		var added = 0;
		foreach (var (entry, duplicateFrom) in candidates)
		{
			var item = Id(entry.ItemId);
			var known = Scalar(connection, transaction,
				"SELECT 1 FROM entries WHERE profile_id = $profile AND item_id = $item" + (duplicateFrom is null ? "" : " AND logged_at >= $from") + " LIMIT 1",
				("$profile", profileId), ("$item", item), ("$from", Time(duplicateFrom))) is not null;
			if (known)
			{
				continue;
			}

			var rewatch = entry.Rewatch || (markRewatches && HasEntry(connection, transaction, profileId, item));
			if (Insert(connection, transaction, profileId, entry with { Rewatch = rewatch }) is not null)
			{
				added++;
			}
		}

		transaction.Commit();
		RememberProfile(profile, connection);
		return added;
	});

	private void RememberProfile(JournalProfile profile, SqliteConnection connection) => ProfileId(connection, profile);

	private static bool HasEntry(SqliteConnection connection, SqliteTransaction transaction, long profileId, string? item) =>
		Scalar(connection, transaction, "SELECT 1 FROM entries WHERE profile_id = $profile AND item_id = $item AND deleted_at IS NULL LIMIT 1",
			("$profile", profileId), ("$item", item)) is not null;

	private DiaryEntry? Insert(SqliteConnection connection, SqliteTransaction transaction, long profileId, DiaryEntry entry)
	{
		var now = _clock.GetUtcNow();
		if (entry.PlaySessionId is { } session && Scalar(connection, transaction, "SELECT 1 FROM entries WHERE profile_id = $profile AND play_session_id = $session",
			("$profile", profileId), ("$session", session)) is not null)
		{
			return null;
		}

		var id = (long)Scalar(connection, transaction, """
			INSERT INTO entries (profile_id, item_id, series_id, season_id, series_name, season_number, episode_number, title, runtime_ticks,
				image_tag, series_image_tag, watched_at, logged_at, date_source, source, rewatch, review, spoiler, play_session_id, on_server, created_at, updated_at)
			VALUES ($profile, $item, $series, $season, $seriesName, $seasonNumber, $episodeNumber, $title, $runtime,
				$image, $seriesImage, $watched, $watched, $dateSource, $source, $rewatch, $review, $spoiler, $playSession, $onServer, $now, $now)
			RETURNING id
			""",
			("$profile", profileId), ("$item", Id(entry.ItemId)), ("$series", Id(entry.SeriesId)), ("$season", Id(entry.SeasonId)),
			("$seriesName", entry.SeriesName), ("$seasonNumber", entry.SeasonNumber), ("$episodeNumber", entry.EpisodeNumber), ("$title", entry.Title),
			("$runtime", entry.RuntimeTicks), ("$image", entry.ImageTag), ("$seriesImage", entry.SeriesImageTag), ("$watched", Time(entry.WatchedAt)),
			("$dateSource", Name(entry.DateSource)), ("$source", Name(entry.Source)), ("$rewatch", entry.Rewatch), ("$review", entry.Review),
			("$spoiler", entry.Spoiler), ("$playSession", entry.PlaySessionId), ("$onServer", entry.OnServer), ("$now", Time(now)))!;
		return entry with { Id = id, WatchedAt = AsStored(entry.WatchedAt), CreatedAt = AsStored(now), UpdatedAt = AsStored(now), DeletedAt = null };
	}

	public bool HasPlaySession(JournalProfile profile, string playSessionId) => Run(connection =>
		Scalar(connection, "SELECT 1 FROM entries WHERE profile_id = $profile AND play_session_id = $session",
			("$profile", ProfileId(connection, profile)), ("$session", playSessionId)) is not null);

	/// <summary>
	/// Whether this journal has an entry for the item: logged with a date at or after <paramref name="notBefore"/> when given
	/// (the date it was logged with, not one the user set since; an entry logged with no date never counts then), and
	/// counting deleted entries when <paramref name="includeDeleted"/>.
	/// </summary>
	public bool HasEntryFor(JournalProfile profile, Guid itemId, DateTimeOffset? notBefore = null, bool includeDeleted = false) => Run(connection =>
	{
		var sql = "SELECT 1 FROM entries WHERE profile_id = $profile AND item_id = $item" +
			(includeDeleted ? "" : " AND deleted_at IS NULL") +
			(notBefore is null ? "" : " AND logged_at >= $notBefore") +
			" LIMIT 1";
		return Scalar(connection, sql, ("$profile", ProfileId(connection, profile)), ("$item", Id(itemId)), ("$notBefore", Time(notBefore))) is not null;
	});

	/// <summary>The journal's entries, newest first, undated last; deleted ones only when asked.</summary>
	public IReadOnlyList<DiaryEntry> GetEntries(JournalProfile profile, bool includeDeleted = false) => Run(connection =>
		Query(connection, $"SELECT {EntryColumns} FROM entries WHERE profile_id = $profile" + (includeDeleted ? "" : " AND deleted_at IS NULL") +
			" ORDER BY watched_at IS NULL, watched_at DESC, id DESC", ReadEntry, ("$profile", ProfileId(connection, profile))));

	public DiaryEntry? GetEntry(JournalProfile profile, long id) => Run(connection =>
		Query(connection, $"SELECT {EntryColumns} FROM entries WHERE profile_id = $profile AND id = $id", ReadEntry,
			("$profile", ProfileId(connection, profile)), ("$id", id)).FirstOrDefault());

	/// <summary>
	/// Changes what the user can change. The date source becomes <see cref="DiaryDateSource.Manual"/> when the date changed;
	/// the date the play was logged with stays (it is what a sync recognises the play by).
	/// </summary>
	public bool UpdateEntry(JournalProfile profile, long id, DiaryEntryEdit edit) => Run(connection =>
	{
		var profileId = ProfileId(connection, profile);
		var current = Query(connection, $"SELECT {EntryColumns} FROM entries WHERE profile_id = $profile AND id = $id AND deleted_at IS NULL", ReadEntry,
			("$profile", profileId), ("$id", id)).FirstOrDefault();
		if (current is null)
		{
			return false;
		}

		var watchedAt = AsStored(edit.WatchedAt);
		var dateSource = watchedAt == current.WatchedAt ? current.DateSource : DiaryDateSource.Manual;
		Execute(connection, "UPDATE entries SET watched_at = $watched, date_source = $dateSource, rewatch = $rewatch, review = $review, spoiler = $spoiler, updated_at = $now WHERE id = $id AND profile_id = $profile",
			("$watched", Time(watchedAt)), ("$dateSource", Name(dateSource)), ("$rewatch", edit.Rewatch), ("$review", string.IsNullOrWhiteSpace(edit.Review) ? null : edit.Review),
			("$spoiler", edit.Spoiler), ("$now", Now()), ("$id", id), ("$profile", profileId));
		return true;
	});

	/// <summary>Deletes an entry: kept as a tombstone, so neither a sync nor its play session brings it back.</summary>
	public bool DeleteEntry(JournalProfile profile, long id) => Run(connection =>
		Execute(connection, "UPDATE entries SET deleted_at = $now, updated_at = $now WHERE id = $id AND profile_id = $profile AND deleted_at IS NULL",
			("$now", Now()), ("$id", id), ("$profile", ProfileId(connection, profile))) > 0);

	/// <summary>The server now has the play an entry was logged locally for.</summary>
	public void MarkOnServer(JournalProfile profile, long entryId) => Run(connection =>
	{
		Execute(connection, "UPDATE entries SET on_server = 1, updated_at = $now WHERE id = $id AND profile_id = $profile",
			("$now", Now()), ("$id", entryId), ("$profile", ProfileId(connection, profile)));
	});

	private const string EntryColumns = "id, item_id, series_id, season_id, series_name, season_number, episode_number, title, runtime_ticks, image_tag, series_image_tag, " +
		"watched_at, date_source, source, rewatch, review, spoiler, play_session_id, on_server, created_at, updated_at, deleted_at";

	private static DiaryEntry ReadEntry(SqliteDataReader r) => new()
	{
		Id = r.GetInt64(0),
		ItemId = Guid.Parse(r.GetString(1)),
		SeriesId = OptionalGuid(r, 2),
		SeasonId = OptionalGuid(r, 3),
		SeriesName = OptionalString(r, 4),
		SeasonNumber = r.IsDBNull(5) ? null : r.GetInt32(5),
		EpisodeNumber = r.IsDBNull(6) ? null : r.GetInt32(6),
		Title = OptionalString(r, 7),
		RuntimeTicks = r.IsDBNull(8) ? null : r.GetInt64(8),
		ImageTag = OptionalString(r, 9),
		SeriesImageTag = OptionalString(r, 10),
		WatchedAt = OptionalTime(r, 11),
		DateSource = ParseDateSource(r.GetString(12)),
		Source = ParseSource(r.GetString(13)),
		Rewatch = r.GetInt64(14) != 0,
		Review = OptionalString(r, 15),
		Spoiler = r.GetInt64(16) != 0,
		PlaySessionId = OptionalString(r, 17),
		OnServer = r.GetInt64(18) != 0,
		CreatedAt = OptionalTime(r, 19)!.Value,
		UpdatedAt = OptionalTime(r, 20)!.Value,
		DeletedAt = OptionalTime(r, 21),
	};

	// ---------------------------------------------------------------- ratings

	public ItemRating? GetRating(JournalProfile profile, Guid itemId) => Run(connection =>
		Query(connection, "SELECT item_id, kind, rating, series_id, name, updated_at FROM ratings WHERE profile_id = $profile AND item_id = $item", ReadRating,
			("$profile", ProfileId(connection, profile)), ("$item", Id(itemId))).FirstOrDefault());

	public IReadOnlyList<ItemRating> GetRatings(JournalProfile profile) => Run(connection =>
		Query(connection, "SELECT item_id, kind, rating, series_id, name, updated_at FROM ratings WHERE profile_id = $profile ORDER BY updated_at DESC", ReadRating,
			("$profile", ProfileId(connection, profile))));

	/// <summary>Sets the rating (1-10), or clears it with 0.</summary>
	public void SetRating(JournalProfile profile, Guid itemId, RatedItemKind kind, int value, Guid? seriesId = null, string? name = null)
	{
		value = StarRating.Normalize(value);
		Run(connection =>
		{
			var profileId = ProfileId(connection, profile);
			if (value == 0)
			{
				Execute(connection, "DELETE FROM ratings WHERE profile_id = $profile AND item_id = $item", ("$profile", profileId), ("$item", Id(itemId)));
				return;
			}

			Execute(connection, """
				INSERT INTO ratings (profile_id, item_id, kind, rating, series_id, name, updated_at) VALUES ($profile, $item, $kind, $rating, $series, $name, $now)
				ON CONFLICT (profile_id, item_id) DO UPDATE SET kind = excluded.kind, rating = excluded.rating,
					series_id = coalesce(excluded.series_id, series_id), name = coalesce(excluded.name, name), updated_at = excluded.updated_at
				""",
				("$profile", profileId), ("$item", Id(itemId)), ("$kind", Name(kind)), ("$rating", value), ("$series", Id(seriesId)), ("$name", name), ("$now", Now()));
		});
	}

	private static ItemRating ReadRating(SqliteDataReader r) =>
		new(Guid.Parse(r.GetString(0)), ParseKind(r.GetString(1)), r.GetInt32(2), OptionalGuid(r, 3), OptionalString(r, 4), OptionalTime(r, 5)!.Value);

	// ---------------------------------------------------------------- sync state

	public JournalSyncState GetSyncState(JournalProfile profile) => Run(connection =>
		Query(connection, "SELECT last_sync_at, backfill_done_at, playback_reporting, playback_reporting_checked_at, last_error FROM sync_state WHERE profile_id = $profile",
			r => new JournalSyncState
			{
				LastSyncAt = OptionalTime(r, 0),
				BackfillDoneAt = OptionalTime(r, 1),
				PlaybackReportingAvailable = r.IsDBNull(2) ? null : r.GetInt64(2) != 0,
				PlaybackReportingCheckedAt = OptionalTime(r, 3),
				LastError = OptionalString(r, 4),
			}, ("$profile", ProfileId(connection, profile))).FirstOrDefault() ?? new JournalSyncState());

	public void SaveSyncState(JournalProfile profile, JournalSyncState state) => Run(connection =>
	{
		Execute(connection, """
			INSERT INTO sync_state (profile_id, last_sync_at, backfill_done_at, playback_reporting, playback_reporting_checked_at, last_error)
			VALUES ($profile, $last, $backfill, $pr, $prChecked, $error)
			ON CONFLICT (profile_id) DO UPDATE SET last_sync_at = excluded.last_sync_at, backfill_done_at = excluded.backfill_done_at,
				playback_reporting = excluded.playback_reporting, playback_reporting_checked_at = excluded.playback_reporting_checked_at, last_error = excluded.last_error
			""",
			("$profile", ProfileId(connection, profile)), ("$last", Time(state.LastSyncAt)), ("$backfill", Time(state.BackfillDoneAt)),
			("$pr", state.PlaybackReportingAvailable), ("$prChecked", Time(state.PlaybackReportingCheckedAt)), ("$error", state.LastError));
	});

	// ---------------------------------------------------------------- pending writes to Jellyfin

	public long AddPendingAction(JournalProfile profile, Guid itemId, string action, DateTimeOffset at, long? entryId) => Run(connection =>
		(long)Scalar(connection, "INSERT INTO pending_actions (profile_id, item_id, action, at, entry_id, created_at) VALUES ($profile, $item, $action, $at, $entry, $now) RETURNING id",
			("$profile", ProfileId(connection, profile)), ("$item", Id(itemId)), ("$action", action), ("$at", Time(at)), ("$entry", entryId), ("$now", Now()))!);

	public IReadOnlyList<PendingJournalAction> GetPendingActions(JournalProfile profile) => Run(connection =>
		Query(connection, "SELECT id, item_id, action, at, entry_id, attempts FROM pending_actions WHERE profile_id = $profile ORDER BY id",
			r => new PendingJournalAction(r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetString(2), OptionalTime(r, 3)!.Value, r.IsDBNull(4) ? null : r.GetInt64(4), r.GetInt32(5)),
			("$profile", ProfileId(connection, profile))));

	public void CompletePendingAction(long id) => Run(connection =>
	{
		Execute(connection, "DELETE FROM pending_actions WHERE id = $id", ("$id", id));
	});

	public void FailPendingAction(long id, string error) => Run(connection =>
	{
		Execute(connection, "UPDATE pending_actions SET attempts = attempts + 1, last_error = $error WHERE id = $id", ("$id", id), ("$error", error));
	});

	// ---------------------------------------------------------------- settings

	public JournalSettings GetSettings() => Run(connection =>
	{
		var values = Query(connection, "SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
		var defaults = new JournalSettings();
		return new JournalSettings
		{
			AutoLog = values.TryGetValue("auto_log", out var autoLog) ? autoLog == "1" : defaults.AutoLog,
			RatingPrompt = values.TryGetValue("rating_prompt", out var prompt) && Enum.TryParse<RatingPromptMode>(prompt, ignoreCase: true, out var mode) ? mode : defaults.RatingPrompt,
			MirrorRatingToJellyfin = values.TryGetValue("mirror_rating", out var mirror) ? mirror == "1" : defaults.MirrorRatingToJellyfin,
		};
	});

	public void SaveSettings(JournalSettings settings) => Run(connection =>
	{
		using var transaction = connection.BeginTransaction();
		foreach (var (key, value) in new[]
		{
			("auto_log", settings.AutoLog ? "1" : "0"),
			("rating_prompt", settings.RatingPrompt.ToString()),
			("mirror_rating", settings.MirrorRatingToJellyfin ? "1" : "0"),
		})
		{
			Execute(connection, "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
				transaction, ("$key", key), ("$value", value));
		}

		transaction.Commit();
	});

	// ---------------------------------------------------------------- helpers

	private string Now() => Time(_clock.GetUtcNow())!;

	internal static string? Time(DateTimeOffset? value) => value?.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

	/// <summary>A time as the store gives it back: the same instant, in UTC (kept to the tick).</summary>
	internal static DateTimeOffset? AsStored(DateTimeOffset? value) => value is { } v ? new DateTimeOffset(v.UtcDateTime, TimeSpan.Zero) : null;

	private static DateTimeOffset AsStored(DateTimeOffset value) => new(value.UtcDateTime, TimeSpan.Zero);

	private static string? Id(Guid? id) => id?.ToString("N");

	private static Guid? OptionalGuid(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));

	private static string? OptionalString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

	private static DateTimeOffset? OptionalTime(SqliteDataReader r, int i) => r.IsDBNull(i)
		? null
		: DateTimeOffset.ParseExact(r.GetString(i), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

	internal static string Name(DiaryEntrySource source) => source switch
	{
		DiaryEntrySource.AutoLog => "auto",
		DiaryEntrySource.Backfill => "backfill",
		DiaryEntrySource.CatchUp => "catchup",
		_ => "manual",
	};

	private static DiaryEntrySource ParseSource(string value) => value switch
	{
		"auto" => DiaryEntrySource.AutoLog,
		"backfill" => DiaryEntrySource.Backfill,
		"catchup" => DiaryEntrySource.CatchUp,
		_ => DiaryEntrySource.Manual,
	};

	internal static string Name(DiaryDateSource source) => source switch
	{
		DiaryDateSource.Session => "session",
		DiaryDateSource.PlaybackReporting => "playback_reporting",
		DiaryDateSource.LastPlayed => "last_played",
		DiaryDateSource.Manual => "manual",
		_ => "unknown",
	};

	private static DiaryDateSource ParseDateSource(string value) => value switch
	{
		"session" => DiaryDateSource.Session,
		"playback_reporting" => DiaryDateSource.PlaybackReporting,
		"last_played" => DiaryDateSource.LastPlayed,
		"manual" => DiaryDateSource.Manual,
		_ => DiaryDateSource.Unknown,
	};

	internal static string Name(RatedItemKind kind) => kind switch
	{
		RatedItemKind.Series => "series",
		RatedItemKind.Season => "season",
		_ => "episode",
	};

	private static RatedItemKind ParseKind(string value) => value switch
	{
		"series" => RatedItemKind.Series,
		"season" => RatedItemKind.Season,
		_ => RatedItemKind.Episode,
	};

	private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction, (string Name, object? Value)[] parameters)
	{
		var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Transaction = transaction;
		foreach (var (name, value) in parameters)
		{
			if (sql.Contains(name, StringComparison.Ordinal))
			{
				command.Parameters.AddWithValue(name, value switch
				{
					null => DBNull.Value,
					bool b => b ? 1L : 0L,
					_ => value,
				});
			}
		}

		return command;
	}

	private static int Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) => Execute(connection, sql, null, parameters);

	private static int Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
	{
		using var command = Command(connection, sql, transaction, parameters);
		return command.ExecuteNonQuery();
	}

	private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) => Scalar(connection, null, sql, parameters);

	private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
	{
		using var command = Command(connection, sql, transaction, parameters);
		return command.ExecuteScalar();
	}

	private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
	{
		using var command = Command(connection, sql, null, parameters);
		using var reader = command.ExecuteReader();
		var results = new List<T>();
		while (reader.Read())
		{
			results.Add(read(reader));
		}

		return results;
	}
}
