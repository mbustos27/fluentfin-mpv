using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Core.Journal;

/// <summary>
/// The journal's SQLite file, <c>journal.db</c> in the app's data folder (docs/JOURNAL.md). One connection for the store's
/// life, every call under one lock: calls are small and the file is private to this process. Times are stored as fixed-width
/// UTC text (so they sort as text), ids as 32 lowercase hex digits. Every table is keyed by the journal (server + user).
/// Opened on first use; a file that is not a database is moved aside and a new one started, a file of a newer schema is
/// left alone and the journal is off (<see cref="JournalUnavailableException"/>).
/// </summary>
public sealed class JournalStore : IDisposable
{
	public const string FileName = "journal.db";

	/// <summary>The schema this build writes. A file with a higher <c>user_version</c> came from a newer build: not touched.</summary>
	public static int SchemaVersion => Migrations[^1].Version;

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

	// ---------------------------------------------------------------- opening and migrations

	/// <summary>Opens (and migrates) the file now; throws <see cref="JournalUnavailableException"/> when it cannot be used.</summary>
	public void EnsureOpen()
	{
		lock (_lock)
		{
			Connection();
		}
	}

	/// <summary>The schema version of the open file.</summary>
	public int GetSchemaVersion()
	{
		lock (_lock)
		{
			return (int)(long)Scalar(Connection(), "PRAGMA user_version")!;
		}
	}

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
		catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
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
		catch (SqliteException ex) when (ex.SqliteErrorCode is SQLitePCL.raw.SQLITE_NOTADB or SQLitePCL.raw.SQLITE_CORRUPT)
		{
			// keep the unreadable file for recovery and start a new journal (as LocalSettingsService does with its file)
			var aside = $"{_path}.corrupt-{_clock.GetUtcNow():yyyyMMddHHmmss}";
			_logger.LogWarning("The journal file could not be read (SQLite error {Code}); keeping it as {Aside} and starting a new one", ex.SqliteErrorCode, System.IO.Path.GetFileName(aside));
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

			foreach (var (step, sql) in Migrations)
			{
				if (step <= version)
				{
					continue;
				}

				using var transaction = connection.BeginTransaction();
				Execute(connection, sql, transaction: transaction);
				// a pragma takes no parameters; the number is ours
				Execute(connection, $"PRAGMA user_version = {step.ToString(CultureInfo.InvariantCulture)}", transaction: transaction);
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

	public void Dispose()
	{
		lock (_lock)
		{
			_disposed = true;
			_connection?.Dispose();
			_connection = null;
		}
	}

	// ---------------------------------------------------------------- profiles

	private long ProfileId(SqliteConnection connection, JournalProfile profile)
	{
		if (_profileIds.TryGetValue(profile, out var id))
		{
			return id;
		}

		var found = Scalar(connection, "SELECT id FROM profiles WHERE server_id = $server AND user_id = $user", ("$server", profile.ServerId), ("$user", Id(profile.UserId)));
		if (found is long existing)
		{
			Execute(connection, "UPDATE profiles SET server_name = coalesce($serverName, server_name), user_name = coalesce($userName, user_name) WHERE id = $id",
				("$serverName", profile.ServerName), ("$userName", profile.UserName), ("$id", existing));
			_profileIds[profile] = existing;
			return existing;
		}

		var created = (long)Scalar(connection, "INSERT INTO profiles (server_id, user_id, server_name, user_name, created_at) VALUES ($server, $user, $serverName, $userName, $now) RETURNING id",
			("$server", profile.ServerId), ("$user", Id(profile.UserId)), ("$serverName", profile.ServerName), ("$userName", profile.UserName), ("$now", Now()))!;
		_profileIds[profile] = created;
		return created;
	}

	/// <summary>The journals in the file (for tests and a later "switch journal" view).</summary>
	public IReadOnlyList<(string ServerId, Guid UserId)> GetProfiles()
	{
		lock (_lock)
		{
			return Query(Connection(), "SELECT server_id, user_id FROM profiles ORDER BY id", r => (r.GetString(0), Guid.Parse(r.GetString(1))));
		}
	}

	// ---------------------------------------------------------------- entries

	/// <summary>
	/// Adds an entry; null when its play session is already in this journal (also as a deleted entry): auto-logging is
	/// deduplicated by play session id.
	/// </summary>
	public DiaryEntry? AddEntry(JournalProfile profile, DiaryEntry entry)
	{
		lock (_lock)
		{
			var connection = Connection();
			var profileId = ProfileId(connection, profile);
			var now = _clock.GetUtcNow();
			try
			{
				var id = (long)Scalar(connection, """
					INSERT INTO entries (profile_id, item_id, series_id, season_id, series_name, season_number, episode_number, title, runtime_ticks,
						image_tag, series_image_tag, watched_at, date_source, source, rewatch, review, spoiler, play_session_id, on_server, created_at, updated_at)
					VALUES ($profile, $item, $series, $season, $seriesName, $seasonNumber, $episodeNumber, $title, $runtime,
						$image, $seriesImage, $watched, $dateSource, $source, $rewatch, $review, $spoiler, $playSession, $onServer, $now, $now)
					RETURNING id
					""",
					("$profile", profileId), ("$item", Id(entry.ItemId)), ("$series", Id(entry.SeriesId)), ("$season", Id(entry.SeasonId)),
					("$seriesName", entry.SeriesName), ("$seasonNumber", entry.SeasonNumber), ("$episodeNumber", entry.EpisodeNumber), ("$title", entry.Title),
					("$runtime", entry.RuntimeTicks), ("$image", entry.ImageTag), ("$seriesImage", entry.SeriesImageTag), ("$watched", Time(entry.WatchedAt)),
					("$dateSource", Name(entry.DateSource)), ("$source", Name(entry.Source)), ("$rewatch", entry.Rewatch), ("$review", entry.Review),
					("$spoiler", entry.Spoiler), ("$playSession", entry.PlaySessionId), ("$onServer", entry.OnServer), ("$now", Time(now)))!;
				return entry with { Id = id, WatchedAt = AsStored(entry.WatchedAt), CreatedAt = AsStored(now), UpdatedAt = AsStored(now), DeletedAt = null };
			}
			catch (SqliteException ex) when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_CONSTRAINT && entry.PlaySessionId is not null)
			{
				return null;
			}
		}
	}

	public bool HasPlaySession(JournalProfile profile, string playSessionId)
	{
		lock (_lock)
		{
			var connection = Connection();
			return Scalar(connection, "SELECT 1 FROM entries WHERE profile_id = $profile AND play_session_id = $session",
				("$profile", ProfileId(connection, profile)), ("$session", playSessionId)) is not null;
		}
	}

	/// <summary>
	/// Whether this journal has an entry for the item: dated at or after <paramref name="notBefore"/> when given (an entry with
	/// no date never counts then), and counting deleted entries when <paramref name="includeDeleted"/>.
	/// </summary>
	public bool HasEntryFor(JournalProfile profile, Guid itemId, DateTimeOffset? notBefore = null, bool includeDeleted = false)
	{
		lock (_lock)
		{
			var connection = Connection();
			var sql = "SELECT 1 FROM entries WHERE profile_id = $profile AND item_id = $item" +
				(includeDeleted ? "" : " AND deleted_at IS NULL") +
				(notBefore is null ? "" : " AND watched_at >= $notBefore") +
				" LIMIT 1";
			return Scalar(connection, sql, ("$profile", ProfileId(connection, profile)), ("$item", Id(itemId)), ("$notBefore", Time(notBefore))) is not null;
		}
	}

	/// <summary>The journal's entries, newest first, undated last; deleted ones only when asked.</summary>
	public IReadOnlyList<DiaryEntry> GetEntries(JournalProfile profile, bool includeDeleted = false)
	{
		lock (_lock)
		{
			var connection = Connection();
			return Query(connection, $"SELECT {EntryColumns} FROM entries WHERE profile_id = $profile" + (includeDeleted ? "" : " AND deleted_at IS NULL") +
				" ORDER BY watched_at IS NULL, watched_at DESC, id DESC", ReadEntry, ("$profile", ProfileId(connection, profile)));
		}
	}

	public DiaryEntry? GetEntry(JournalProfile profile, long id)
	{
		lock (_lock)
		{
			var connection = Connection();
			return Query(connection, $"SELECT {EntryColumns} FROM entries WHERE profile_id = $profile AND id = $id", ReadEntry,
				("$profile", ProfileId(connection, profile)), ("$id", id)).FirstOrDefault();
		}
	}

	/// <summary>Changes what the user can change. The date source becomes <see cref="DiaryDateSource.Manual"/> when the date changed.</summary>
	public bool UpdateEntry(JournalProfile profile, long id, DiaryEntryEdit edit)
	{
		lock (_lock)
		{
			var connection = Connection();
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
		}
	}

	/// <summary>Deletes an entry: kept as a tombstone, so neither a sync nor its play session brings it back.</summary>
	public bool DeleteEntry(JournalProfile profile, long id)
	{
		lock (_lock)
		{
			var connection = Connection();
			return Execute(connection, "UPDATE entries SET deleted_at = $now, updated_at = $now WHERE id = $id AND profile_id = $profile AND deleted_at IS NULL",
				("$now", Now()), ("$id", id), ("$profile", ProfileId(connection, profile))) > 0;
		}
	}

	/// <summary>The server now has the play an entry was logged locally for.</summary>
	public void MarkOnServer(JournalProfile profile, long entryId)
	{
		lock (_lock)
		{
			var connection = Connection();
			Execute(connection, "UPDATE entries SET on_server = 1, updated_at = $now WHERE id = $id AND profile_id = $profile",
				("$now", Now()), ("$id", entryId), ("$profile", ProfileId(connection, profile)));
		}
	}

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

	public ItemRating? GetRating(JournalProfile profile, Guid itemId)
	{
		lock (_lock)
		{
			var connection = Connection();
			return Query(connection, "SELECT item_id, kind, rating, series_id, name, updated_at FROM ratings WHERE profile_id = $profile AND item_id = $item", ReadRating,
				("$profile", ProfileId(connection, profile)), ("$item", Id(itemId))).FirstOrDefault();
		}
	}

	public IReadOnlyList<ItemRating> GetRatings(JournalProfile profile)
	{
		lock (_lock)
		{
			var connection = Connection();
			return Query(connection, "SELECT item_id, kind, rating, series_id, name, updated_at FROM ratings WHERE profile_id = $profile ORDER BY updated_at DESC", ReadRating,
				("$profile", ProfileId(connection, profile)));
		}
	}

	/// <summary>Sets the rating (1-10), or clears it with 0.</summary>
	public void SetRating(JournalProfile profile, Guid itemId, RatedItemKind kind, int value, Guid? seriesId = null, string? name = null)
	{
		value = StarRating.Normalize(value);
		lock (_lock)
		{
			var connection = Connection();
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
		}
	}

	private static ItemRating ReadRating(SqliteDataReader r) =>
		new(Guid.Parse(r.GetString(0)), ParseKind(r.GetString(1)), r.GetInt32(2), OptionalGuid(r, 3), OptionalString(r, 4), OptionalTime(r, 5)!.Value);

	// ---------------------------------------------------------------- sync state

	public JournalSyncState GetSyncState(JournalProfile profile)
	{
		lock (_lock)
		{
			var connection = Connection();
			return Query(connection, "SELECT last_sync_at, backfill_done_at, playback_reporting, playback_reporting_checked_at, last_error FROM sync_state WHERE profile_id = $profile",
				r => new JournalSyncState
				{
					LastSyncAt = OptionalTime(r, 0),
					BackfillDoneAt = OptionalTime(r, 1),
					PlaybackReportingAvailable = r.IsDBNull(2) ? null : r.GetInt64(2) != 0,
					PlaybackReportingCheckedAt = OptionalTime(r, 3),
					LastError = OptionalString(r, 4),
				}, ("$profile", ProfileId(connection, profile))).FirstOrDefault() ?? new JournalSyncState();
		}
	}

	public void SaveSyncState(JournalProfile profile, JournalSyncState state)
	{
		lock (_lock)
		{
			var connection = Connection();
			Execute(connection, """
				INSERT INTO sync_state (profile_id, last_sync_at, backfill_done_at, playback_reporting, playback_reporting_checked_at, last_error)
				VALUES ($profile, $last, $backfill, $pr, $prChecked, $error)
				ON CONFLICT (profile_id) DO UPDATE SET last_sync_at = excluded.last_sync_at, backfill_done_at = excluded.backfill_done_at,
					playback_reporting = excluded.playback_reporting, playback_reporting_checked_at = excluded.playback_reporting_checked_at, last_error = excluded.last_error
				""",
				("$profile", ProfileId(connection, profile)), ("$last", Time(state.LastSyncAt)), ("$backfill", Time(state.BackfillDoneAt)),
				("$pr", state.PlaybackReportingAvailable), ("$prChecked", Time(state.PlaybackReportingCheckedAt)), ("$error", state.LastError));
		}
	}

	// ---------------------------------------------------------------- pending writes to Jellyfin

	public long AddPendingAction(JournalProfile profile, Guid itemId, string action, DateTimeOffset at, long? entryId)
	{
		lock (_lock)
		{
			var connection = Connection();
			return (long)Scalar(connection, "INSERT INTO pending_actions (profile_id, item_id, action, at, entry_id, created_at) VALUES ($profile, $item, $action, $at, $entry, $now) RETURNING id",
				("$profile", ProfileId(connection, profile)), ("$item", Id(itemId)), ("$action", action), ("$at", Time(at)), ("$entry", entryId), ("$now", Now()))!;
		}
	}

	public IReadOnlyList<PendingJournalAction> GetPendingActions(JournalProfile profile)
	{
		lock (_lock)
		{
			var connection = Connection();
			return Query(connection, "SELECT id, item_id, action, at, entry_id, attempts FROM pending_actions WHERE profile_id = $profile ORDER BY id",
				r => new PendingJournalAction(r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetString(2), OptionalTime(r, 3)!.Value, r.IsDBNull(4) ? null : r.GetInt64(4), r.GetInt32(5)),
				("$profile", ProfileId(connection, profile)));
		}
	}

	public void CompletePendingAction(long id)
	{
		lock (_lock)
		{
			Execute(Connection(), "DELETE FROM pending_actions WHERE id = $id", ("$id", id));
		}
	}

	public void FailPendingAction(long id, string error)
	{
		lock (_lock)
		{
			Execute(Connection(), "UPDATE pending_actions SET attempts = attempts + 1, last_error = $error WHERE id = $id", ("$id", id), ("$error", error));
		}
	}

	// ---------------------------------------------------------------- settings

	public JournalSettings GetSettings()
	{
		lock (_lock)
		{
			var values = Query(Connection(), "SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
			var defaults = new JournalSettings();
			return new JournalSettings
			{
				AutoLog = values.TryGetValue("auto_log", out var autoLog) ? autoLog == "1" : defaults.AutoLog,
				RatingPrompt = values.TryGetValue("rating_prompt", out var prompt) && Enum.TryParse<RatingPromptMode>(prompt, ignoreCase: true, out var mode) ? mode : defaults.RatingPrompt,
				MirrorRatingToJellyfin = values.TryGetValue("mirror_rating", out var mirror) ? mirror == "1" : defaults.MirrorRatingToJellyfin,
			};
		}
	}

	public void SaveSettings(JournalSettings settings)
	{
		lock (_lock)
		{
			var connection = Connection();
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
		}
	}

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

	private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
	{
		using var command = Command(connection, sql, null, parameters);
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
