using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6 (plan 4.1, 4.4): what the user keeps of Nixie's collections, on this PC, per server and user: the library
/// filters saved under a name ("Save as collection") and the Nixie collections pinned with Favourite (a Nixie collection is
/// not a server item, so its Favourite is kept here). Forgotten with the user's other files on sign-out.
/// </summary>
public sealed class SmartCollectionStore : Builders.ISpotlightPicks, ICategoryWeeks
{
	private const int FormatVersion = 1;

	/// <summary>
	/// 1.9 (the rebuild, before b3): the lineup's revision the weeks kept on this PC were chosen by. The weeks' categories and
	/// Spotlight picks of a file without it, or with an older one (1.9's b2 and before, 1.8's picks), are dropped once at its
	/// first read: a kept week holds to its Sunday, and b2's would have shown the old row and Spotlight on b3 until Monday.
	/// The file is written with it at once, so the reset is once, not each start. Raise it when the lineup's choice changes
	/// what a kept week should hold.
	/// </summary>
	public const int WeeksRevision = 2;

	private readonly IJellyfinClient _client;
	private readonly HomeCache _homeCache;
	private readonly UserFiles _files;
	private readonly ILogger _logger;
	private readonly SemaphoreSlim _lock = new(1, 1);
	private string? _key;
	private State _state = new([], []);

	private sealed record Saved(string Id, string Name, SavedFilter Filter);

	private sealed record State(List<Saved> Filters, HashSet<string> Favorites)
	{
		// 1.8 (stream s; the review's Minor 3): the week's Spotlight by ISO week ("2026-W48" → its id), the newest weeks only
		public Dictionary<string, string> Spotlight { get; init; } = new(StringComparer.Ordinal);

		// 1.8 (stream l, BH-180-m1): a pinned Nixie collection's title (its id → title), so a pin that is no longer made (last
		// week's Spotlight, a pick from what the user watched that moved on) still shows in Saved by name, said to have ended
		public Dictionary<string, string> PinnedTitles { get; init; } = new(StringComparer.Ordinal);

		// 1.9 (Collections 2, stream lineup): the categories each ISO week showed ("2026-W48" → their ids, the explore pick), the
		// newest weeks only
		public Dictionary<string, CategoryWeek> Categories { get; init; } = new(StringComparer.Ordinal);
	}

	public SmartCollectionStore(IJellyfinClient client, HomeCache homeCache, KnownFolders folders, ILogger<SmartCollectionStore> logger)
		: this(client, homeCache, Path.Combine(folders.ApplicationData, "SmartCollections"), logger)
	{
	}

	internal SmartCollectionStore(IJellyfinClient client, HomeCache homeCache, string folder, ILogger logger)
	{
		_client = client;
		_homeCache = homeCache;
		_logger = logger;
		_files = new UserFiles(folder, homeCache, logger);
		_files.Deleted += () => _key = null;
	}

	/// <summary>A filter was saved or removed, or a favourite changed.</summary>
	public event EventHandler? Changed;

	private static readonly JsonSerializerOptions Json = new();

	private async Task<State> Load()
	{
		var key = HomeCache.FileKey(_client.BaseUrl, _client.UserId);
		if (_key == key)
		{
			return _state;
		}

		var state = new State([], []);
		var began = _files.Generation; // 1.9 (the rebuild): the once-only reset's write, refused after a sign-out during the read
		var (stale, dropped) = (false, false);
		if (await _files.Read(key).ConfigureAwait(false) is { } root && root["Version"]?.GetValue<int>() == FormatVersion)
		{
			try
			{
				state = new State(root["Filters"].Deserialize<List<Saved>>(Json) ?? [], root["Favorites"].Deserialize<HashSet<string>>(Json) ?? [])
				{
					Spotlight = root["Spotlight"]?.Deserialize<Dictionary<string, string>>(Json) ?? new(StringComparer.Ordinal), // 1.8
					PinnedTitles = root["PinnedTitles"]?.Deserialize<Dictionary<string, string>>(Json) ?? new(StringComparer.Ordinal), // 1.8 (BH-180-m1)
				};
			}
			catch (Exception ex)
			{
				_logger.LogInformation(ex, "Ignoring the saved collections");
			}

			// 1.9 (the rebuild): weeks chosen by an older lineup are dropped, once (WeeksRevision)
			var revision = root["WeeksRevision"] is JsonValue stamp && stamp.TryGetValue<int>(out var r) ? r : 0;
			if (revision < WeeksRevision)
			{
				dropped = state.Spotlight.Count > 0 || root["Categories"] is JsonObject { Count: > 0 };
				state.Spotlight.Clear();
				_logger.LogInformation("The saved weeks were chosen before this version's lineup (revision {Old} of {New}): {What}", revision, WeeksRevision,
					dropped ? "dropped once" : "none to drop");
				stale = true;
			}

			// 1.9: the categories' weeks apart: a damaged history costs the history only, never the saved filters and pins
			try
			{
				if (!stale && root["Categories"]?.Deserialize<Dictionary<string, CategoryWeek>>(Json) is { } weeks)
				{
					foreach (var (week, shown) in weeks)
					{
						if (shown?.Ids is { } ids)
						{
							state.Categories[week] = shown with { Ids = [.. ids.Where(x => !string.IsNullOrEmpty(x))] };
						}
					}
				}
			}
			catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
			{
				_logger.LogInformation("Ignoring the categories' saved weeks ({Error})", ex.GetType().Name);
			}
		}

		(_key, _state) = (key, state);
		if (stale)
		{
			await Save(state, changed: false, since: began).ConfigureAwait(false); // stamped now: the reset is once
		}

		return state;
	}

	/// <param name="changed">1.8: false for the week's Spotlight pick (nothing the pages list changed).</param>
	/// <param name="since">1.9 (the review's Major 1): the sign-outs' number the data was read after (<see cref="UserFiles.Generation"/>); null: written whatever came since.</param>
	private async Task Save(State state, bool changed = true, long? since = null)
	{
		var root = new JsonObject
		{
			["Version"] = FormatVersion,
			["Filters"] = JsonSerializer.SerializeToNode(state.Filters, Json),
			["Favorites"] = JsonSerializer.SerializeToNode(state.Favorites, Json),
			["Spotlight"] = JsonSerializer.SerializeToNode(state.Spotlight, Json), // 1.8
			["PinnedTitles"] = JsonSerializer.SerializeToNode(state.PinnedTitles, Json), // 1.8 (BH-180-m1)
			["Categories"] = JsonSerializer.SerializeToNode(state.Categories, Json), // 1.9
			["WeeksRevision"] = WeeksRevision, // 1.9 (the rebuild)
		};
		await _files.Write(_key!, root, _homeCache.LoginKey(_client.BaseUrl, _client.UserId), since).ConfigureAwait(false);
		if (changed)
		{
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}

	// 1.8 (stream s; the review's Minor 3)
	/// <summary>How many weeks' Spotlight picks are kept.</summary>
	public const int SpotlightWeeksKept = 8;

	/// <summary>
	/// 1.8: the Spotlight picked each week, on this PC (by ISO week, "2026-W48"); forgotten with the user's files on sign-out.
	/// 1.8.1 (BH-180-q1): of <paramref name="of"/>'s server and user only (an index's key): none when another sign-in is now.
	/// </summary>
	public async Task<IReadOnlyDictionary<string, string>> SpotlightPicksAsync(string? of = null)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var began = _files.Generation; // 1.9 (the review's Major 1): before the read
			var state = await Load().ConfigureAwait(false);
			if (of is null || of == _key)
			{
				_picksRead[_key!] = began;
			}

			return of is not null && of != _key ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(state.Spotlight, StringComparer.Ordinal);
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>
	/// 1.8: the week's Spotlight, kept so it holds all week and the next week's is another; the newest weeks only. 1.8.1
	/// (BH-180-q1): for <paramref name="of"/>'s server and user only: a Switch user during the Spotlight's build (0.2 to 0.7 s)
	/// saved user A's pick as user B's week; it is A's to make again at A's next visit.
	/// </summary>
	public async Task SaveSpotlightPick(string week, string id, string? of = null)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var began = _files.Generation;
			var state = await Load().ConfigureAwait(false);
			if (of is not null && of != _key)
			{
				return; // another sign-in's store now
			}

			// 1.9 (the review's Major 1, as the categories' week): not kept once the user signed out after the picks were read
			var since = _picksRead.TryGetValue(_key!, out var read) ? read : began;
			if (SignedOutSince(since))
			{
				return;
			}

			if (state.Spotlight.TryGetValue(week, out var was) && was == id)
			{
				return;
			}

			state.Spotlight[week] = id;
			foreach (var old in state.Spotlight.Keys.OrderByDescending(x => x, StringComparer.Ordinal).Skip(SpotlightWeeksKept).ToList())
			{
				state.Spotlight.Remove(old);
			}

			await Save(state, changed: false, since: since).ConfigureAwait(false);
		}
		finally
		{
			_lock.Release();
		}
	}
	// 1.9 (the review's Major 1): the sign-outs' number each key's Spotlight picks were last read after
	private readonly Dictionary<string, long> _picksRead = new(StringComparer.Ordinal);
	// end 1.8

	// 1.9 (Collections 2, stream lineup)
	/// <summary>How many weeks of the categories shown are kept (the lineup reads the last four).</summary>
	public const int CategoryWeeksKept = 8;

	/// <summary>
	/// 1.9: the categories shown each week, on this PC (by ISO week, "2026-W48"), of <paramref name="of"/>'s server and user only
	/// (an index's key: none when another sign-in is now); forgotten with the user's files on sign-out. The review's Major 1:
	/// what it returns says when it was read (<see cref="KeptCategoryWeeks.ReadAfter"/>), and the read is noted for the key, so
	/// the week kept after it is not written once the user signed out meanwhile (1.6.1's guard).
	/// </summary>
	public async Task<IReadOnlyDictionary<string, CategoryWeek>> CategoryWeeksAsync(string? of = null)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var began = _files.Generation; // before the read: a sign-out during it counts
			var state = await Load().ConfigureAwait(false);
			if (of is not null && of != _key)
			{
				return new KeptCategoryWeeks(began);
			}

			_weeksRead[_key!] = began;
			var weeks = new KeptCategoryWeeks(began);
			foreach (var (week, shown) in state.Categories)
			{
				weeks[week] = shown;
			}

			return weeks;
		}
		finally
		{
			_lock.Release();
		}
	}

	// 1.9 (the review's Major 1): the sign-outs' number each key's weeks were last read after
	private readonly Dictionary<string, long> _weeksRead = new(StringComparer.Ordinal);

	/// <summary>
	/// 1.9: the categories <paramref name="week"/> shows, kept so the week holds them and the next weeks leave them out; the newest
	/// <see cref="CategoryWeeksKept"/> weeks only; nothing written when the week already holds the same. For <paramref name="of"/>'s
	/// server and user only (BH-180-q1's race: a Switch user during the build). The review's Major 1: not written when the user
	/// signed out after the weeks were read (<paramref name="since"/>, else the key's last read, else now): the build's save
	/// brought the signed-out user's file back.
	/// </summary>
	public async Task SaveCategoryWeek(string week, CategoryWeek shown, string? of = null, long? since = null)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var began = _files.Generation;
			var state = await Load().ConfigureAwait(false);
			if (of is not null && of != _key)
			{
				return; // another sign-in's store now
			}

			var after = since ?? (_weeksRead.TryGetValue(_key!, out var read) ? read : began);
			if (SignedOutSince(after))
			{
				return;
			}

			if (state.Categories.TryGetValue(week, out var was) && was.Ids.SequenceEqual(shown.Ids, StringComparer.Ordinal) && was.Explore == shown.Explore)
			{
				return;
			}

			state.Categories[week] = shown with { Ids = [.. shown.Ids] };
			foreach (var old in state.Categories.Keys.OrderByDescending(x => x, StringComparer.Ordinal).Skip(CategoryWeeksKept).ToList())
			{
				state.Categories.Remove(old);
			}

			await Save(state, changed: false, since: after).ConfigureAwait(false);
		}
		finally
		{
			_lock.Release();
		}
	}

	// 1.9 (the review's Major 1): whether the user signed out (or their login was removed) after <since>: then nothing is kept,
	// and nothing of it stays in memory either (the next read reads the disk: a sign-in again sees what was written)
	private bool SignedOutSince(long since)
	{
		if (!_files.ForgottenSince(_key!, _homeCache.LoginKey(_client.BaseUrl, _client.UserId), since))
		{
			return false;
		}

		_key = null;
		return true;
	}
	// end 1.9

	/// <summary>The saved filters, as collections (their titles are found when one is opened).</summary>
	public async Task<IReadOnlyList<SmartCollection>> SavedAsync()
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			return [.. (await Load().ConfigureAwait(false)).Filters.Select(ToCollection)];
		}
		finally
		{
			_lock.Release();
		}
	}

	// the fix round's re-review: a filter's revision is its own or its id's, whichever is newer. 1.7.2 rewrites the file with
	// the fields it knows (a filter saved by 1.7.3 lost its Revision at a 1.7.2 save, and back on 1.7.3 took 1.7.2's rules),
	// and it keeps the id as it is
	private static SmartCollection ToCollection(Saved saved) =>
		new(saved.Id, SmartCollectionKind.Saved, saved.Name, $"Your filter in {saved.Filter.LibraryName}: {saved.Filter.Describe()}", HasOwnOrder: false, [])
		{
			Filter = saved.Filter with { Revision = Math.Max(saved.Filter.Revision, SavedFilter.RevisionOfId(saved.Id)) },
		};

	/// <summary>
	/// "Save as collection" (the library's applied-filter row): <paramref name="filter"/> kept under <paramref name="name"/>
	/// (trimmed; the filter's own words when empty). The same name in the same library replaces the earlier one.
	/// </summary>
	public async Task<SmartCollection> SaveFilter(string name, SavedFilter filter)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var state = await Load().ConfigureAwait(false);
			filter = filter with { Revision = SavedFilter.CurrentRevision }; // fix round (BH-173-M2): its preset means what it means now
			var title = string.IsNullOrWhiteSpace(name) ? filter.Describe() : name.Trim();
			state.Filters.RemoveAll(x => x.Filter.LibraryId == filter.LibraryId && string.Equals(x.Name, title, StringComparison.CurrentCultureIgnoreCase));
			var saved = new Saved(SavedFilter.NewId(), title, filter); // the re-review: its id says its revision too (1.7.2 keeps ids)
			state.Filters.Add(saved);
			await Save(state).ConfigureAwait(false);
			return ToCollection(saved);
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>"Save as collection" with the library page's own filter model (colpage's button calls this).</summary>
	public Task<SmartCollection> SaveFilter(string name, BaseItemDto library, LibraryFilter filter) =>
		SaveFilter(name, new SavedFilter(library.Id ?? Guid.Empty, CollectionNames.DisplayLibrary(library.Name), filter.Unwatched, filter.Favorites,
			[.. filter.Genres], [.. filter.Tags], [.. filter.OfficialRatings], [.. filter.Years]));

	/// <summary>A saved filter removed (its page's Remove); false when there was none.</summary>
	public async Task<bool> RemoveSaved(string id)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var state = await Load().ConfigureAwait(false);
			var removed = state.Filters.RemoveAll(x => x.Id == id) > 0;
			state.Favorites.Remove(id);
			if (removed)
			{
				await Save(state).ConfigureAwait(false);
			}

			return removed;
		}
		finally
		{
			_lock.Release();
		}
	}

	// 1.7.5 (fx1; M2's review of 1.7.3)
	/// <summary>
	/// The pins whose id <paramref name="gone"/> names, and the saved filters whose preset's id it names, taken out in one call
	/// under the store's lock, the file written once; how many went (a saved filter's own pin goes with it, uncounted, as
	/// <see cref="RemoveSaved"/> does). A second call at the same time finds them gone and says 0: Home's start-up call and the
	/// Collections page's could run together on a first visit, each counted the same pins, and the file was written once a pin.
	/// </summary>
	public async Task<int> RemoveWhere(Func<string?, bool> gone)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var state = await Load().ConfigureAwait(false);
			var pins = state.Favorites.RemoveWhere(x => gone(x));
			var filters = state.Filters.Where(x => gone(x.Filter?.PresetId)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
			state.Filters.RemoveAll(x => filters.Contains(x.Id));
			state.Favorites.ExceptWith(filters);
			if (pins + filters.Count > 0)
			{
				await Save(state).ConfigureAwait(false);
			}

			return pins + filters.Count;
		}
		finally
		{
			_lock.Release();
		}
	}
	// end 1.7.5

	/// <summary>The Nixie collections pinned with Favourite (their ids).</summary>
	public async Task<IReadOnlySet<string>> FavoritesAsync()
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			return (await Load().ConfigureAwait(false)).Favorites.ToHashSet();
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <param name="title">1.8 (BH-180-m1): a Nixie collection's title, kept with its pin (Saved names it once it is no longer made).</param>
	public async Task SetFavorite(string collectionId, bool favorite, string? title = null)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var state = await Load().ConfigureAwait(false);
			var titled = favorite && title is { Length: > 0 } && (!state.PinnedTitles.TryGetValue(collectionId, out var was) || was != title);
			if (favorite && titled)
			{
				state.PinnedTitles[collectionId] = title!;
			}
			else if (!favorite)
			{
				state.PinnedTitles.Remove(collectionId);
			}

			if ((favorite ? state.Favorites.Add(collectionId) : state.Favorites.Remove(collectionId)) || titled)
			{
				await Save(state).ConfigureAwait(false);
			}
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>1.8 (BH-180-m1): the titles kept with the pins (a pin made before 1.8's fix has none).</summary>
	public async Task<IReadOnlyDictionary<string, string>> PinnedTitlesAsync()
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			return new Dictionary<string, string>((await Load().ConfigureAwait(false)).PinnedTitles, StringComparer.Ordinal);
		}
		finally
		{
			_lock.Release();
		}
	}
}
