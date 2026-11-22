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
public sealed class SmartCollectionStore
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

	private sealed record State(List<Saved> Filters, HashSet<string> Favorites);

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
				state = new State(root["Filters"].Deserialize<List<Saved>>(Json) ?? [], root["Favorites"].Deserialize<HashSet<string>>(Json) ?? []);
			}
			catch (Exception ex)
			{
				_logger.LogInformation(ex, "Ignoring the saved collections");
			}
		}

		(_key, _state) = (key, state);
		return state;
	}

	private async Task Save(State state)
	{
		var root = new JsonObject
		{
			["Version"] = FormatVersion,
			["Filters"] = JsonSerializer.SerializeToNode(state.Filters, Json),
			["Favorites"] = JsonSerializer.SerializeToNode(state.Favorites, Json),
		};
		await _files.Write(_key!, root, _homeCache.LoginKey(_client.BaseUrl, _client.UserId)).ConfigureAwait(false);
		Changed?.Invoke(this, EventArgs.Empty);
	}

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

	private static SmartCollection ToCollection(Saved saved) =>
		new(saved.Id, SmartCollectionKind.Saved, saved.Name, $"Your filter in {saved.Filter.LibraryName}: {saved.Filter.Describe()}", HasOwnOrder: false, [])
		{
			Filter = saved.Filter,
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
			var title = string.IsNullOrWhiteSpace(name) ? filter.Describe() : name.Trim();
			state.Filters.RemoveAll(x => x.Filter.LibraryId == filter.LibraryId && string.Equals(x.Name, title, StringComparison.CurrentCultureIgnoreCase));
			var saved = new Saved($"saved:{Guid.NewGuid():N}", title, filter);
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

	public async Task SetFavorite(string collectionId, bool favorite)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var state = await Load().ConfigureAwait(false);
			if (favorite ? state.Favorites.Add(collectionId) : state.Favorites.Remove(collectionId))
			{
				await Save(state).ConfigureAwait(false);
			}
		}
		finally
		{
			_lock.Release();
		}
	}
}
