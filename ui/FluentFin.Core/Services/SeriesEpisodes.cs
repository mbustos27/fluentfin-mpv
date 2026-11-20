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

	// ---- Nixie 1.4.1 (season switching): a season's episodes and a series' seasons, as their pages list them -------------

	private sealed record ListEntry(BaseItemDtoQueryResult Answer, long ReadAt, (int Server, int Toggles) Version);

	private readonly Dictionary<Guid, ListEntry> _lists = [];
	private readonly Dictionary<Guid, (Task<BaseItemDtoQueryResult?> Answer, (int Server, int Toggles) Version)> _askingLists = [];

	/// <summary>
	/// Toggles made in the app (<see cref="Apply"/>): each one drops the kept lists, and an answer asked before it is not kept.
	/// A toggle elsewhere (an episode's own page, a library card, an Undo) changed episodes the kept lists also hold, and the
	/// server's own notice of it (the change count) comes later, or never without its websocket.
	/// </summary>
	private int _toggles;

	/// <summary>The most lists kept (each a season of a few dozen episodes): the oldest go first.</summary>
	internal const int MostLists = 64;

	private (int Server, int Toggles) ListVersion() => (_changes(), _toggles);

	/// <summary>
	/// A season's episodes (or a series' seasons) as <see cref="IJellyfinClient.GetItems"/> lists them, kept under the same
	/// rules as the series' episodes (the change count, <see cref="FreshFor"/>), and dropped by any toggle (<see cref="Apply"/>): a season seen
	/// again, or asked ahead (<see cref="Prefetch"/>), shows at once; every click asked the server again (300 ms or more on a
	/// real server), the series page's and the season pages' alike. A list asked again while on its way shares the request.
	/// <paramref name="fresh"/>: asked again whatever is kept (a page's own refresh after a change). Null when it failed
	/// (not kept).
	/// </summary>
	public async Task<BaseItemDtoQueryResult?> GetList(BaseItemDto parent, bool fresh = false)
	{
		if (parent.Id is not { } id)
		{
			return await _client.GetItems(parent);
		}

		TaskCompletionSource<BaseItemDtoQueryResult?>? mine = null;
		Task<BaseItemDtoQueryResult?>? shared = null;
		(int Server, int Toggles) version;
		long readAt;
		lock (_lock)
		{
			version = ListVersion();
			if (!fresh && _lists.TryGetValue(id, out var entry) && entry.Version == version && _time.GetElapsedTime(entry.ReadAt) < FreshFor)
			{
				return entry.Answer;
			}

			if (!fresh && _askingLists.TryGetValue(id, out var asking) && asking.Version == version)
			{
				shared = asking.Answer;
			}
			else
			{
				mine = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
				_askingLists[id] = (mine.Task, version);
			}

			readAt = _time.GetTimestamp();
		}

		if (shared is not null || mine is null)
		{
			return shared is null ? null : await shared;
		}

		BaseItemDtoQueryResult? answer = null;
		try
		{
			answer = await _client.GetItems(parent);
			if (answer?.Items is not null)
			{
				lock (_lock)
				{
					// kept only when nothing changed since it was asked (a toggle, the server's notice: it may predate them), and
					// not over a newer answer (two refreshes whose answers came the other way round)
					if (ListVersion() == version && (!_lists.TryGetValue(id, out var kept) || kept.ReadAt < readAt))
					{
						_lists[id] = new ListEntry(answer, readAt, version);
						Trim();
					}
				}
			}

			return answer;
		}
		finally
		{
			mine.TrySetResult(answer);
			lock (_lock)
			{
				if (_askingLists.TryGetValue(id, out var asking) && asking.Answer == mine.Task)
				{
					_askingLists.Remove(id);
				}
			}
		}
	}

	/// <summary>
	/// The lists of <paramref name="parents"/> asked now if not kept (the seasons either side of the one shown), so a click to
	/// one of them shows at once. Nothing is waited for; a failure is only not kept.
	/// </summary>
	public void Prefetch(IEnumerable<BaseItemDto> parents)
	{
		foreach (var parent in parents)
		{
			_ = GetList(parent);
		}
	}

	/// <summary>Whether a fresh list of <paramref name="parentId"/> is kept (tests).</summary>
	internal bool HasList(Guid parentId)
	{
		lock (_lock)
		{
			return _lists.TryGetValue(parentId, out var entry) && entry.Version == ListVersion() && _time.GetElapsedTime(entry.ReadAt) < FreshFor;
		}
	}

	/// <summary>How many lists are kept (tests).</summary>
	internal int ListCount
	{
		get
		{
			lock (_lock)
			{
				return _lists.Count;
			}
		}
	}

	// under _lock: the stale ones go, then the oldest, down to MostLists
	private void Trim()
	{
		if (_lists.Count <= MostLists)
		{
			return;
		}

		var version = ListVersion();
		foreach (var stale in _lists.Where(x => x.Value.Version != version || _time.GetElapsedTime(x.Value.ReadAt) >= FreshFor).Select(x => x.Key).ToList())
		{
			_lists.Remove(stale);
		}

		foreach (var oldest in _lists.OrderBy(x => x.Value.ReadAt).Take(Math.Max(0, _lists.Count - MostLists)).Select(x => x.Key).ToList())
		{
			_lists.Remove(oldest);
		}
	}

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
		if (change.Failed)
		{
			return;
		}

		// Nixie 1.4.1: any toggle the server took (an Undo's episodes too) drops the kept season and series lists: they may hold
		// a copy of what changed (a toggle on an episode's own page, a library card, another season), and an answer on its way
		// is not kept (it may predate the toggle). The next click asks again; the page shown asks its own season again.
		lock (_lock)
		{
			_toggles++;
			_lists.Clear();
		}

		// an Undo's series or season: its episodes came one by one (Partial), each as the server has it, so none is marked here
		if (change.Mixed)
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
