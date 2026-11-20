using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Services;

/// <summary>
/// 1.2.0 (items review, item 6): the episodes of a series as Play reads them (<see cref="IJellyfinClient.GetSeriesEpisodes"/>,
/// in <see cref="Mpv.MpvQueue"/>'s order), for the series and season pages' Play label (<see cref="ItemPlay"/>). The label must
/// name what Play starts, the series' first episode not played, which only this list says (the server's Next Up is the
/// episode after the last one watched, not the same thing). It was asked for again on every page open and after every
/// toggle; now it is kept per series and asked for again only when something may have changed it on the server: a
/// watch-state or library change the server reported, a sign-in, a player page opened or left (the change count of
/// <see cref="HomeCache"/>), or after <see cref="FreshFor"/>. A toggle in the app changes it in place from the server's
/// answer (<see cref="Apply"/>), as the server does: one episode, or every episode of a season or of the series.
/// </summary>
public sealed class SeriesEpisodes : IDisposable
{
	public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

	private readonly IJellyfinClient _client;
	private readonly Func<int> _changes;
	private readonly TimeProvider _time;
	private readonly bool _subscribed;
	private readonly Lock _lock = new();
	private readonly Dictionary<Guid, Entry> _entries = [];

	private sealed record Entry(List<BaseItemDto> Episodes, long ReadAt, int Changes);

	/// <summary>The app's one: kept while <paramref name="changes"/>' change count stays put.</summary>
	public SeriesEpisodes(IJellyfinClient client, HomeCache changes)
		: this(client, () => changes.ChangeCount, TimeProvider.System, subscribe: true)
	{
	}

	/// <param name="subscribe">Follow every toggle in the app (<see cref="ItemUserData.Changed"/>); a page's own copy (a view
	/// model made without the app's) is told by its page instead (<see cref="Apply"/>).</param>
	internal SeriesEpisodes(IJellyfinClient client, Func<int> changes, TimeProvider time, bool subscribe)
	{
		_client = client;
		_changes = changes;
		_time = time;
		_subscribed = subscribe;
		if (subscribe)
		{
			ItemUserData.Changed += OnUserDataChanged;
		}
	}

	/// <summary>A view model made without the app's (tests, a page of its own): kept for the page's life, never stale by itself.</summary>
	internal static SeriesEpisodes ForOnePage(IJellyfinClient client) => new(client, () => 0, TimeProvider.System, subscribe: false);

	/// <summary>
	/// Nixie 1.4.1: the request on its way per series, with the change count it was asked at: a second asker meanwhile (the
	/// page's own early start, the Editorial pages' refresh on every change) shares it rather than asking for the whole
	/// series again.
	/// </summary>
	private readonly Dictionary<Guid, (Task<IReadOnlyList<BaseItemDto>?> Answer, int Changes)> _asking = [];

	/// <summary>The series' episodes in play order; null when they could not be loaded.</summary>
	public async Task<IReadOnlyList<BaseItemDto>?> Get(Guid seriesId)
	{
		TaskCompletionSource<IReadOnlyList<BaseItemDto>?>? mine = null;
		Task<IReadOnlyList<BaseItemDto>?>? shared = null;
		// counted before asking: a change reported while the answer is on its way leaves it stale at once
		var changes = _changes();
		lock (_lock)
		{
			if (_entries.TryGetValue(seriesId, out var entry) && entry.Changes == changes && _time.GetElapsedTime(entry.ReadAt) < FreshFor)
			{
				return entry.Episodes;
			}

			if (_asking.TryGetValue(seriesId, out var asking) && asking.Changes == changes)
			{
				shared = asking.Answer;
			}
			else
			{
				mine = new TaskCompletionSource<IReadOnlyList<BaseItemDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
				_asking[seriesId] = (mine.Task, changes);
			}
		}

		if (shared is not null || mine is null)
		{
			return shared is null ? null : await shared;
		}

		IReadOnlyList<BaseItemDto>? episodes = null;
		try
		{
			var readAt = _time.GetTimestamp();
			var result = await _client.GetSeriesEpisodes(seriesId);
			if (result?.Items is { } items)
			{
				var list = items.ToList();
				episodes = list;
				lock (_lock)
				{
					_entries[seriesId] = new Entry(list, readAt, changes);
				}
			}

			return episodes;
		}
		finally
		{
			// those who shared it hear the same (null when it failed, as for a failed answer)
			mine.TrySetResult(episodes);
			lock (_lock)
			{
				if (_asking.TryGetValue(seriesId, out var asking) && asking.Answer == mine.Task)
				{
					_asking.Remove(seriesId);
				}
			}
		}
	}

	/// <summary>Whether the series' kept episodes include this item (an episode of it).</summary>
	public bool Contains(Guid seriesId, Guid itemId)
	{
		lock (_lock)
		{
			return _entries.TryGetValue(seriesId, out var entry) && entry.Episodes.Any(x => x.Id == itemId || x.SeasonId == itemId);
		}
	}

	/// <summary>A toggle the server took: the kept episodes follow its answer. The same change twice changes nothing more.</summary>
	public void Apply(ItemUserDataChangedEventArgs change)
	{
		// an Undo's series or season: its episodes came one by one (Partial), each as the server has it, so none is marked here
		if (change.Failed || change.Mixed)
		{
			return;
		}

		lock (_lock)
		{
			foreach (var (seriesId, entry) in _entries)
			{
				Patch(entry.Episodes, seriesId, change.ItemId, change.UserData);
			}
		}
	}

	/// <summary>
	/// The server's answer for <paramref name="itemId"/> applied to a series' episodes: an episode takes it as it is; marking a
	/// season or the series (un)played does the same to each of its episodes, as Jellyfin does (played or not, the resume point
	/// cleared). True when an episode changed.
	/// </summary>
	public static bool Patch(List<BaseItemDto> episodes, Guid seriesId, Guid itemId, UserItemDataDto data)
	{
		var changed = false;
		for (var i = 0; i < episodes.Count; i++)
		{
			var episode = episodes[i];
			if (episode.Id == itemId)
			{
				episode.UserData = data;
				changed = true;
			}
			else if (itemId == seriesId || episode.SeasonId == itemId)
			{
				var user = episode.UserData ??= new UserItemDataDto();
				user.Played = data.Played == true;
				user.PlaybackPositionTicks = 0;
				user.PlayedPercentage = null;
				changed = true;
			}
		}

		return changed;
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e) => Apply(e);

	public void Dispose()
	{
		if (_subscribed)
		{
			ItemUserData.Changed -= OnUserDataChanged;
		}
	}
}
