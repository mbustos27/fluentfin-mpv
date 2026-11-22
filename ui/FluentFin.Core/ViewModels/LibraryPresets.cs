using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>1.6 colpage (LB2, LB3, LB5): what a library preset is.</summary>
public enum LibraryPresetKind
{
	/// <summary>Every title: no filter (and Recently added's sort undone).</summary>
	All,

	/// <summary>The twin of the Unwatched filter: lit whenever it is on.</summary>
	Unwatched,

	/// <summary>The twin of the Favorites filter.</summary>
	Favorites,

	/// <summary>The sort by date added, newest first.</summary>
	RecentlyAdded,

	/// <summary>A Nixie collection in this library (the collections stream's): its own titles.</summary>
	Collection,
}

/// <summary>A Nixie collection's titles in this library, as a filter: its id, the words its applied-filter chip shows, its titles.</summary>
public sealed record LibraryPresetItems(string Id, string Label, IReadOnlySet<Guid> Items);

/// <summary>What a preset applies: a switch, the sort, or a set of titles (a Nixie collection may also turn Unwatched on).</summary>
public sealed record LibraryPresetFilter
{
	public bool Unwatched { get; init; }

	public bool Favorites { get; init; }

	/// <summary>Date added, newest first.</summary>
	public bool RecentlyAdded { get; init; }

	public LibraryPresetItems? Items { get; init; }
}

/// <summary>
/// 1.6 colpage (LB2, LB3, LB5): one preset of a library's strip, in every theme: an id (stable: "all", "unwatched",
/// "nixie:franchise:mummy"), the label the strip shows, the name Narrator hears, and the filter it applies. The strip shows
/// All · Unwatched · Favorites · Recently added, then the library's Nixie collections (<see cref="ILibraryPresetSource"/>).
/// Themes own the look (Editorial's chips, Cinematic's tabs, Drive-in's letter-board tiles, Classic's chips).
/// </summary>
public sealed record LibraryPreset(string Id, string Label, string Name, LibraryPresetKind Kind, LibraryPresetFilter Filter)
{
	public static LibraryPreset All { get; } = new("all", "All", "All titles", LibraryPresetKind.All, new());

	public static LibraryPreset Unwatched { get; } = new("unwatched", "Unwatched", "Unwatched", LibraryPresetKind.Unwatched, new() { Unwatched = true });

	public static LibraryPreset Favorites { get; } = new("favorites", "Favorites", "Favorites", LibraryPresetKind.Favorites, new() { Favorites = true });

	public static LibraryPreset RecentlyAdded { get; } = new("recent", "Recently added", "Recently added, newest first", LibraryPresetKind.RecentlyAdded, new() { RecentlyAdded = true });

	/// <summary>The four every library has, in the strip's order.</summary>
	public static IReadOnlyList<LibraryPreset> Builtin { get; } = [All, Unwatched, Favorites, RecentlyAdded];

	/// <summary>A Nixie collection as a preset ("Heist", "The 1990s"): its titles in this library.</summary>
	public static LibraryPreset ForCollection(string id, string label, IReadOnlySet<Guid> items, string? name = null) =>
		new(id, label, name ?? $"{label}, a Nixie collection", LibraryPresetKind.Collection, new() { Items = new LibraryPresetItems(id, label, items) });
}

/// <summary>A preset as the strip shows it now: lit or not, and its Narrator name with what it holds ("Unwatched, 223 titles").</summary>
public sealed record LibraryPresetState(LibraryPreset Preset, bool IsOn, string Name)
{
	public string Id => Preset.Id;

	public string Label => Preset.Label;
}

/// <summary>
/// 1.6 colpage, for the collections stream: a library's Nixie collections as presets (the strip's entries after the four
/// built in). Registered in DI; until it is, the strip has the four built-in presets. Review Major 4: the collections come
/// from an index read in the background, so the source may answer late, and say later that it has more:
/// <list type="bullet">
/// <item><see cref="PresetsForAsync"/> is asked once the library's titles are in (it may wait for the index; [] for none);</item>
/// <item><see cref="Changed"/> (any thread) makes a library that is shown ask again: the presets appear without a new visit.
/// A library that is left lets go of it.</item>
/// </list>
/// </summary>
public interface ILibraryPresetSource
{
	Task<IReadOnlyList<LibraryPreset>> PresetsForAsync(BaseItemDto library, IReadOnlyList<BaseItemDto> titles, CancellationToken cancellationToken);

	event EventHandler? Changed;
}

/// <summary>
/// 1.6 colpage, Save as collection (the applied-filter row): the filters on, as a saved Nixie collection is made from.
/// </summary>
public sealed record LibrarySavedFilter(
	Guid LibraryId,
	string LibraryName,
	bool Unwatched,
	bool Favorites,
	IReadOnlyList<string> Genres,
	IReadOnlyList<string> Years,
	IReadOnlyList<string> Ratings,
	IReadOnlyList<string> Tags,
	string? PresetId,
	ItemSortBy SortBy,
	SortOrder Order);

/// <summary>
/// 1.6 colpage: where "Save as collection" saves. A stub until the collections stream's <c>SmartCollectionStore.SaveFilter(name,
/// filter)</c> merges and implements it (registered in DI); with none, the button is not shown.
/// </summary>
public interface ISavedFilters
{
	/// <summary>Saves the filter under <paramref name="name"/>; false when it could not (the page says so).</summary>
	Task<bool> SaveFilter(string name, LibrarySavedFilter filter);
}

/// <summary>1.6 colpage: the strip's rules, apart from the page so they can be tested.</summary>
public static class LibraryPresets
{
	/// <summary>Whether <paramref name="preset"/> is lit for this filter and sort.</summary>
	public static bool IsOn(LibraryPreset preset, LibraryFilter filter, bool recentSort) => preset.Kind switch
	{
		LibraryPresetKind.All => LibraryBrowse.ActiveFilters(filter) == 0 && filter.Preset is null && !recentSort,
		LibraryPresetKind.Unwatched => filter.Unwatched,
		LibraryPresetKind.Favorites => filter.Favorites,
		LibraryPresetKind.RecentlyAdded => recentSort,
		LibraryPresetKind.Collection => filter.Preset is { } on && on.Id == preset.Filter.Items?.Id,
		_ => false,
	};

	/// <summary>What Narrator hears: "All titles, 320", "Unwatched, 223 titles", the preset's own name otherwise.</summary>
	public static string NameOf(LibraryPreset preset, int total, int unwatched) => preset.Kind switch
	{
		LibraryPresetKind.All => $"All titles, {total.ToString("N0", CultureInfo.CurrentCulture)}",
		LibraryPresetKind.Unwatched => unwatched == 1 ? "Unwatched, 1 title" : $"Unwatched, {unwatched.ToString("N0", CultureInfo.CurrentCulture)} titles",
		_ => preset.Name,
	};

	/// <summary>A name for a saved filter, from what it holds: "Unwatched Comedy", "Comedy, 1999", "Favorites".</summary>
	public static string SuggestedName(LibraryFilter filter)
	{
		var words = new List<string>();
		if (filter.Unwatched)
		{
			words.Add("Unwatched");
		}

		if (filter.Favorites)
		{
			words.Add(words.Count > 0 ? "favorite" : "Favorite");
		}

		var kinds = filter.Genres.Concat(filter.Tags).Concat(filter.Years).Concat(filter.OfficialRatings.Select(r => $"rated {r}")).ToList();
		if (filter.Preset is { } preset)
		{
			kinds.Insert(0, preset.Label);
		}

		var head = string.Join(" ", words);
		var tail = string.Join(", ", kinds);
		var name = (head, tail) switch
		{
			("", "") => "My collection",
			("", _) => tail,
			(_, "") => words.Count == 2 ? $"{head}s" : head == "Favorite" ? "Favorites" : head,
			_ => $"{head} {tail}",
		};
		return name.Length > 60 ? name[..60].TrimEnd() : name;
	}

	/// <summary>A saved filter's name is fine: not empty, at most 80 characters.</summary>
	public static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 80;
}
