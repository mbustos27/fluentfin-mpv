using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>Nixie 1.4.0: the orders a collection or a playlist can be shown in (its own first).</summary>
public enum CollectionOrder
{
	/// <summary>The collection's own order: a box set's DisplayOrder (release date when unset), a playlist's as the server keeps it.</summary>
	Collection,
	ReleaseDate,
	Name,
	DateAdded,
}

/// <summary>What Play does on a collection page: the film it starts, and whether it resumes it or starts it again.</summary>
public readonly record struct CollectionNext(BaseItemDto? Item, int Position, bool Resume, bool Again);

/// <summary>
/// Nixie 1.4.0 (the UX review's plan item 1; X1, CL-M4): what the shared Collection page says and does, apart from the page so
/// it can be tested: the collection's own order (the server's DisplayOrder, release date when unset), its summary line
/// ("6 films · 1979–2016 · 1 of 6 watched"), the film Play starts (the first unwatched in collection order) and the words of
/// its buttons.
/// </summary>
public static class CollectionBrowse
{
	/// <summary>Fewer items than this and the page has no A to Z (a jump list is for sets too big to scan).</summary>
	public const int LettersFrom = 30;

	/// <summary>
	/// The order a box set asks for: Jellyfin's BoxSet DisplayOrder is "SortName" (by name) or "PremiereDate" (by release
	/// date, its default); anything else or nothing is release date, as Infuse and jellyfin-web fall back to.
	/// </summary>
	public static CollectionOrder OwnOrder(string? displayOrder) =>
		displayOrder?.Trim().ToLowerInvariant() is "sortname" or "name" ? CollectionOrder.Name : CollectionOrder.ReleaseDate;

	/// <summary>
	/// The items in <paramref name="order"/>. <paramref name="serverOrder"/> is the order the server gave them in (a playlist's
	/// own); a box set's own order is its DisplayOrder's.
	/// </summary>
	public static IReadOnlyList<BaseItemDto> Ordered(IReadOnlyList<BaseItemDto> serverOrder, CollectionOrder order, bool playlist, string? displayOrder)
	{
		var places = serverOrder.Select((item, i) => (item, i)).ToDictionary(x => x.item, x => x.i, ReferenceEqualityComparer.Instance);
		int Place(BaseItemDto item) => places.TryGetValue(item, out var i) ? i : int.MaxValue;

		// 1.7 trending: a Nixie collection in its list's order (a rank): the order the titles came in
		if (order == CollectionOrder.Collection && !playlist && displayOrder == ListOrder)
		{
			return [.. serverOrder];
		}
		// end 1.7 trending

		if (order == CollectionOrder.Collection && !playlist && displayOrder == TrackOrder)
		{
			return [.. serverOrder.OrderBy(x => x.ParentIndexNumber ?? 0).ThenBy(x => x.IndexNumber ?? int.MaxValue).ThenBy(Place)];
		}

		var effective = order == CollectionOrder.Collection && !playlist ? OwnOrder(displayOrder) : order;
		IEnumerable<BaseItemDto> sorted = effective switch
		{
			CollectionOrder.Collection => serverOrder.OrderBy(Place),
			CollectionOrder.ReleaseDate => serverOrder.OrderBy(ReleaseKey).ThenBy(SortKey, StringComparer.CurrentCultureIgnoreCase).ThenBy(Place),
			CollectionOrder.Name => serverOrder.OrderBy(SortKey, StringComparer.CurrentCultureIgnoreCase).ThenBy(Place),
			CollectionOrder.DateAdded => serverOrder.OrderByDescending(x => x.DateCreated ?? DateTimeOffset.MinValue).ThenBy(Place),
			_ => serverOrder,
		};
		return [.. sorted];
	}

	/// <summary>When a film came out: its premiere date, else 1 January of its year; undated films last.</summary>
	public static DateTimeOffset ReleaseKey(BaseItemDto item) =>
		item.PremiereDate ?? (item.ProductionYear is int year and > 0 ? new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero) : DateTimeOffset.MaxValue);

	/// <summary>What a title sorts under: the server's SortName ("Matrix" for "The Matrix"), else its name.</summary>
	public static string SortKey(BaseItemDto item) => string.IsNullOrWhiteSpace(item.SortName) ? item.Name ?? "" : item.SortName;

	public static bool IsWatched(BaseItemDto item) => item.UserData?.Played == true;

	/// <summary>
	/// What Play and Shuffle can start: a film, an episode, a show, a video; not a collection inside the collection, a folder or
	/// a playlist (the player has nothing to queue for them: they open their own page instead). 1.5 (BH-140-m5): a home video
	/// and a music video too (the player plays each alone, MpvQueue), so a playlist of them has Play and Shuffle.
	/// </summary>
	public static bool IsPlayable(BaseItemDto item) =>
		item.Type is BaseItemDto_Type.Movie or BaseItemDto_Type.Episode or BaseItemDto_Type.Series or BaseItemDto_Type.Season
			or BaseItemDto_Type.Trailer or BaseItemDto_Type.Video or BaseItemDto_Type.MusicVideo;

	/// <summary>The display order an album keeps: its discs and tracks.</summary>
	public const string TrackOrder = "IndexNumber";

	// 1.7 trending
	/// <summary>The display order of a Nixie collection kept in its list's order (trending's rank): never a server value.</summary>
	public const string ListOrder = "Nixie.ListOrder";
	// end 1.7 trending

	/// <summary>"Collection", "Playlist" or "Album": what the page is (its eyebrow, the list's name, the empty state).</summary>
	public static string KindOf(BaseItemDto? collection) => collection?.Type switch
	{
		BaseItemDto_Type.Playlist => "Playlist",
		BaseItemDto_Type.MusicAlbum or BaseItemDto_Type.PhotoAlbum => "Album",
		_ => "Collection",
	};

	/// <summary>The noun for the items: "film"/"films" for films, "show", "episode", else "item".</summary>
	public static (string One, string Many) Noun(IReadOnlyList<BaseItemDto> items)
	{
		var types = items.Select(x => x.Type).Distinct().ToList();
		return types.Count != 1 ? ("item", "items") : types[0] switch
		{
			BaseItemDto_Type.Movie => ("film", "films"),
			BaseItemDto_Type.Series => ("show", "shows"),
			BaseItemDto_Type.Episode => ("episode", "episodes"),
			BaseItemDto_Type.MusicVideo => ("music video", "music videos"),
			BaseItemDto_Type.Video => ("video", "videos"),
			BaseItemDto_Type.Audio => ("song", "songs"),
			_ => ("item", "items"),
		};
	}

	public static string Count(int n, (string One, string Many) noun) => $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? noun.One : noun.Many)}";

	/// <summary>"1979–2016", "1979" for one year, "" when no item has a year.</summary>
	public static string YearSpan(IReadOnlyList<BaseItemDto> items)
	{
		var years = items.Select(x => x.ProductionYear ?? x.PremiereDate?.Year).Where(y => y is > 0).Select(y => y!.Value).ToList();
		if (years.Count == 0)
		{
			return "";
		}

		var (first, last) = (years.Min(), years.Max());
		return first == last ? first.ToString(CultureInfo.InvariantCulture) : $"{first.ToString(CultureInfo.InvariantCulture)}–{last.ToString(CultureInfo.InvariantCulture)}";
	}

	/// <summary>"6 films · 1979–2016 · 1 of 6 watched" (a playlist: no years); "" when there is nothing.</summary>
	public static string Summary(IReadOnlyList<BaseItemDto> items, bool playlist)
	{
		if (items.Count == 0)
		{
			return "";
		}

		var parts = new List<string> { Count(items.Count, Noun(items)) };
		if (!playlist && YearSpan(items) is { Length: > 0 } span)
		{
			parts.Add(span);
		}

		parts.Add($"{items.Count(IsWatched).ToString("N0", CultureInfo.CurrentCulture)} of {items.Count.ToString("N0", CultureInfo.CurrentCulture)} watched");
		return string.Join(" · ", parts);
	}

	/// <summary>
	/// Over the cards: "5 of 6 films" when Hide watched hides some; else "" (1.6 colpage, C2: the summary already says "6 films",
	/// so the grid's heading shows only when it says something new).
	/// </summary>
	public static string ShownText(int shown, IReadOnlyList<BaseItemDto> items) =>
		items.Count == 0 || shown == items.Count ? ""
		: $"{shown.ToString("N0", CultureInfo.CurrentCulture)} of {Count(items.Count, Noun(items))}";

	// ---- 1.6 colpage (LB1, C3): a card's place in the set, only where the order means something ----------------------------

	/// <summary>
	/// Whether the set's own order means something, so its cards carry their place ("1 · Wild Canyons"): a playlist (the user's
	/// order), an album (its tracks), a box set in release order (a franchise); not a box set its admin sorted by name (A to Z
	/// says nothing a title doesn't), and never in another order picked here.
	/// </summary>
	public static bool PositionsMeanSomething(CollectionOrder order, bool playlist, string? displayOrder) =>
		order == CollectionOrder.Collection && (playlist || displayOrder == TrackOrder || displayOrder == ListOrder /* 1.7 trending */ || OwnOrder(displayOrder) == CollectionOrder.ReleaseDate);

	/// <summary>The card's caption prefix: "1" before "· Wild Canyons" (the separator is the card's).</summary>
	public static string CaptionPrefix(int position) => position.ToString(CultureInfo.CurrentCulture);

	// ---- 1.6 colpage (C1, C5): what a theme's header stage shows ------------------------------------------------------------

	/// <summary>How many of the set's films a header lists ("01 … 06": Editorial's contents); more are "and N more".</summary>
	public const int ContentsShown = 8;

	/// <summary>
	/// The header's facts, in a shape a Nixie collection can fill too: the eyebrow ("Collection", "Made by Nixie · Franchise"),
	/// the title, the count ("6 films"), a why line (a Nixie collection's source; "" for a server's set), the art (the set's
	/// Backdrop, else its first member's in the set's order, else up to four posters for a 2×2 mosaic), the films as a numbered
	/// contents list ("01", "Wild Canyons", "1979"), and the marquee's words ("NOW SHOWING: COPPER AND GLASS · 6 FEATURES").
	/// </summary>
	public static CollectionHeaderFacts Header(BaseItemDto? set, IReadOnlyList<BaseItemDto> inOwnOrder, string kind, bool numbered, string eyebrow = "", string why = "")
	{
		var title = set?.Name ?? "";
		var noun = Noun(inOwnOrder);
		var count = inOwnOrder.Count == 0 ? "" : Count(inOwnOrder.Count, noun);
		var backdrop = HasBackdrop(set) ? set : inOwnOrder.FirstOrDefault(HasBackdrop);
		var mosaic = backdrop is null ? inOwnOrder.Where(HasPrimary).Take(4).ToList() : [];
		var width = Math.Max(2, inOwnOrder.Count.ToString(CultureInfo.InvariantCulture).Length);
		var contents = inOwnOrder.Take(ContentsShown).Select((x, i) => new CollectionContentsLine(
			numbered ? (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') : "",
			x.Name ?? "",
			(x.ProductionYear ?? x.PremiereDate?.Year) is int y and > 0 ? y.ToString(CultureInfo.InvariantCulture) : "")).ToList();
		var more = Math.Max(0, inOwnOrder.Count - ContentsShown);
		var features = inOwnOrder.Count == 0 ? "" : noun.One == "film"
			? $"{inOwnOrder.Count.ToString("N0", CultureInfo.CurrentCulture)} {(inOwnOrder.Count == 1 ? "FEATURE" : "FEATURES")}"
			: count.ToUpper(CultureInfo.CurrentCulture);
		var marquee = string.IsNullOrEmpty(features) ? $"NOW SHOWING: {title.ToUpper(CultureInfo.CurrentCulture)}" : $"NOW SHOWING: {title.ToUpper(CultureInfo.CurrentCulture)} · {features}";
		return new CollectionHeaderFacts(string.IsNullOrEmpty(eyebrow) ? kind : eyebrow, title, count, why, backdrop, mosaic, contents,
			more > 0 ? $"and {more.ToString("N0", CultureInfo.CurrentCulture)} more" : "", features, marquee);
	}

	/// <summary>The item has a backdrop of its own (a box set's, a film's).</summary>
	public static bool HasBackdrop(BaseItemDto? item) => item?.BackdropImageTags is { Count: > 0 } tags && !string.IsNullOrEmpty(tags[0]);

	/// <summary>The item has a poster.</summary>
	public static bool HasPrimary(BaseItemDto? item) => item?.ImageTags?.AdditionalData?.ContainsKey(nameof(ImageType.Primary)) == true;

	/// <summary>How much of the collection is watched, 0 to 1 (the progress bar).</summary>
	public static double Progress(IReadOnlyList<BaseItemDto> items) => items.Count == 0 ? 0 : (double)items.Count(IsWatched) / items.Count;

	/// <summary>
	/// What Play starts, from the items in collection order: the first one not watched (resumed when it has a resume point);
	/// when every one is watched, the first, again. Its position is 1-based.
	/// </summary>
	public static CollectionNext Next(IReadOnlyList<BaseItemDto> inCollectionOrder)
	{
		var first = -1;
		for (var i = 0; i < inCollectionOrder.Count; i++)
		{
			var item = inCollectionOrder[i];
			if (!IsPlayable(item))
			{
				continue;
			}

			first = first < 0 ? i : first;
			if (!IsWatched(item))
			{
				return new CollectionNext(item, i + 1, item.UserData?.PlaybackPositionTicks is > 0, Again: false);
			}
		}

		return first < 0 ? default : new CollectionNext(inCollectionOrder[first], first + 1, Resume: false, Again: true);
	}

	/// <summary>The Primary button's words: "Resume Forest of Brightwater", "Play Copper Mine", "Play again from the start".</summary>
	public static string PlayLabel(CollectionNext next) => next.Item is null ? "Play"
		: next.Again ? "Play again from the start"
		: $"{(next.Resume ? "Resume" : "Play")} {next.Item.Name}";

	/// <summary>The Primary button for Narrator, with where the film is in the set: "Resume Forest of Brightwater, film 3 of 6".</summary>
	public static string PlayName(CollectionNext next, IReadOnlyList<BaseItemDto> items) => next.Item is null ? "Play"
		: next.Again ? $"Play again from the start: {next.Item.Name}, {Noun(items).One} {next.Position.ToString(CultureInfo.CurrentCulture)} of {items.Count.ToString(CultureInfo.CurrentCulture)}"
		: $"{PlayLabel(next)}, {Noun(items).One} {next.Position.ToString(CultureInfo.CurrentCulture)} of {items.Count.ToString(CultureInfo.CurrentCulture)}";

	/// <summary>What Shuffle plays: one of the items not watched yet, picked by <paramref name="pick"/> (0 to n-1); every item when all are watched.</summary>
	public static BaseItemDto? Shuffle(IReadOnlyList<BaseItemDto> items, Func<int, int> pick)
	{
		var pool = items.Where(x => IsPlayable(x) && !IsWatched(x)).ToList();
		if (pool.Count == 0)
		{
			pool = [.. items.Where(IsPlayable)];
		}

		return pool.Count == 0 ? null : pool[Math.Clamp(pick(pool.Count), 0, pool.Count - 1)];
	}

	/// <summary>The Order button's words: "Order: Collection" ("Order: Playlist" for a playlist's own order).</summary>
	public static string OrderLabel(CollectionOrder order, bool playlist) => $"Order: {OrderName(order, playlist)}";

	public static string OrderName(CollectionOrder order, bool playlist) => order switch
	{
		CollectionOrder.Collection => playlist ? "Playlist" : "Collection",
		CollectionOrder.ReleaseDate => "Release date",
		CollectionOrder.Name => "Name",
		CollectionOrder.DateAdded => "Date added",
		_ => order.ToString(),
	};

	/// <summary>The words of an empty page, and of one whose items are all hidden by Hide watched.</summary>
	public static (string Title, string Message) EmptyText(string kind, string server) => kind switch
	{
		"Playlist" => ("This playlist is empty", $"Items added to it on {server} appear here."),
		"Album" => ("This album is empty", $"What is added to it on {server} appears here."),
		_ => ("No films in this collection you can see", $"Films added to it on {server} appear here."),
	};

	/// <summary>"All 6 films are watched", for Hide watched with nothing left to show.</summary>
	public static string AllWatchedText(IReadOnlyList<BaseItemDto> items) =>
		items.Count == 1 ? $"The one {Noun(items).One} is watched" : $"All {Count(items.Count, Noun(items))} are watched";
}

/// <summary>1.6 colpage (C5): one line of a header's contents list: "01", "Wild Canyons", "1979" (no number when the order means nothing).</summary>
public sealed record CollectionContentsLine(string Number, string Title, string Year)
{
	/// <summary>The line as one string, for a theme's single text run: "01  Wild Canyons  1979".</summary>
	public string Text => string.Join("  ", new[] { Number, Title, Year }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>
/// 1.6 colpage (C1, C5): what a Collection page's header stage shows, the same for a server's box set or playlist and a Nixie
/// collection (the collections stream fills <see cref="Eyebrow"/> and <see cref="Why"/>). <see cref="BackdropFrom"/> is the
/// item whose Backdrop is drawn (the set's own, else its first member's); with none, <see cref="Mosaic"/> holds up to four
/// members whose posters make a 2×2 mosaic.
/// </summary>
public sealed record CollectionHeaderFacts(
	string Eyebrow,
	string Title,
	string CountWords,
	string Why,
	BaseItemDto? BackdropFrom,
	IReadOnlyList<BaseItemDto> Mosaic,
	IReadOnlyList<CollectionContentsLine> Contents,
	string ContentsMore,
	string Features,
	string Marquee)
{
	public static CollectionHeaderFacts Empty { get; } = new("", "", "", "", null, [], [], "", "", "");
}
