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
	/// <summary>The sort's name, as its menu says it. Nixie 1.4.0 (the UX review's item 8): the name sort is "Title".</summary>
	public static string SortName(ItemSortBy sortBy) => sortBy switch
	{
		ItemSortBy.Name => "Title",
		ItemSortBy.CommunityRating => "Community rating",
		ItemSortBy.CriticRating => "Critic rating",
		ItemSortBy.DateCreated => "Date added",
		ItemSortBy.DatePlayed => "Date played",
		ItemSortBy.PlayCount => "Play count",
		ItemSortBy.PremiereDate => "Release date",
		ItemSortBy.Runtime => "Runtime",
		_ => sortBy.ToString(),
	};

	/// <summary>The keys the Sort menu offers, in its order.</summary>
	public static IReadOnlyList<ItemSortBy> SortKeys { get; } =
	[
		ItemSortBy.Name, ItemSortBy.DateCreated, ItemSortBy.PremiereDate, ItemSortBy.CommunityRating, ItemSortBy.CriticRating,
		ItemSortBy.DatePlayed, ItemSortBy.PlayCount, ItemSortBy.Runtime,
	];

	/// <summary>
	/// Nixie 1.4.0 (the UX review's X5, item 8): the direction a key opens in: titles A to Z, the shortest first, and every
	/// date, rating and play count newest, highest or most first (Date added opened oldest first).
	/// </summary>
	public static SortOrder NaturalOrder(ItemSortBy sortBy) => sortBy switch
	{
		ItemSortBy.Name or ItemSortBy.Runtime => SortOrder.Ascending,
		_ => SortOrder.Descending,
	};

	/// <summary>The direction in words, for the key: "A to Z", "newest first", "highest first", "most played first", "shortest first".</summary>
	public static string DirectionWords(ItemSortBy sortBy, SortOrder order)
	{
		var descending = order == SortOrder.Descending;
		return sortBy switch
		{
			ItemSortBy.Name => descending ? "Z to A" : "A to Z",
			ItemSortBy.DateCreated or ItemSortBy.DatePlayed or ItemSortBy.PremiereDate => descending ? "newest first" : "oldest first",
			ItemSortBy.CommunityRating or ItemSortBy.CriticRating => descending ? "highest first" : "lowest first",
			ItemSortBy.PlayCount => descending ? "most played first" : "least played first",
			ItemSortBy.Runtime => descending ? "longest first" : "shortest first",
			_ => descending ? "descending" : "ascending",
		};
	}

	/// <summary>The words of the other direction ("Z to A" for "A to Z").</summary>
	public static string OtherDirectionWords(ItemSortBy sortBy, SortOrder order) =>
		DirectionWords(sortBy, order == SortOrder.Descending ? SortOrder.Ascending : SortOrder.Descending);

	/// <summary>What the Sort button shows in every theme: "Title · A to Z", "Date added · newest first".</summary>
	public static string SortText(ItemSortBy sortBy, SortOrder order) => $"{SortName(sortBy)} · {DirectionWords(sortBy, order)}";

	/// <summary>
	/// What Narrator hears for the Sort button: the words it shows first (WCAG 2.5.3, label in name), then what it is:
	/// "Title, A to Z, Sort".
	/// </summary>
	public static string SortButtonName(ItemSortBy sortBy, SortOrder order) => $"{SortName(sortBy)}, {DirectionWords(sortBy, order)}, Sort";

	/// <summary>The direction button for Narrator, what it is and what a click does: "A to Z, switch to Z to A".</summary>
	public static string SortOrderButtonName(ItemSortBy sortBy, SortOrder order) =>
		$"{Capital(DirectionWords(sortBy, order))}, switch to {OtherDirectionWords(sortBy, order)}";

	private static string Capital(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

	/// <summary>The arrow beside the sort's name: up for ascending, down for descending (Segoe Fluent Icons).</summary>
	public static string SortGlyph(SortOrder order) => order == SortOrder.Descending ? "" : "";

	/// <summary>The filters on: Unwatched, Favorites, and every genre, tag, year and rating picked counts one.</summary>
	public static int ActiveFilters(LibraryFilter filter) =>
		(filter.Unwatched ? 1 : 0) + (filter.Favorites ? 1 : 0) + filter.Genres.Count + filter.Tags.Count + filter.Years.Count + filter.OfficialRatings.Count;

	/// <summary>The Filter button's words: "Filter", or "Filter (2)" with two on.</summary>
	public static string FilterText(int active) => active > 0 ? $"Filter ({active})" : "Filter";

	/// <summary>
	/// What Narrator hears for the Filter button: "Filter (2 on)". 1.5 (the button review's L25): it starts with the words the
	/// button shows ("Filter (2)"), so a speech user can say what they see.
	/// </summary>
	public static string FilterButtonName(int active) => active switch
	{
		0 => "Filter (none on)",
		_ => $"Filter ({active} on)",
	};

	/// <summary>"368 movies", "1 show", "12 of 368 movies" when filters hide some.</summary>
	public static string CountText(int shown, int total, BaseItemDto_CollectionType? collection)
	{
		var (one, many) = Words(collection);
		return shown == total
			? $"{total:N0} {(total == 1 ? one : many)}"
			: $"{shown:N0} of {total:N0} {(total == 1 ? one : many)}";
	}

	private static (string One, string Many) Words(BaseItemDto_CollectionType? collection) => collection switch
	{
		BaseItemDto_CollectionType.Movies => ("movie", "movies"),
		BaseItemDto_CollectionType.Tvshows => ("show", "shows"),
		_ => ("item", "items"),
	};

	/// <summary>Nixie 1.4.1 (big libraries): while the rest loads: "Loading… 1,200 of 25,842 movies".</summary>
	public static string LoadingText(int loaded, int total, BaseItemDto_CollectionType? collection)
	{
		var (one, many) = Words(collection);
		return $"Loading… {loaded:N0} of {total:N0} {(total == 1 ? one : many)}";
	}

	/// <summary>Nixie 1.4.1: a page failed every try: "1,200 of 25,842 movies · the rest didn't load".</summary>
	public static string StoppedText(int loaded, int total, BaseItemDto_CollectionType? collection)
	{
		var (one, many) = Words(collection);
		return $"{loaded:N0} of {total:N0} {(total == 1 ? one : many)} · the rest didn't load";
	}

	/// <summary>
	/// Nixie 1.4.1: filters on over a library not all in: "12 of 1,200 movies so far" while it loads, "12 of 1,200 loaded
	/// movies" when it stopped.
	/// </summary>
	public static string PartCountText(int shown, int loaded, BaseItemDto_CollectionType? collection, bool loading)
	{
		var (one, many) = Words(collection);
		var words = loaded == 1 ? one : many;
		return loading ? $"{shown:N0} of {loaded:N0} {words} so far" : $"{shown:N0} of {loaded:N0} loaded {words}";
	}

	/// <summary>Nixie 1.4.0 (item 2): the applied-filter row's count line: "Showing 223 of 320 movies".</summary>
	public static string ShowingText(int shown, int total, BaseItemDto_CollectionType? collection) => $"Showing {CountText(shown, total, collection)}";

	/// <summary>
	/// Nixie 1.4.0 (item 13): the A to Z shows from this many titles shown; under it the grid is one or two screens, where a
	/// jump list only costs room (Microsoft's semantic zoom guidance; the UX review's X16: it showed for a six-film collection).
	/// </summary>
	public const int LettersFrom = 48;

	/// <summary>
	/// Nixie 1.4.0 (item 13, large libraries): where the view is: "M · 324 of 1,000" (the letter of the first title in view,
	/// sorted by title), else "324 of 1,000"; <paramref name="index"/> counts from 0.
	/// </summary>
	public static string PositionText(string? letter, int index, int total)
	{
		if (total <= 0 || index < 0)
		{
			return "";
		}

		var place = $"{Math.Min(index + 1, total):N0} of {total:N0}";
		return string.IsNullOrEmpty(letter) ? place : $"{letter} · {place}";
	}

	/// <summary>
	/// Nixie 1.4.0 (item 4, type-to-jump): the first title, from <paramref name="from"/> on and round, whose sort name or name
	/// starts with what was typed (any case, accents ignored), else null.
	/// </summary>
	public static int? TypeAhead(IReadOnlyList<BaseItemViewModel> items, string typed, int from = 0)
	{
		if (items.Count == 0 || string.IsNullOrWhiteSpace(typed))
		{
			return null;
		}

		var compare = CultureInfo.CurrentCulture.CompareInfo;
		const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
		var start = Math.Clamp(from, 0, items.Count - 1);
		for (var i = 0; i < items.Count; i++)
		{
			var item = items[(start + i) % items.Count];
			if (compare.IsPrefix(SortKey(item).TrimStart(), typed, options) || compare.IsPrefix((item.Name ?? "").TrimStart(), typed, options))
			{
				return (start + i) % items.Count;
			}
		}

		return null;
	}

	/// <summary>
	/// Nixie 1.4.0 (item 13): the A to Z in every theme: # and A to Z always, in one shape (a letter no title starts with is
	/// dimmed, skipped by the arrows and silent to Narrator), then any other key the titles have (Б, 漢).
	/// </summary>
	/// <summary>
	/// Nixie 1.4.0 integration (the big-library stream with the A to Z): the tiles of <paramref name="letters"/> set into the
	/// existing <paramref name="tiles"/> when they are the same letters in the same order (every page of a big library moved
	/// the jumps and made the tiles anew, so the keyboard on a letter fell out of the A to Z each time); false when the
	/// letters themselves changed, and the tiles must be made anew.
	/// </summary>
	public static bool UpdateTiles(IReadOnlyList<LetterTile> tiles, IReadOnlyList<LetterJump> letters)
	{
		var fresh = Tiles(letters);
		if (fresh.Count != tiles.Count || !fresh.Select(t => t.Letter).SequenceEqual(tiles.Select(t => t.Letter)))
		{
			return false;
		}

		for (var i = 0; i < tiles.Count; i++)
		{
			tiles[i].Jump = fresh[i].Jump;
			tiles[i].Position = fresh[i].Position;
			tiles[i].Size = fresh[i].Size;
		}

		return true;
	}

	public static IReadOnlyList<LetterTile> Tiles(IReadOnlyList<LetterJump> letters)
	{
		var byLetter = letters.ToDictionary(l => l.Letter, StringComparer.OrdinalIgnoreCase);
		var tiles = new List<LetterTile>();
		foreach (var letter in "#ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString()))
		{
			tiles.Add(new LetterTile(letter, byLetter.GetValueOrDefault(letter)));
			byLetter.Remove(letter);
		}

		tiles.AddRange(letters.Where(l => byLetter.ContainsKey(l.Letter)).Select(l => new LetterTile(l.Letter, l)));
		var reachable = tiles.Where(t => t.HasTitles).ToList();
		foreach (var tile in reachable)
		{
			tile.Position = reachable.IndexOf(tile) + 1;
			tile.Size = reachable.Count;
		}

		return tiles;
	}

	/// <summary>
	/// The A-Z key of a title (the review, item 6): its first letter without accents (Éclair: E), the Latin letters that do
	/// not come apart under Unicode folded by hand (Ł: L, Ø: O, Æ: A, ß: S...); a letter of another cased script (Cyrillic,
	/// Greek, Armenian) is its own key in capitals (Брат: Б); a script without capitals has one key for all its titles
	/// (Chinese, Japanese kana, Korean, Arabic, Hebrew, Thai, Devanagari: 漢, あ, 한, ع, א, ก, अ); a number or a sign is "#".
	/// </summary>
	/// <summary>
	/// Nixie 1.3.1 (the UX review's CL-M6): what a title sorts and files under: Jellyfin's SortName ("Matrix" for "The
	/// Matrix"; the server's own rules and the user's forced sort name), else its name.
	/// </summary>
	public static string SortKey(BaseItemViewModel item) => string.IsNullOrWhiteSpace(item.SortName) ? item.Name ?? "" : item.SortName;

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
/// library's titles have, with how many have it, as a check box bound to the filter's own list of picked values. Nixie
/// 1.4.0 (the UX review's X4): the counts are counted again on every filter change, against the other filters on (they were
/// counted once, at load), and a value that would leave nothing is greyed, not hidden.
/// </summary>
public sealed class FilterFacet
{
	private readonly ObservableCollection<string> _picked;

	private FilterFacet(FilterGroup group, string title, IReadOnlyList<FilterOption> options, ObservableCollection<string> picked)
	{
		Group = group;
		Title = title;
		Options = options;
		_picked = picked;
		picked.CollectionChanged += Picked_CollectionChanged;
	}

	private void Picked_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		foreach (var option in Options)
		{
			option.Refresh();
		}
	}

	/// <summary>Nixie 1.4.1: replaced by new lists: no longer follows the picks (each list made again was kept alive by them).</summary>
	public void Detach() => _picked.CollectionChanged -= Picked_CollectionChanged;

	public FilterGroup Group { get; }

	public string Title { get; }

	public IReadOnlyList<FilterOption> Options { get; }

	/// <summary>The lists for a library's titles, those with values only: genres and tags A to Z, years newest first, ratings A to Z.</summary>
	public static IReadOnlyList<FilterFacet> From(IReadOnlyList<BaseItemDto> items, LibraryFilter filter)
	{
		var facets = new List<FilterFacet>();

		void Add(FilterGroup group, string title, IEnumerable<string> values, ObservableCollection<string> picked, bool newestFirst = false)
		{
			var counted = values.Where(v => !string.IsNullOrWhiteSpace(v)).GroupBy(v => v, StringComparer.Ordinal)
				.Select(g => (Value: g.Key, Count: g.Count()));
			counted = newestFirst ? counted.OrderByDescending(x => x.Value, StringComparer.Ordinal) : counted.OrderBy(x => x.Value, StringComparer.CurrentCultureIgnoreCase);
			var options = counted.Select(x => new FilterOption(x.Value, x.Count, picked)).ToList();
			if (options.Count > 0)
			{
				facets.Add(new FilterFacet(group, title, options, picked));
			}
		}

		// each title counts once per value (a title listing a genre twice is still one)
		Add(FilterGroup.Genres, "Genres", items.SelectMany(x => (x.Genres ?? []).Distinct()), filter.Genres);
		Add(FilterGroup.Years, "Years", items.Where(x => x.ProductionYear is > 0).Select(x => x.ProductionYear!.Value.ToString(CultureInfo.InvariantCulture)), filter.Years, newestFirst: true);
		Add(FilterGroup.Ratings, "Ratings", items.Select(x => x.OfficialRating ?? ""), filter.OfficialRatings);
		Add(FilterGroup.Tags, "Tags", items.SelectMany(x => (x.Tags ?? []).Distinct()), filter.Tags);
		return facets;
	}

	/// <summary>Each value's count again: the titles that have it among those the other filters on let through.</summary>
	public void Recount(IEnumerable<BaseItemViewModel> items, LibraryFilter filter)
	{
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var item in items)
		{
			if (!filter.Matches(item, Group))
			{
				continue;
			}

			foreach (var value in ValuesOf(item, Group))
			{
				counts[value] = counts.GetValueOrDefault(value) + 1;
			}
		}

		foreach (var option in Options)
		{
			option.Count = counts.GetValueOrDefault(option.Value);
		}
	}

	private static IEnumerable<string> ValuesOf(BaseItemViewModel item, FilterGroup group) => group switch
	{
		FilterGroup.Genres => (item.Genres ?? []).Distinct(),
		FilterGroup.Tags => (item.Tags ?? []).Distinct(),
		FilterGroup.Ratings => string.IsNullOrWhiteSpace(item.OfficialRating) ? [] : [item.OfficialRating],
		FilterGroup.Years => item.ProductionYear is > 0 and var year ? [year.ToString(CultureInfo.InvariantCulture)] : [],
		_ => [],
	};
}

/// <summary>
/// Seaglass 1.1.4: one check box of the Filter menu: a value, how many titles have it, and whether it is picked. Nixie 1.4.0:
/// its count follows the other filters; at 0 (and not picked) it is greyed.
/// </summary>
public sealed partial class FilterOption(string value, int count, ObservableCollection<string> picked) : ObservableObject
{
	public string Value { get; } = value;

	/// <summary>How many titles the other filters on leave that have it.</summary>
	public int Count
	{
		get;
		set
		{
			if (SetProperty(ref field, value))
			{
				OnPropertyChanged(nameof(Label));
				OnPropertyChanged(nameof(Name));
				OnPropertyChanged(nameof(IsAvailable));
			}
		}
	} = count;

	/// <summary>"Drama (12)".</summary>
	public string Label => $"{Value} ({Count})";

	/// <summary>For Narrator: "Drama, 12 titles".</summary>
	public string Name => Count == 1 ? $"{Value}, 1 title" : $"{Value}, {Count} titles";

	/// <summary>Nixie 1.4.0: something to pick: it would leave titles, or it is picked (and can be taken off).</summary>
	public bool IsAvailable => Count > 0 || IsChecked;

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

	internal void Refresh()
	{
		OnPropertyChanged(nameof(IsChecked));
		OnPropertyChanged(nameof(IsAvailable));
	}
}
