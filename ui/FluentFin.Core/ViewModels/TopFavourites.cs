using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

// 1.6 topbar

/// <summary>What a star did: added the library to the top bar, removed it, or nothing (the bar holds <see cref="TopFavourites.Max"/>).</summary>
public enum FavouriteChange
{
	Added,
	Removed,
	Full,
	NotALibrary,
}

/// <summary>
/// Nixie 1.6 (the top bar's favourites): which media libraries the bar shows, up to <see cref="Max"/>. The user's stars when
/// they have starred anything here; else the libraries they pinned in 1.3 (still stored, <see cref="TopNavigation.PinsSetting"/>);
/// else the Movies and Shows libraries by their kind, then the libraries opened most. Never just the server's order: a server
/// whose first libraries are "[HUB] Trending …" hubs would fill the bar with them.
/// </summary>
public static class TopFavourites
{
	public const int Max = 3;

	/// <summary>
	/// The favourites shown, in order: <paramref name="chosen"/> (the user's stars, null when they never starred here), else
	/// <paramref name="pins"/>, else the defaults (<see cref="Defaults"/>). Only media libraries the server lists now: a star
	/// or a pin to a library that went is dropped quietly.
	/// </summary>
	public static IReadOnlyList<Guid> Pick(IReadOnlyList<BaseItemDto> views, IReadOnlyList<Guid>? chosen, IReadOnlyList<Guid> pins, IReadOnlyDictionary<Guid, int> opens)
	{
		var media = views.Where(IsMedia).Select(v => v.Id!.Value).ToHashSet();
		if (chosen is not null)
		{
			return Existing(chosen, media);
		}

		var pinned = Existing(pins, media);
		return pinned.Count > 0 ? pinned : Defaults(views, opens);
	}

	private static List<Guid> Existing(IEnumerable<Guid> ids, HashSet<Guid> media) => [.. ids.Where(media.Contains).Distinct().Take(Max)];

	/// <summary>
	/// With no stars and no pins: the first Movies library and the first Shows library (by kind, a hub's name skipped while
	/// another of the kind has a plain one), then the libraries opened most (at least once), most first.
	/// </summary>
	public static IReadOnlyList<Guid> Defaults(IReadOnlyList<BaseItemDto> views, IReadOnlyDictionary<Guid, int> opens)
	{
		var media = views.Where(IsMedia).ToList();
		List<Guid> picked = [];
		foreach (var kind in new[] { BaseItemDto_CollectionType.Movies, BaseItemDto_CollectionType.Tvshows })
		{
			var ofKind = media.Where(v => v.CollectionType == kind).ToList();
			if ((ofKind.FirstOrDefault(v => !IsTagged(v.Name)) ?? ofKind.FirstOrDefault()) is { Id: { } id })
			{
				picked.Add(id);
			}
		}

		var order = media.Select((v, i) => (v.Id!.Value, i)).ToDictionary(p => p.Item1, p => p.i);
		foreach (var (id, _) in opens.Where(o => o.Value > 0 && order.ContainsKey(o.Key) && !picked.Contains(o.Key))
			.OrderByDescending(o => o.Value).ThenBy(o => order[o.Key]))
		{
			if (picked.Count >= Max)
			{
				break;
			}

			picked.Add(id);
		}

		return picked;
	}

	/// <summary>
	/// A name that starts with a bracketed tag ("[HUB] Trending Movies", "{4K} Films"): an admin's hub or a copy of a library,
	/// not the library to start the bar with. Only the default skips these; a star or a pin on one is kept.
	/// </summary>
	public static bool IsTagged(string? name)
	{
		var trimmed = name?.TrimStart() ?? "";
		if (trimmed.Length < 3 || trimmed[0] is not ('[' or '{'))
		{
			return false;
		}

		var close = trimmed.IndexOf(trimmed[0] == '[' ? ']' : '}', 1);
		return close > 1 && close < trimmed.Length - 1;
	}

	/// <summary>A view that is a library of media (not the server's Collections or Playlists).</summary>
	public static bool IsMedia(BaseItemDto view) => view.Id is not null && !TopTabs.IsCollectionView(view);

	/// <summary>
	/// A star pressed on <paramref name="id"/>: the favourites after it, and what happened. A fourth is refused (<see cref="FavouriteChange.Full"/>):
	/// the user is told the bar holds three, and removes one first.
	/// </summary>
	public static (IReadOnlyList<Guid> Favourites, FavouriteChange Change) Toggle(IReadOnlyList<Guid> current, Guid id)
	{
		if (current.Contains(id))
		{
			return ([.. current.Where(x => x != id)], FavouriteChange.Removed);
		}

		return current.Count >= Max ? (current, FavouriteChange.Full) : ([.. current, id], FavouriteChange.Added);
	}

	/// <summary>The star's name for Narrator and its tooltip: "Add Movies to the top bar", "Remove Movies from the top bar".</summary>
	public static string StarName(string? library, bool favourite)
	{
		var name = string.IsNullOrWhiteSpace(library) ? "this library" : library.Trim();
		return favourite ? $"Remove {name} from the top bar" : $"Add {name} to the top bar";
	}

	/// <summary>The message when the bar is full: "Up to 3 favourites in the top bar. Remove a star to add Anime."</summary>
	public static string FullMessage(string? library) =>
		$"Up to {Max} favourites in the top bar. Remove a star to add {(string.IsNullOrWhiteSpace(library) ? "this library" : library.Trim())}.";
}

/// <summary>Nixie 1.6: one user's top bar on one server: their stars (null until they star anything) and how often they opened each library.</summary>
public sealed class FavouritesRecord
{
	public List<Guid>? Chosen { get; set; }

	public Dictionary<Guid, int> Opens { get; set; } = [];
}

/// <summary>
/// Nixie 1.6: the favourites and the opens, per server and user, in the settings file (<see cref="SettingKey"/>: an object of
/// records keyed by <see cref="ProfileKey"/>). Read without writing (a store that says nothing stays so), and a record that
/// cannot be read is a fresh one.
/// </summary>
public sealed class FavouritesStore(ILocalSettingsService settings)
{
	public const string SettingKey = "TopBarFavourites";

	private static readonly JsonSerializerOptions Options = new();

	/// <summary>The signed-in server and user ("server|user"); null before sign-in.</summary>
	public static string? ProfileKey()
	{
		if (SessionInfo.CurrentUser?.Id is not { } user || user == Guid.Empty)
		{
			return null;
		}

		var server = string.IsNullOrWhiteSpace(SessionInfo.ServerId) ? SessionInfo.BaseUrl?.TrimEnd('/') : SessionInfo.ServerId;
		return string.IsNullOrWhiteSpace(server) ? null : $"{server}|{user:N}";
	}

	public FavouritesRecord Read(string? profile)
	{
		if (profile is null)
		{
			return new();
		}

		try
		{
			return settings.ReadSettingRaw(SettingKey) is JsonObject all && all[profile] is JsonNode node
				? node.Deserialize<FavouritesRecord>(Options) ?? new()
				: new();
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
		{
			return new();
		}
	}

	public void Save(string? profile, FavouritesRecord record)
	{
		if (profile is null)
		{
			return;
		}

		JsonObject all;
		try
		{
			all = settings.ReadSettingRaw(SettingKey) is JsonObject read ? (JsonObject)read.DeepClone() : [];
		}
		catch (InvalidOperationException)
		{
			all = [];
		}

		all[profile] = JsonSerializer.SerializeToNode(record, Options);
		settings.SaveSetting(SettingKey, all);
	}

	/// <summary>The libraries pinned in 1.3 ("Add to the top bar"), in their order: read, never written.</summary>
	public IReadOnlyList<Guid> Pins()
	{
		try
		{
			return settings.ReadSettingRaw(TopNavigation.PinsSetting)?.Deserialize<List<CustomNavigationViewItem>>(Options) is { } items
				? [.. items.Where(x => x.Key == TopTabs.LibraryKey && x.Parameter is not null).Select(x => x.Parameter!.Value)]
				: [];
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
		{
			return [];
		}
	}
}

// 1.6 integration: topbar's CollectionsSection (All, BoxSets, Playlists) is the collections stream's (CollectionsViewModel.cs:
// All, Server, Playlists, MadeByNixie, Saved), and its interim CollectionsRoute (the server's box-set view, else the
// Libraries page lit as Collections) is retired: GlobalCommands.OpenCollections opens the Collections page.

// end 1.6 topbar
