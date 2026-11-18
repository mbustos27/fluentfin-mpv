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

	/// <summary>
	/// The A-Z key of a title (the review, item 6): its first letter without accents (Éclair: E), the Latin letters that do
	/// not come apart under Unicode folded by hand (Ł: L, Ø: O, Æ: A, ß: S...); a letter of another cased script (Cyrillic,
	/// Greek, Armenian) is its own key in capitals (Брат: Б); a script without capitals has one key for all its titles
	/// (Chinese, Japanese kana, Korean, Arabic, Hebrew, Thai, Devanagari: 漢, あ, 한, ع, א, ก, अ); a number or a sign is "#".
	/// </summary>
	public static string LetterOf(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "#";
		}

		foreach (var rune in name.Trim().Normalize(NormalizationForm.FormD).EnumerateRunes())
		{
			var category = Rune.GetUnicodeCategory(rune);
			if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
			{
				continue;
			}

			if (!Rune.IsLetter(rune))
			{
				return "#";
			}

			var upper = Rune.ToUpperInvariant(rune);
			if (upper.Value is >= 'A' and <= 'Z')
			{
				return ((char)upper.Value).ToString();
			}

			if (LatinFolds.TryGetValue(upper.Value, out var folded))
			{
				return folded;
			}

			if (ScriptKey(rune.Value) is { } script)
			{
				return script;
			}

			// a cased letter of another script: its capital; a caseless one of a script not listed: itself
			return upper.ToString();
		}

		return "#";
	}

	// Latin letters with no decomposition to a base letter
	private static readonly Dictionary<int, string> LatinFolds = new()
	{
		['Ł'] = "L", ['Ŀ'] = "L", ['Ø'] = "O", ['Œ'] = "O", ['Æ'] = "A", ['Đ'] = "D", ['Ð'] = "D", ['Þ'] = "T", ['Ŧ'] = "T",
		['Ħ'] = "H", ['Ŋ'] = "N", ['ẞ'] = "S", ['ß'] = "S", ['ı'] = "I", ['Ɨ'] = "I", ['ĸ'] = "K", ['Ƒ'] = "F", ['Ʒ'] = "Z",
	};

	// one key for every title of a script without capitals (they sort together, and a key per character would be hundreds)
	private static string? ScriptKey(int c) => c switch
	{
		>= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F => "あ",           // kana: あ
		>= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3FFFF => "漢", // Han: 漢
		>= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F => "한",           // Hangul: 한
		>= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F => "ع",                                      // Arabic: ع
		>= 0x0590 and <= 0x05FF => "א",                                                                // Hebrew: א
		>= 0x0E00 and <= 0x0E7F => "ก",                                                                // Thai: ก
		>= 0x0900 and <= 0x097F => "अ",                                                                // Devanagari: अ
		_ => null,
	};

	/// <summary>
	/// The A-Z jump of a list in its order (the library sorted by name, filtered): each key that some title has, once, in
	/// the order the list meets them, with the place of its first title, and of the first title of every later run of it
	/// (a key whose titles the sort does not keep together: the letter steps through them).
	/// </summary>
	public static IReadOnlyList<LetterJump> Letters(IEnumerable<string?> sortedNames)
	{
		var runs = new Dictionary<string, List<int>>();
		var order = new List<string>();
		string? previous = null;
		var index = 0;
		foreach (var name in sortedNames)
		{
			var letter = LetterOf(name);
			if (!runs.TryGetValue(letter, out var starts))
			{
				runs[letter] = starts = [];
				order.Add(letter);
			}

			if (letter != previous)
			{
				starts.Add(index);
			}

			previous = letter;
			index++;
		}

		return [.. order.Select(letter => new LetterJump(letter, runs[letter][0]) { Runs = [.. runs[letter]] })];
	}
}

/// <summary>Seaglass 1.1.4: one button of the Library's A-Z jump: the letter and where its first title is in the library.</summary>
public sealed record LetterJump(string Letter, int Index)
{
	/// <summary>Where each run of titles with this key starts (the first is <see cref="Index"/>): pressed again, the letter goes on to the next.</summary>
	public IReadOnlyList<int> Runs { get; init; } = [Index];

	/// <summary>What Narrator hears: "Jump to M", "Jump to titles that start with a number or a sign".</summary>
	public string Name => Letter == "#" ? "Jump to titles that start with a number or a sign" : $"Jump to {Letter}";

	public bool Equals(LetterJump? other) => other is not null && Letter == other.Letter && Index == other.Index && Runs.SequenceEqual(other.Runs);

	public override int GetHashCode() => HashCode.Combine(Letter, Index, Runs.Count);
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
