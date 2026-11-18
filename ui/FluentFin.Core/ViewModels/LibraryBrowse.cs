using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Seaglass 1.1.4 (UX review, fixes 9 and 16): what the Library's bar says (the sort in use, the filters on, the count) and
/// its A-Z jump, apart from the page so they can be tested.
/// </summary>
public static class LibraryBrowse
{
	/// <summary>The sort's name, as its menu says it.</summary>
	public static string SortName(ItemSortBy sortBy) => sortBy switch
	{
		ItemSortBy.Name => "Name",
		ItemSortBy.CommunityRating => "Community rating",
		ItemSortBy.CriticRating => "Critic rating",
		ItemSortBy.DateCreated => "Date added",
		ItemSortBy.DatePlayed => "Date played",
		ItemSortBy.PlayCount => "Play count",
		ItemSortBy.PremiereDate => "Release date",
		ItemSortBy.Runtime => "Runtime",
		_ => sortBy.ToString(),
	};

	/// <summary>What Narrator hears for the Sort button: "Sort: Name, ascending".</summary>
	public static string SortButtonName(ItemSortBy sortBy, SortOrder order) =>
		$"Sort: {SortName(sortBy)}, {(order == SortOrder.Descending ? "descending" : "ascending")}";

	/// <summary>The direction button for Narrator, what it is and what a click does: "Ascending, switch to descending".</summary>
	public static string SortOrderButtonName(SortOrder order) =>
		order == SortOrder.Descending ? "Descending, switch to ascending" : "Ascending, switch to descending";

	/// <summary>The arrow beside the sort's name: up for ascending, down for descending (Segoe Fluent Icons).</summary>
	public static string SortGlyph(SortOrder order) => order == SortOrder.Descending ? "" : "";

	/// <summary>The filters on: Unwatched, Favorites, and every genre, tag, year and rating picked counts one.</summary>
	public static int ActiveFilters(LibraryFilter filter) =>
		(filter.Unwatched ? 1 : 0) + (filter.Favorites ? 1 : 0) + filter.Genres.Count + filter.Tags.Count + filter.Years.Count + filter.OfficialRatings.Count;

	/// <summary>The Filter button's words: "Filter", or "Filter (2)" with two on.</summary>
	public static string FilterText(int active) => active > 0 ? $"Filter ({active})" : "Filter";

	/// <summary>What Narrator hears for the Filter button: "Filter, 2 on".</summary>
	public static string FilterButtonName(int active) => active switch
	{
		0 => "Filter, none on",
		1 => "Filter, 1 on",
		_ => $"Filter, {active} on",
	};

	/// <summary>"368 movies", "1 show", "12 of 368 movies" when filters hide some.</summary>
	public static string CountText(int shown, int total, BaseItemDto_CollectionType? collection)
	{
		var (one, many) = collection switch
		{
			BaseItemDto_CollectionType.Movies => ("movie", "movies"),
			BaseItemDto_CollectionType.Tvshows => ("show", "shows"),
			_ => ("item", "items"),
		};

		return shown == total
			? $"{total:N0} {(total == 1 ? one : many)}"
			: $"{shown:N0} of {total:N0} {(total == 1 ? one : many)}";
	}

	/// <summary>The A-Z key of a title: its first letter, without accents, A to Z; "#" for a digit, a sign or nothing.</summary>
	public static string LetterOf(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "#";
		}

		foreach (var c in name.Trim().Normalize(NormalizationForm.FormD))
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
			{
				continue;
			}

			var upper = char.ToUpperInvariant(c);
			return upper is >= 'A' and <= 'Z' ? upper.ToString() : "#";
		}

		return "#";
	}

	/// <summary>
	/// The A-Z jump of a list in its order (the library sorted by name, filtered): each key that some title has, once, in
	/// the order the list meets them, with the place of its first title. Only the letters that exist.
	/// </summary>
	public static IReadOnlyList<LetterJump> Letters(IEnumerable<string?> sortedNames)
	{
		var letters = new List<LetterJump>();
		var seen = new HashSet<string>();
		var index = 0;
		foreach (var name in sortedNames)
		{
			var letter = LetterOf(name);
			if (seen.Add(letter))
			{
				letters.Add(new LetterJump(letter, index));
			}

			index++;
		}

		return letters;
	}
}

/// <summary>Seaglass 1.1.4: one button of the Library's A-Z jump: the letter and where its first title is in the library.</summary>
public sealed record LetterJump(string Letter, int Index)
{
	/// <summary>What Narrator hears: "Jump to M", "Jump to titles that start with a number or a sign".</summary>
	public string Name => Letter == "#" ? "Jump to titles that start with a number or a sign" : $"Jump to {Letter}";
}

/// <summary>
/// Seaglass 1.1.4: where the user was in a library when they left it for an item (the sort, the filters, the card), so Back
/// puts them there again. Kept for the session, per user and library.
/// </summary>
public sealed record LibraryPlace(ItemSortBy SortBy, SortOrder Order, IReadOnlyList<string> Genres, IReadOnlyList<string> Tags,
	IReadOnlyList<string> Years, IReadOnlyList<string> OfficialRatings, Guid? ItemId, double Offset = 0, bool Unwatched = false, bool Favorites = false);

public static class LibraryPlaces
{
	private const int Kept = 16;
	private static readonly Lock s_lock = new();
	private static readonly List<(Guid User, Guid Library, LibraryPlace Place)> s_places = [];

	public static void Remember(Guid user, Guid library, LibraryPlace place)
	{
		lock (s_lock)
		{
			s_places.RemoveAll(x => x.User == user && x.Library == library);
			s_places.Add((user, library, place));
			if (s_places.Count > Kept)
			{
				s_places.RemoveAt(0);
			}
		}
	}

	public static LibraryPlace? Recall(Guid user, Guid library)
	{
		lock (s_lock)
		{
			return s_places.LastOrDefault(x => x.User == user && x.Library == library).Place;
		}
	}
}

/// <summary>
/// Seaglass 1.1.4 (interaction audit, item 12): one list of the Filter menu (genres, years, ratings, tags): each value the
/// library's titles have, with how many have it, as a check box bound to the filter's own list of picked values.
/// </summary>
public sealed class FilterFacet
{
	private FilterFacet(string title, IReadOnlyList<FilterOption> options, ObservableCollection<string> picked)
	{
		Title = title;
		Options = options;
		picked.CollectionChanged += (_, _) =>
		{
			foreach (var option in options)
			{
				option.Refresh();
			}
		};
	}

	public string Title { get; }

	public IReadOnlyList<FilterOption> Options { get; }

	/// <summary>The lists for a library's titles, those with values only: genres and tags A to Z, years newest first, ratings A to Z.</summary>
	public static IReadOnlyList<FilterFacet> From(IReadOnlyList<BaseItemDto> items, LibraryFilter filter)
	{
		var facets = new List<FilterFacet>();

		void Add(string title, IEnumerable<string> values, ObservableCollection<string> picked, bool newestFirst = false)
		{
			var counted = values.Where(v => !string.IsNullOrWhiteSpace(v)).GroupBy(v => v, StringComparer.Ordinal)
				.Select(g => (Value: g.Key, Count: g.Count()));
			counted = newestFirst ? counted.OrderByDescending(x => x.Value, StringComparer.Ordinal) : counted.OrderBy(x => x.Value, StringComparer.CurrentCultureIgnoreCase);
			var options = counted.Select(x => new FilterOption(x.Value, x.Count, picked)).ToList();
			if (options.Count > 0)
			{
				facets.Add(new FilterFacet(title, options, picked));
			}
		}

		// each title counts once per value (a title listing a genre twice is still one)
		Add("Genres", items.SelectMany(x => (x.Genres ?? []).Distinct()), filter.Genres);
		Add("Years", items.Where(x => x.ProductionYear is > 0).Select(x => x.ProductionYear!.Value.ToString(CultureInfo.InvariantCulture)), filter.Years, newestFirst: true);
		Add("Ratings", items.Select(x => x.OfficialRating ?? ""), filter.OfficialRatings);
		Add("Tags", items.SelectMany(x => (x.Tags ?? []).Distinct()), filter.Tags);
		return facets;
	}
}

/// <summary>Seaglass 1.1.4: one check box of the Filter menu: a value, how many titles have it, and whether it is picked.</summary>
public sealed partial class FilterOption(string value, int count, ObservableCollection<string> picked) : ObservableObject
{
	public string Value { get; } = value;

	public int Count { get; } = count;

	/// <summary>"Drama (12)".</summary>
	public string Label => $"{Value} ({Count})";

	/// <summary>For Narrator: "Drama, 12 titles".</summary>
	public string Name => Count == 1 ? $"{Value}, 1 title" : $"{Value}, {Count} titles";

	/// <summary>Picked: in the filter's list (a click adds or removes it; a change of the list from elsewhere shows here).</summary>
	public bool IsChecked
	{
		get => picked.Contains(Value);
		set
		{
			if (value == IsChecked)
			{
				return;
			}

			if (value)
			{
				picked.Add(Value);
			}
			else
			{
				picked.Remove(Value);
			}
		}
	}

	internal void Refresh() => OnPropertyChanged(nameof(IsChecked));
}
