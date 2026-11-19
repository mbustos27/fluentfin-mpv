using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.3.0, the Editorial theme's Library (Themes\Editorial\Views\EditorialLibraryPage): genre chips with their counts
/// over the library, and the grid split by letter with details in place. Additive: read from the titles the library
/// already holds and its Filter; nothing more is asked of the server.
/// </summary>
public partial class LibraryViewModel
{
	/// <summary>Every title of the library (unfiltered), for the "All" chip.</summary>
	public int EditorialTotal => _itemsCache.Count;

	/// <summary>"312 films · 41 unwatched · 18 added this month" (over the whole library).</summary>
	public string EditorialSummary() => EditorialLibrary.Summary([.. _itemsCache.Items], _library?.CollectionType, DateTimeOffset.UtcNow);

	/// <summary>The genre chips: the library's genres with how many titles have each, A to Z (the Filter menu's own counts).</summary>
	public IReadOnlyList<FilterOption> EditorialGenreChips() =>
		Facets.FirstOrDefault(f => f.Title == "Genres")?.Options ?? [];

	/// <summary>Titles not watched yet among those the other filters leave, for the Unwatched switch (Nixie 1.4.0: honest under a filter).</summary>
	public int EditorialUnwatched => UnwatchedCount;

	/// <summary>The genre the chips show as picked: the one genre the filter has, null for none (All) or several.</summary>
	public string? EditorialGenre => Filter.Genres.Count == 1 ? Filter.Genres[0] : null;

	/// <summary>
	/// A chip. Nixie 1.4.0 (the UX review's E-M3, item 2): the genre chips are the shared filter's genres, picked and unpicked
	/// one by one (OR within: Comedy or Drama), as the Filter panel's boxes; All takes every genre off.
	/// </summary>
	public void EditorialPickGenre(string? genre)
	{
		if (genre is null)
		{
			Filter.Genres.Clear();
		}
		else if (!Filter.Genres.Remove(genre))
		{
			Filter.Genres.Add(genre);
		}
	}
}

/// <summary>Editorial: one line of the Library's grid split by letter: a section's heading, a row of posters, or details.</summary>
public abstract record EditorialLibraryLine;

/// <summary>A section's heading: "B", "23 films".</summary>
/// <param name="FirstIndex">Where its first title is in the library as shown.</param>
public sealed record EditorialSectionLine(string Letter, string Caption, int FirstIndex) : EditorialLibraryLine
{
	/// <summary>Narrator: "B, 23 films".</summary>
	public string AutomationName => $"{(Letter == "#" ? "Numbers and signs" : Letter)}, {Caption}";
}

/// <summary>A row of posters; <paramref name="FirstIndex"/>: the first one's place in the library as shown.</summary>
public sealed record EditorialRowLine(IReadOnlyList<BaseItemViewModel> Items, int FirstIndex, string Letter, int Total = 0) : EditorialLibraryLine
{
	/// <summary>The row's posters with their place in the library as shown (Narrator's "n of m").</summary>
	public IReadOnlyList<EditorialCell> Cells => [.. Items.Select((item, i) => new EditorialCell(item, FirstIndex + i + 1, Total))];
}

/// <summary>A poster of a row, with its place: <paramref name="Position"/> of <paramref name="Size"/>.</summary>
public sealed record EditorialCell(BaseItemViewModel Item, int Position, int Size);

/// <summary>The opened poster's details, under its row.</summary>
public sealed record EditorialDetailsLine(BaseItemViewModel Item) : EditorialLibraryLine;

/// <summary>Editorial: the Library grid's pure parts (tested in EditorialThemeTests).</summary>
public static class EditorialLibrary
{
	/// <summary>A poster's width and the gap between posters (12 across at 1920).</summary>
	public const double PosterWidth = 136, Gap = 16;

	/// <summary>How many posters a row holds in <paramref name="width"/> (at least 2).</summary>
	public static int Columns(double width, double poster = PosterWidth) => Math.Max(2, (int)Math.Floor((width + Gap) / (poster + Gap)));

	/// <summary>
	/// The grid's lines: the titles (in the library's order) cut into rows of <paramref name="columns"/>; sorted by name,
	/// each letter is a section of its own with its heading ("B", "23 films"); <paramref name="opened"/>'s details follow
	/// its row.
	/// </summary>
	public static IReadOnlyList<EditorialLibraryLine> Lines(IReadOnlyList<BaseItemViewModel> items, int columns, bool byLetter, Guid? opened, BaseItemDto_CollectionType? kind)
	{
		var lines = new List<EditorialLibraryLine>();
		columns = Math.Max(1, columns);
		void AddRows(int start, int end, string letter)
		{
			for (var i = start; i < end; i += columns)
			{
				var row = new List<BaseItemViewModel>();
				for (var j = i; j < Math.Min(end, i + columns); j++)
				{
					row.Add(items[j]);
				}

				lines.Add(new EditorialRowLine(row, i, letter, items.Count));
				if (opened is { } id && row.FirstOrDefault(x => x.Id == id) is { } item)
				{
					lines.Add(new EditorialDetailsLine(item));
				}
			}
		}

		if (!byLetter)
		{
			AddRows(0, items.Count, "");
			return lines;
		}

		var start = 0;
		while (start < items.Count)
		{
			var letter = LibraryBrowse.LetterOf(LibraryBrowse.SortKey(items[start]));
			var end = start + 1;
			while (end < items.Count && LibraryBrowse.LetterOf(LibraryBrowse.SortKey(items[end])) == letter)
			{
				end++;
			}

			lines.Add(new EditorialSectionLine(letter, Count(end - start, kind), start));
			AddRows(start, end, letter);
			start = end;
		}

		return lines;
	}

	/// <summary>"23 films", "1 show", "4 titles".</summary>
	public static string Count(int count, BaseItemDto_CollectionType? kind)
	{
		var (one, many) = kind switch
		{
			BaseItemDto_CollectionType.Movies => ("film", "films"),
			BaseItemDto_CollectionType.Tvshows => ("show", "shows"),
			_ => ("title", "titles"),
		};
		return $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? one : many)}";
	}

	/// <summary>The line and column of the title at <paramref name="index"/> in the library as shown, or null.</summary>
	public static (int Line, int Column)? Find(IReadOnlyList<EditorialLibraryLine> lines, int index)
	{
		for (var i = 0; i < lines.Count; i++)
		{
			if (lines[i] is EditorialRowLine row && index >= row.FirstIndex && index < row.FirstIndex + row.Items.Count)
			{
				return (i, index - row.FirstIndex);
			}
		}

		return null;
	}

	/// <summary>The line of the section heading of <paramref name="letter"/>, or -1.</summary>
	public static int SectionOf(IReadOnlyList<EditorialLibraryLine> lines, string letter)
	{
		for (var i = 0; i < lines.Count; i++)
		{
			if (lines[i] is EditorialSectionLine section && section.Letter == letter)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>
	/// The keyboard in the grid: from the title at <paramref name="index"/>, the arrow keys' title (Left and Right along the
	/// library's order, across rows; Up and Down to the nearest column of the row above or below, across sections), or null
	/// at an end. Nixie 1.3.1: Page Up and Page Down go <paramref name="rowsPerPage"/> rows up or down in the same column (as
	/// far as there are rows: the first or the last row at the ends), as the other themes' grids do.
	/// </summary>
	public static int? Move(IReadOnlyList<EditorialLibraryLine> lines, int index, int count, string key, int rowsPerPage = 1)
	{
		if (key is "PageUp" or "PageDown")
		{
			int? landed = null;
			for (var i = 0; i < Math.Max(1, rowsPerPage); i++)
			{
				if (Move(lines, landed ?? index, count, key == "PageDown" ? "Down" : "Up") is not { } next)
				{
					break;
				}

				landed = next;
			}

			return landed;
		}

		switch (key)
		{
			case "Left":
				return index > 0 ? index - 1 : null;
			case "Right":
				return index < count - 1 ? index + 1 : null;
			case "Home":
				return 0;
			case "End":
				return count - 1;
		}

		if (Find(lines, index) is not { } at || key is not ("Up" or "Down"))
		{
			return null;
		}

		var step = key == "Down" ? 1 : -1;
		for (var i = at.Line + step; i >= 0 && i < lines.Count; i += step)
		{
			if (lines[i] is EditorialRowLine row)
			{
				return row.FirstIndex + Math.Min(at.Column, row.Items.Count - 1);
			}
		}

		return null;
	}

	/// <summary>"312 films · 41 unwatched · 18 added this month" (the parts it has).</summary>
	public static string Summary(IReadOnlyList<BaseItemViewModel> items, BaseItemDto_CollectionType? kind, DateTimeOffset now)
	{
		if (items.Count == 0)
		{
			return "";
		}

		var unwatched = items.Count(x => x.UserData?.Played != true);
		var added = items.Count(x => x.DateCreated is { } at && now - at <= TimeSpan.FromDays(30));
		var parts = new List<string> { Count(items.Count, kind) };
		if (unwatched > 0)
		{
			parts.Add($"{unwatched.ToString("N0", CultureInfo.CurrentCulture)} unwatched");
		}

		if (added > 0)
		{
			parts.Add($"{added.ToString("N0", CultureInfo.CurrentCulture)} added this month");
		}

		return string.Join(" · ", parts);
	}

	/// <summary>
	/// Nixie 1.3.1 (the UX review's E-M4, WCAG 2.5.3): the Sort button's name starts with the words it shows. Nixie 1.4.0: the
	/// shared words ("Title · A to Z" shows, "Title, A to Z, Sort" is heard), as in every theme.
	/// </summary>
	public static string SortButtonName(ItemSortBy sortBy, SortOrder order, bool inPlaylistOrder, string sortButtonName) =>
		inPlaylistOrder ? $"{LibraryViewModel.PlaylistOrderName}, Sort" : LibraryBrowse.SortButtonName(sortBy, order);

	/// <summary>Nixie 1.3.1: a playlist in its own order says so.</summary>
	public static string SortWords(ItemSortBy sortBy, SortOrder order, bool inPlaylistOrder) =>
		inPlaylistOrder ? LibraryViewModel.PlaylistOrderName : SortWords(sortBy, order);

	/// <summary>Nixie 1.4.0 (item 8): the direction in words on the button in every theme: "Title · A to Z", "Date added · newest first".</summary>
	public static string SortWords(ItemSortBy sortBy, SortOrder order) => LibraryBrowse.SortText(sortBy, order);
}
