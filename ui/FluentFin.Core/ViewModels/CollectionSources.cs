using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.6 (plan 4.3): where a Collection page's titles come from. The page logic stays shared; only the load and the
/// header's facts differ: a server's box set or playlist (<see cref="ServerCollection"/>, the 1.4.0 code) or a collection
/// Nixie made (<see cref="NixieCollection"/>).
/// </summary>
public interface ICollectionSource
{
	/// <summary>The title as shown.</summary>
	string Title { get; }

	/// <summary>Over the title: "Collection", "Playlist", "Made by Nixie · Franchise".</summary>
	string Eyebrow { get; }

	/// <summary>The line under the summary saying where it came from ("" for the server's own).</summary>
	string Why { get; }

	/// <summary>Its order means something (a franchise's release order): its page numbers the cards.</summary>
	bool HasOwnOrder { get; }

	/// <summary>Its titles in its order; null when the server could not be read.</summary>
	Task<IReadOnlyList<BaseItemDto>?> LoadItems(IJellyfinClient client, CancellationToken cancellationToken = default);
}

/// <summary>A box set or a playlist on the server: the 1.4.0 Collection page's own load.</summary>
public sealed record ServerCollection(BaseItemDto Set) : ICollectionSource
{
	public string Title => CollectionNames.Display(Set);

	public string Eyebrow => CollectionBrowse.KindOf(Set);

	public string Why => "";

	public bool HasOwnOrder => true;

	public async Task<IReadOnlyList<BaseItemDto>?> LoadItems(IJellyfinClient client, CancellationToken cancellationToken = default)
	{
		var result = Set.Type == BaseItemDto_Type.Playlist && Set.Id is { } id ? await client.GetPlaylistItems(id) : await client.GetItems(Set);
		return result?.Items;
	}
}

/// <summary>
/// A collection Nixie made (<see cref="SmartCollection"/>): its titles by id from the server (fresh watch state), in the
/// rule's order; a saved filter reads its library page by page and keeps what passes, the way the library page filters.
/// </summary>
public sealed record NixieCollection(SmartCollection Collection) : ICollectionSource
{
	public string Title => Collection.Title;

	public string Eyebrow => Collection.Eyebrow;

	public string Why => Collection.Why;

	public bool HasOwnOrder => Collection.HasOwnOrder;

	/// <summary>
	/// The item record the page shows for it (its title, no server id: nothing on it is sent to the server; Favourite is
	/// kept on this PC, <paramref name="favorite"/>).
	/// </summary>
	public BaseItemDto AsItem(bool favorite) => new()
	{
		Name = Collection.Title,
		Type = BaseItemDto_Type.BoxSet,
		DisplayOrder = Collection.HasOwnOrder ? "PremiereDate" : "SortName",
		UserData = new UserItemDataDto { IsFavorite = favorite },
	};

	public async Task<IReadOnlyList<BaseItemDto>?> LoadItems(IJellyfinClient client, CancellationToken cancellationToken = default)
	{
		if (Collection.Filter is { } filter)
		{
			return await Filtered(client, filter, cancellationToken);
		}

		return Collection.ItemIds.Count == 0 ? [] : await client.GetItemsByIds(Collection.ItemIds, cancellationToken);
	}

	private static async Task<IReadOnlyList<BaseItemDto>?> Filtered(IJellyfinClient client, SavedFilter saved, CancellationToken cancellationToken)
	{
		var library = (await client.GetUserViews()).FirstOrDefault(x => x.Id == saved.LibraryId);
		if (library is null)
		{
			return null;
		}

		var reader = new LibraryPageReader(client, library);
		if (await reader.ReadAllAsync(cancellationToken) is not { } all)
		{
			return null;
		}

		var filter = new LibraryFilter { Unwatched = saved.Unwatched, Favorites = saved.Favorites };
		foreach (var (list, into) in new[] { (saved.Genres, filter.Genres), (saved.Tags, filter.Tags), (saved.OfficialRatings, filter.OfficialRatings), (saved.Years, filter.Years) })
		{
			foreach (var value in list ?? [])
			{
				into.Add(value);
			}
		}

		// a Nixie preset was on too ("Heists" in Movies): its titles only, as the collection is now
		var preset = saved.PresetId is { } id ? await PresetItems(id, saved.LibraryId) : null;
		return [.. all.Where(x => filter.IsVisible(BaseItemViewModel.FromDto(x)) && (preset is null || x.Id is { } item && preset.Contains(item)))];
	}

	// the titles of the Nixie collection a saved filter's preset was ("nixie:franchise:2344"), from the library index; an empty
	// set when that collection is no longer made (the saved filter then keeps nothing, rather than everything)
	private static async Task<HashSet<Guid>> PresetItems(string presetId, Guid libraryId)
	{
		if (SmartCollectionServices.Index is not { } library || await library.GetAsync() is not { } index)
		{
			return [];
		}

		var id = presetId.StartsWith(NixiePreset.IdPrefix, StringComparison.Ordinal) ? presetId[NixiePreset.IdPrefix.Length..] : presetId;
		var own = index with { Entries = [.. index.Entries.Where(x => x.LibraryId == libraryId)] };
		return (await SmartCollectionCatalog.BuildAllAsync(own, DateTimeOffset.Now)).FirstOrDefault(x => x.Id == id)?.ItemIds.ToHashSet() ?? [];
	}
}
