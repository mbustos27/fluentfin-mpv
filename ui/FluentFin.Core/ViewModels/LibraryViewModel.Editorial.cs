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
	public int EditorialTotal => _all.Count;

	/// <summary>
	/// "312 films · 41 unwatched · 18 added this month" (over the whole library). Nixie 1.4.1: while the rest of a big library
	/// loads, "Loading… 1,200 of 25,842 films"; when it stopped, "1,200 of 25,842 films · the rest didn't load".
	/// </summary>
	public string EditorialSummary()
	{
		var whole = Math.Max(_all.Count, _serverTotal ?? 0);
		if (_all.Count > 0 && (IsLoadingMore || (LoadStopped && whole > _all.Count)))
		{
			var count = EditorialLibrary.Count(whole, _library?.CollectionType);
			return IsLoadingMore ? $"Loading… {_all.Count:N0} of {count}" : $"{_all.Count:N0} of {count} · the rest didn't load";
		}

		return EditorialLibrary.Summary(_all, _library?.CollectionType, DateTimeOffset.UtcNow);
	}

	/// <summary>The genre chips: the library's genres with how many titles have each, A to Z (the Filter menu's own counts).</summary>
	public IReadOnlyList<FilterOption> EditorialGenreChips() =>
		Facets.FirstOrDefault(f => f.Title == "Genres")?.Options ?? [];

	/// <summary>Titles not watched yet among those the other filters leave, for the Unwatched switch (Nixie 1.4.0: honest under a filter).</summary>
	public int EditorialUnwatched => UnwatchedCount;

	/// <summary>The genre the chips show as picked: the one genre the filter has, null for none (All) or several.</summary>
	public string? EditorialGenre => Filter.Genres.Count == 1 ? Filter.Genres[0] : null;

	/// <summary>
	/// A chip: only that genre (null: All, every genre). Nixie 1.4.0: the chips stay one genre at a time (a tab list); several
	/// genres (OR) are picked in the shared Filter panel ("More filters"), and then no genre chip is lit.
	/// </summary>
	public void EditorialPickGenre(string? genre)
	{
		if (genre == EditorialGenre && (genre is not null || Filter.Genres.Count == 0))
		{
			return;
		}

		Filter.Genres.Clear();
		if (genre is not null)
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
public sealed record EditorialRowLine(IReadOnlyList<BaseItemViewModel> Items, int FirstIndex, string Letter, int Total = 0, IReadOnlyList<string?>? Tags = null) : EditorialLibraryLine
{
	/// <summary>Nixie 1.4.0: the grid's poster size as it is when the row is drawn (null: the medium width).</summary>
	public EditorialGridMetrics? Metrics { get; init; }

	/// <summary>
	/// The row's posters with their place in the library as shown (Narrator's "n of m"), their letter tags, and the posters'
	/// width now (read each time the row is drawn).
	/// </summary>
	public IReadOnlyList<EditorialCell> Cells => [.. Items.Select((item, i) => new EditorialCell(item, FirstIndex + i + 1, Total)
	{
		Tag = Tags is { } tags && i < tags.Count ? tags[i] : null,
		PosterWidth = Metrics?.PosterWidth ?? EditorialLibrary.PosterWidth,
		ListForm = Metrics?.ListForm ?? false,
	})];
}

/// <summary>A poster of a row, with its place: <paramref name="Position"/> of <paramref name="Size"/>.</summary>
public sealed record EditorialCell(BaseItemViewModel Item, int Position, int Size)
{
	/// <summary>Nixie 1.4.0 (the UX review's E-M5, item 12): the letter of the first title of a letter, on its poster ("M"); else null.</summary>
	public string? Tag { get; init; }

	public bool HasTag => !string.IsNullOrEmpty(Tag);

	/// <summary>Nixie 1.4.0: the poster's width when its row was drawn (EditorialGridMetrics).</summary>
	public double PosterWidth { get; init; } = EditorialLibrary.PosterWidth;

	/// <summary>1.6 colpage: drawn as a line of the list view.</summary>
	public bool ListForm { get; init; }
}

/// <summary>
/// Nixie 1.4.0 (the release's layout cycle): the width the Library grid's posters have now, one object the page owns and each
/// row reads as it is drawn, so a poster has its width before it is first measured. A binding to the page reached a new poster
/// only once it was in the window: every row the grid made was measured 12 px taller than it then became, and a far scroll (the
/// scroll bar, Narrator) sent the grid's layout round for 30 s (a LayoutCycleException) and left the window unusable.
/// </summary>
public sealed class EditorialGridMetrics
{
	public double PosterWidth { get; set; } = EditorialLibrary.PosterWidth;

	/// <summary>1.6 colpage: the list view (one title a line) rather than the grid.</summary>
	public bool ListForm { get; set; }
}

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

	/// <summary>
	/// Nixie 1.4.0 (the UX review's E-M5, item 12): the grid's lines with the rows flowing across letters (each letter started a
	/// row: 1 to 11 posters a row, 4 collections on a screen), the first title of each letter tagged with it when
	/// <paramref name="tagLetters"/> (sorted by title); <paramref name="opened"/>'s details follow its row. The rows read their
	/// posters' width from <paramref name="metrics"/> as they are drawn.
	/// </summary>
	public static IReadOnlyList<EditorialLibraryLine> Flow(IReadOnlyList<BaseItemViewModel> items, int columns, bool tagLetters, Guid? opened, EditorialGridMetrics? metrics = null)
	{
		var lines = new List<EditorialLibraryLine>();
		columns = Math.Max(1, columns);
		string? previous = null;
		for (var i = 0; i < items.Count; i += columns)
		{
			var row = new List<BaseItemViewModel>();
			var tags = new List<string?>();
			for (var j = i; j < Math.Min(items.Count, i + columns); j++)
			{
				row.Add(items[j]);
				string? tag = null;
				if (tagLetters)
				{
					var letter = LibraryBrowse.LetterOf(LibraryBrowse.SortKey(items[j]));
					tag = letter != previous ? letter : null;
					previous = letter;
				}

				tags.Add(tag);
			}

			lines.Add(new EditorialRowLine(row, i, tagLetters ? LibraryBrowse.LetterOf(LibraryBrowse.SortKey(row[0])) : "", items.Count, tags) { Metrics = metrics });
			if (opened is { } id && row.FirstOrDefault(x => x.Id == id) is { } item)
			{
				lines.Add(new EditorialDetailsLine(item));
			}
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

	/// <summary>
	/// Nixie 1.4.0 (the release's layout cycle): whether something from <paramref name="top"/> down <paramref name="height"/> (in
	/// the view's own coordinates) shows, at least in part, in a view <paramref name="viewport"/> high.
	/// </summary>
	public static bool PartlyInView(double top, double height, double viewport) =>
		viewport > 0 && height >= 0 && top < viewport && top + height > 0;

	/// <summary>
	/// Nixie 1.4.0 (the release's layout cycle): whether something from <paramref name="top"/> down <paramref name="height"/> (in
	/// the view's own coordinates) lies within one view of a view <paramref name="viewport"/> high: in it, or less than a view
	/// above or below. Further away, the keyboard's poster no longer goes with the view (the keyboard follows the scroll).
	/// </summary>
	public static bool WithinAViewOf(double top, double height, double viewport) =>
		viewport > 0 && height >= 0 && top < 2 * viewport && top + height > -viewport;

	/// <summary>The grid's offset that puts a row whose top is <paramref name="rowTop"/> (in the grid) where the first row sits: 8 px under the view's top.</summary>
	public static double RowToTop(double rowTop, double scrollable) => Math.Clamp(rowTop - 8, 0, Math.Max(0, scrollable));

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

/// <summary>
/// 1.6 colpage (theme polish): Editorial's list view, a magazine's index: each title a line with its small poster, the title,
/// "1979 · 1h 52m · PG", a short line from its overview, and where the user is with it ("Watched", "45% watched", "5 left").
/// View > List in the toolbar, kept per library; the grid's keyboard and Narrator model (one poster a stop, "n of m").
/// </summary>
public static class EditorialListForm
{
	/// <summary>A list line's poster: 64 wide (96 high), and the line's height.</summary>
	public const double ArtWidth = 64, LineHeight = 112;

	/// <summary>The facts: the poster's own line ("1979 · 1h 52m", "3 seasons") and the rating.</summary>
	public static string Facts(BaseItemDto? dto)
	{
		var meta = EditorialCards.PosterMeta(dto);
		var rating = dto?.OfficialRating?.Trim();
		return string.Join(" · ", new[] { meta, rating ?? "" }.Where(x => !string.IsNullOrEmpty(x)));
	}

	/// <summary>The overview's first sentence, at most <paramref name="most"/> characters (cut at a word, with an ellipsis); "" when none.</summary>
	public static string ShortLine(BaseItemDto? dto, int most = 140)
	{
		var text = (dto?.Overview ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
		while (text.Contains("  ", StringComparison.Ordinal))
		{
			text = text.Replace("  ", " ", StringComparison.Ordinal);
		}

		if (text.Length == 0)
		{
			return "";
		}

		var end = text.IndexOfAny(['.', '!', '?']);
		var sentence = end is > 0 and var e && e < most ? text[..(e + 1)] : text;
		if (sentence.Length <= most)
		{
			return sentence;
		}

		var cut = sentence.LastIndexOf(' ', most - 1);
		return $"{sentence[..(cut > most / 2 ? cut : most - 1)].TrimEnd(',', ';', ':', ' ')}…";
	}

	/// <summary>Where the user is with it: "Watched", "45% watched", "5 left" (a show or a set), or "" (not started).</summary>
	public static string State(BaseItemDto? dto)
	{
		if (ItemBadges.ToWatch(dto) > 0)
		{
			return $"{ItemBadges.ToWatchText(dto)} left";
		}

		if (ItemBadges.Watched(dto))
		{
			return "Watched";
		}

		return EditorialCards.Progress(dto) is > 0 and var percent ? $"{Math.Round(percent).ToString(CultureInfo.CurrentCulture)}% watched" : "";
	}
}
