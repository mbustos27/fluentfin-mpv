namespace FluentFin.Core.Journal;

/// <summary>
/// What the journal reads from and writes to the Jellyfin server, as the signed-in user (FluentFin's own client,
/// <c>JellyfinClient_Journal.cs</c>). Narrow on purpose: the tests fake it, and a later TMDB layer (docs/JOURNAL.md) sits
/// beside it as its own interface instead of growing this one. Every method answers null/false/<see cref="PlaybackHistory.Unavailable"/>
/// when the request failed (logged without the token); none throws.
/// </summary>
public interface IJournalServer
{
	/// <summary>The user's data for an item now (<c>GET /UserItems/{id}/UserData</c>).</summary>
	Task<JournalUserData?> GetUserDataAsync(Guid itemId, CancellationToken cancellationToken = default);

	/// <summary>The item with its series, season, numbers, image tags and user data (<c>GET /Items/{id}</c>).</summary>
	Task<JournalEpisode?> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default);

	/// <summary>
	/// A page of the episodes the user has played, the most recently played first (<c>GET /Items</c> with
	/// <c>Filters=IsPlayed</c>, <c>SortBy=DatePlayed</c> descending), each with its user data.
	/// </summary>
	Task<JournalPage<JournalEpisode>?> GetPlayedEpisodesAsync(int startIndex, int limit, CancellationToken cancellationToken = default);

	/// <summary>Marks an item played at that date (<c>POST /UserPlayedItems/{id}?datePlayed=</c>). True when the server accepted it.</summary>
	Task<bool> MarkPlayedAsync(Guid itemId, DateTimeOffset datePlayed, CancellationToken cancellationToken = default);

	/// <summary>
	/// Sets the user's numeric rating (0-10) of an item (<c>POST /UserItems/{id}/UserData</c> with <c>Rating</c>). Only
	/// behind the setting that defaults off: whether the user's server keeps it is not verified (docs/JOURNAL.md).
	/// </summary>
	Task<bool> SetRatingAsync(Guid itemId, int rating, CancellationToken cancellationToken = default);

	/// <summary>
	/// The Playback Reporting plugin's play history of episodes for this user, when the plugin is installed and the user may
	/// read it (its API is for administrators: a 401/403 means "not available", silently, as does a 404).
	/// </summary>
	Task<PlaybackHistory> GetPlaybackHistoryAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Who is signed in to FluentFin now: the journal that is current.</summary>
public interface IJournalSession
{
	/// <summary>Null when nobody is signed in.</summary>
	JournalProfile? Current { get; }
}
