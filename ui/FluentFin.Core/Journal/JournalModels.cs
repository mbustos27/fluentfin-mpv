namespace FluentFin.Core.Journal;

// fluentfin-mpv: the TV journal (docs/JOURNAL.md). Everything in this folder is new code of this fork.

/// <summary>
/// Whose journal: one per Jellyfin server and user (docs/JOURNAL.md, decision 6). Equal when the server id (without case)
/// and the user id are; the names and the url are only for display and building image urls.
/// </summary>
public sealed class JournalProfile : IEquatable<JournalProfile>
{
	public JournalProfile(string serverId, Guid userId, string? serverName = null, string? userName = null, string? serverUrl = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
		if (userId == Guid.Empty)
		{
			throw new ArgumentException("A journal needs a user", nameof(userId));
		}

		ServerId = serverId.Trim().ToLowerInvariant();
		UserId = userId;
		ServerName = serverName;
		UserName = userName;
		ServerUrl = serverUrl;
	}

	public string ServerId { get; }

	public Guid UserId { get; }

	public string? ServerName { get; }

	public string? UserName { get; }

	/// <summary>The server's base url, for image urls. Never logged (it may carry credentials).</summary>
	public string? ServerUrl { get; }

	public bool Equals(JournalProfile? other) => other is not null && ServerId == other.ServerId && UserId == other.UserId;

	public override bool Equals(object? obj) => Equals(obj as JournalProfile);

	public override int GetHashCode() => HashCode.Combine(ServerId, UserId);

	public static bool operator ==(JournalProfile? a, JournalProfile? b) => a is null ? b is null : a.Equals(b);

	public static bool operator !=(JournalProfile? a, JournalProfile? b) => !(a == b);

	/// <summary>Ids only: never the url.</summary>
	public override string ToString() => $"user {UserId:N} on server {ServerId}";
}

/// <summary>How an entry got into the diary.</summary>
public enum DiaryEntrySource
{
	/// <summary>Written when an episode finished in FluentFin's mpv player.</summary>
	AutoLog,

	/// <summary>The first sync of a journal: every episode Jellyfin had as played.</summary>
	Backfill,

	/// <summary>A later sync: plays from other clients since the last sync.</summary>
	CatchUp,

	/// <summary>Added by hand (later increments).</summary>
	Manual,
}

/// <summary>Where an entry's date comes from, and so how exact it is.</summary>
public enum DiaryDateSource
{
	/// <summary>The end of the play session FluentFin watched: exact.</summary>
	Session,

	/// <summary>The Playback Reporting plugin's play history: exact (its time zone is not verified, docs/JOURNAL.md).</summary>
	PlaybackReporting,

	/// <summary>Jellyfin's <c>LastPlayedDate</c>: when the last play started, or when it was marked played. Approximate.</summary>
	LastPlayed,

	/// <summary>Set by the user.</summary>
	Manual,

	/// <summary>Jellyfin has the episode as played but no date at all.</summary>
	Unknown,
}

public enum RatedItemKind
{
	Series,
	Season,
	Episode,
}

/// <summary>When to offer the rating prompt after an episode (the prompt itself is a later increment; off by default).</summary>
public enum RatingPromptMode
{
	Off,
	EveryEpisode,
	FinalesOnly,
}

/// <summary>One watch of one episode.</summary>
public sealed record DiaryEntry
{
	public long Id { get; init; }

	/// <summary>The Jellyfin episode.</summary>
	public Guid ItemId { get; init; }

	public Guid? SeriesId { get; init; }

	public Guid? SeasonId { get; init; }

	public string? SeriesName { get; init; }

	public int? SeasonNumber { get; init; }

	public int? EpisodeNumber { get; init; }

	/// <summary>The episode's name.</summary>
	public string? Title { get; init; }

	public long? RuntimeTicks { get; init; }

	/// <summary>The episode's Primary image tag (its still), null when it has none.</summary>
	public string? ImageTag { get; init; }

	/// <summary>The series' Primary image tag, the fallback image.</summary>
	public string? SeriesImageTag { get; init; }

	/// <summary>When it was watched (UTC); null when Jellyfin knows it was played but not when.</summary>
	public DateTimeOffset? WatchedAt { get; init; }

	public DiaryDateSource DateSource { get; init; }

	public DiaryEntrySource Source { get; init; }

	public bool Rewatch { get; init; }

	public string? Review { get; init; }

	public bool Spoiler { get; init; }

	/// <summary>The play session it was logged for (auto-log only): the dedupe key.</summary>
	public string? PlaySessionId { get; init; }

	/// <summary>False when the server was not told yet (the backend's Stopped report failed; a mark-played is queued).</summary>
	public bool OnServer { get; init; } = true;

	public DateTimeOffset CreatedAt { get; init; }

	public DateTimeOffset UpdatedAt { get; init; }

	/// <summary>Deleted entries are kept as tombstones so a sync does not bring them back.</summary>
	public DateTimeOffset? DeletedAt { get; init; }

	public bool IsDateApproximate => DateSource is DiaryDateSource.LastPlayed or DiaryDateSource.Unknown;
}

/// <summary>What the user can change on an entry.</summary>
public sealed record DiaryEntryEdit(DateTimeOffset? WatchedAt, bool Rewatch, string? Review, bool Spoiler);

/// <summary>The user's rating of a series, season or episode: 1-10, half-stars (decision 5).</summary>
public sealed record ItemRating(Guid ItemId, RatedItemKind Kind, int Value, Guid? SeriesId, string? Name, DateTimeOffset UpdatedAt);

/// <summary>Jellyfin's user data for an item, as far as the journal reads it.</summary>
public sealed record JournalUserData(bool Played, int PlayCount, DateTimeOffset? LastPlayedDate, bool IsFavorite = false, double? Rating = null);

/// <summary>An item as the journal needs it from Jellyfin.</summary>
public sealed record JournalEpisode(
	Guid Id,
	string? Type,
	string? Name,
	Guid? SeriesId,
	string? SeriesName,
	Guid? SeasonId,
	int? SeasonNumber,
	int? EpisodeNumber,
	long? RuntimeTicks,
	string? ImageTag,
	string? SeriesImageTag,
	JournalUserData? UserData)
{
	public const string EpisodeType = "Episode";

	public bool IsEpisode => string.Equals(Type, EpisodeType, StringComparison.OrdinalIgnoreCase);
}

public sealed record JournalPage<T>(IReadOnlyList<T> Items, int? TotalCount);

/// <summary>One play from the Playback Reporting plugin's history.</summary>
public sealed record PlaybackHistoryPlay(Guid ItemId, DateTimeOffset StartedAt, TimeSpan Duration)
{
	public DateTimeOffset EndedAt => StartedAt + Duration;
}

/// <summary>The Playback Reporting plugin's history for the user, or why there is none (not installed, not an administrator).</summary>
public sealed record PlaybackHistory(bool Available, IReadOnlyList<PlaybackHistoryPlay> Plays, string? Reason)
{
	public static PlaybackHistory Unavailable(string reason) => new(false, [], reason);
}

/// <summary>The journal's own settings (stored in journal.db, for every journal on this PC).</summary>
public sealed record JournalSettings
{
	/// <summary>Write a diary entry when an episode finishes (decision 3).</summary>
	public bool AutoLog { get; init; } = true;

	/// <summary>Decision 4: off by default; stored now, used by the rating prompt of a later increment.</summary>
	public RatingPromptMode RatingPrompt { get; init; } = RatingPromptMode.Off;

	/// <summary>Also send star ratings to Jellyfin's per-user rating. Off by default: not verified against the user's server (decision 5).</summary>
	public bool MirrorRatingToJellyfin { get; init; }
}

/// <summary>Where a journal's sync with Jellyfin is.</summary>
public sealed record JournalSyncState
{
	/// <summary>When the last completed sync started (this PC's clock, UTC); null before the first (the backfill).</summary>
	public DateTimeOffset? LastSyncAt { get; init; }

	public DateTimeOffset? BackfillDoneAt { get; init; }

	/// <summary>Whether the Playback Reporting history was readable at the last check; null when never checked.</summary>
	public bool? PlaybackReportingAvailable { get; init; }

	public DateTimeOffset? PlaybackReportingCheckedAt { get; init; }

	/// <summary>Why the last sync did not finish, null when it did.</summary>
	public string? LastError { get; init; }
}

/// <summary>A write to Jellyfin the journal still owes (for now: mark played, after a failed Stopped report).</summary>
public sealed record PendingJournalAction(long Id, Guid ItemId, string Action, DateTimeOffset At, long? EntryId, int Attempts)
{
	public const string MarkPlayed = "mark_played";
}

/// <summary>The journal store could not be opened (a newer schema, a file it cannot use): the journal is off, the app runs on.</summary>
public sealed class JournalUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
