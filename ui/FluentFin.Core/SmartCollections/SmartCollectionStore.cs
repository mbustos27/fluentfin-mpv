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
public sealed class SmartCollectionStore : Builders.ISpotlightPicks
{
	private const int FormatVersion = 1;

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
		}

		(_key, _state) = (key, state);
		return state;
	}

	/// <param name="changed">1.8: false for the week's Spotlight pick (nothing the pages list changed).</param>
	private async Task Save(State state, bool changed = true)
	{
		var root = new JsonObject
		{
			["Version"] = FormatVersion,
			["Filters"] = JsonSerializer.SerializeToNode(state.Filters, Json),
			["Favorites"] = JsonSerializer.SerializeToNode(state.Favorites, Json),
			["Spotlight"] = JsonSerializer.SerializeToNode(state.Spotlight, Json), // 1.8
			["PinnedTitles"] = JsonSerializer.SerializeToNode(state.PinnedTitles, Json), // 1.8 (BH-180-m1)
		};
		await _files.Write(_key!, root, _homeCache.LoginKey(_client.BaseUrl, _client.UserId)).ConfigureAwait(false);
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
			var state = await Load().ConfigureAwait(false);
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
			var state = await Load().ConfigureAwait(false);
			if (of is not null && of != _key)
			{
				return; // another sign-in's store now
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

			await Save(state, changed: false).ConfigureAwait(false);
		}
		finally
		{
			_lock.Release();
		}
	}
	// end 1.8

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
