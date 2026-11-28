using System.Globalization;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.8 (the reverse toggle): what an order runs by, so its direction has words. The order a page offers decides it (a
/// Collection page's Name runs <see cref="AToZ"/>, its Release date <see cref="OldestFirst"/>, its Date added
/// <see cref="NewestFirst"/>, a playlist's own order <see cref="FirstToLast"/>); the toggle only reverses it.
/// </summary>
public enum OrderRuns
{
	/// <summary>Titles A to Z; reversed, Z to A.</summary>
	AToZ,

	/// <summary>Dates, oldest first (a release order, a franchise); reversed, newest first.</summary>
	OldestFirst,

	/// <summary>Dates, newest first (date added); reversed, oldest first.</summary>
	NewestFirst,

	/// <summary>A list's own sequence (a playlist, an album's tracks, a list's ranks); reversed, last to first.</summary>
	FirstToLast,

	/// <summary>Scores, highest first (a rating, Made by Nixie's best first); reversed, lowest first.</summary>
	HighestFirst,

	/// <summary>1.9: the categories "For you" (the lineup's ranking: the best fit first); reversed, the best fit last.</summary>
	ForYou,
}

/// <summary>
/// Nixie 1.8 (the reverse toggle; the owner, 2026-11-23: "we should be able to reverse the direction of the sort in
/// collections"): the one sort-direction setting the Collection page (collections, playlists, Made by Nixie) and the
/// Collections page's grid share. It reverses whichever order is chosen, and is kept like Order and Hide watched: for the
/// session, per user and per view, put back by Back, Forward and R (<see cref="Remember"/>, <see cref="Recall"/>); a view
/// opened anew starts as its order runs (not reversed).
/// </summary>
/// <remarks>
/// The button is an icon button beside Order, as the Library's direction button is (<see cref="LibraryBrowse.SortGlyph"/>):
/// its width never changes when it flips (rule 7: no jump), its glyph points the way the cards run, and its Narrator name says
/// the direction first and then what a press does: "Newest first, switch to Oldest first" (<see cref="ButtonName"/>).
/// </remarks>
public static class SortDirections
{
	// ---- the words ---------------------------------------------------------------------------------------------------------

	/// <summary>The direction the cards run in, capitalised as a label: "A to Z", "Z to A", "Oldest first", "Newest first", "First to last".</summary>
	public static string Words(OrderRuns runs, bool reversed) => (runs, reversed) switch
	{
		(OrderRuns.AToZ, false) => "A to Z",
		(OrderRuns.AToZ, true) => "Z to A",
		(OrderRuns.OldestFirst, false) or (OrderRuns.NewestFirst, true) => "Oldest first",
		(OrderRuns.OldestFirst, true) or (OrderRuns.NewestFirst, false) => "Newest first",
		(OrderRuns.HighestFirst, false) => "Highest first",
		(OrderRuns.HighestFirst, true) => "Lowest first",
		(OrderRuns.ForYou, false) => "Best fit first", // 1.9
		(OrderRuns.ForYou, true) => "Best fit last",
		(_, false) => "First to last",
		(_, true) => "Last to first",
	};

	/// <summary>The words of the other direction (what a press switches to): "Z to A" for "A to Z".</summary>
	public static string OtherWords(OrderRuns runs, bool reversed) => Words(runs, !reversed);

	/// <summary>
	/// The button for Narrator, the direction first and then what a press does (as the Library's direction button says it):
	/// "Newest first, switch to Oldest first"; "A to Z, switch to Z to A".
	/// </summary>
	public static string ButtonName(OrderRuns runs, bool reversed) =>
		$"{Words(runs, reversed)}, switch to {Lower(OtherWords(runs, reversed))}";

	/// <summary>The tooltip: "Switch to Z to A".</summary>
	public static string ToolTip(OrderRuns runs, bool reversed) => $"Switch to {Lower(OtherWords(runs, reversed))}";

	/// <summary>Whether the cards run up (A to Z, oldest first, first to last, lowest first): the arrow's way.</summary>
	public static bool RunsUp(OrderRuns runs, bool reversed) => (runs is OrderRuns.NewestFirst or OrderRuns.HighestFirst or OrderRuns.ForYou) == reversed;

	/// <summary>The arrow on the button, the Library's (Segoe Fluent Icons): up while the cards run up, down while they run down.</summary>
	public static string Glyph(OrderRuns runs, bool reversed) =>
		LibraryBrowse.SortGlyph(RunsUp(runs, reversed) ? Jellyfin.Sdk.Generated.Models.SortOrder.Ascending : Jellyfin.Sdk.Generated.Models.SortOrder.Descending);

	// "A to Z" keeps its capitals mid-sentence; "Oldest first" becomes "oldest first"
	private static string Lower(string words) =>
		words.Length > 1 && char.IsUpper(words[0]) && !char.IsUpper(words[1]) && words[1] != ' '
			? char.ToLower(words[0], CultureInfo.CurrentCulture) + words[1..]
			: words;

	/// <summary>
	/// What a Collection page's order runs by: Name A to Z, Release date oldest first, Date added newest first; its own order
	/// as the set keeps it (a box set's DisplayOrder: by name A to Z, else release order; a playlist, an album's tracks and a
	/// ranked list first to last). The arguments are <see cref="CollectionBrowse.Ordered"/>'s.
	/// </summary>
	public static OrderRuns Runs(CollectionOrder order, bool playlist, string? displayOrder) => order switch
	{
		CollectionOrder.Name => OrderRuns.AToZ,
		CollectionOrder.ReleaseDate => OrderRuns.OldestFirst,
		CollectionOrder.DateAdded => OrderRuns.NewestFirst,
		_ when playlist || displayOrder is CollectionBrowse.TrackOrder or CollectionBrowse.ListOrder => OrderRuns.FirstToLast,
		_ => CollectionBrowse.OwnOrder(displayOrder) == CollectionOrder.Name ? OrderRuns.AToZ : OrderRuns.OldestFirst,
	};

	// ---- the order ---------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The items in the direction picked: as the order gave them, or mirrored, every item at the other end (undated films
	/// too: the order runs backwards, nothing is sorted again).
	/// </summary>
	public static IReadOnlyList<T> Apply<T>(IReadOnlyList<T> ordered, bool reversed)
	{
		if (!reversed)
		{
			return ordered;
		}

		var mirrored = new T[ordered.Count];
		for (var i = 0; i < ordered.Count; i++)
		{
			mirrored[ordered.Count - 1 - i] = ordered[i];
		}

		return mirrored;
	}

	/// <summary>
	/// Review (Minor 4): the items in the direction picked, those <paramref name="last"/> says have no place in the order (an
	/// undated film in a release order) at the end both ways, in the order they came: "Newest first" starts with the newest, not
	/// with the films that have no date. Null <paramref name="last"/>: <see cref="Apply{T}(IReadOnlyList{T}, bool)"/>.
	/// </summary>
	public static IReadOnlyList<T> Apply<T>(IReadOnlyList<T> ordered, bool reversed, Func<T, bool>? last)
	{
		if (!reversed || last is null)
		{
			return Apply(ordered, reversed);
		}

		var placed = ordered.Where(x => !last(x)).Reverse().ToList();
		placed.AddRange(ordered.Where(last));
		return placed;
	}

	/// <summary>
	/// Review (Minor 4): what a Collection page's order puts last as having no place in it, so a reversed order keeps it last:
	/// a release order's films with no date (<see cref="CollectionBrowse.ReleaseKey"/>), date added's with none; nothing for
	/// A to Z or a list's own sequence. The arguments are <see cref="Runs"/>'.
	/// </summary>
	public static Func<Jellyfin.Sdk.Generated.Models.BaseItemDto, bool>? Undated(CollectionOrder order, bool playlist, string? displayOrder) =>
		Runs(order, playlist, displayOrder) switch
		{
			OrderRuns.OldestFirst => x => x.PremiereDate is null && x.ProductionYear is not > 0,
			OrderRuns.NewestFirst => x => x.DateCreated is null,
			_ => null,
		};

	// ---- kept for the session ----------------------------------------------------------------------------------------------

	/// <summary>The view a Collection page keeps its direction under: one collection, playlist or Made by Nixie collection.</summary>
	public static string ForCollection(Guid collection) => $"collection:{collection:N}";

	/// <summary>The view a Made by Nixie collection keeps its direction under (its own id, not a server's).</summary>
	public static string ForNixie(string nixieId) => $"nixie:{nixieId}";

	/// <summary>The view the Collections page's grid (See all) keeps its direction under: its section and, for Made by Nixie, its kind.</summary>
	public static string ForGrid(CollectionsSection section, SmartCollections.SmartCollectionKind? kind) =>
		kind is { } k ? $"grid:{section}:{k}" : $"grid:{section}";

	/// <summary>The app's one keep (the pages' directions this session).</summary>
	public static SortDirectionKeep Shared { get; } = new();

	/// <summary>
	/// The user's change, kept for the session: Back, Forward and R put it back (<see cref="Recall"/>). Call it from the user's
	/// press only, never when a page opens with its default (a view opened anew would put its default over the direction an
	/// earlier visit left, and Back to that visit would lose it: CollectionPlaces' rule).
	/// </summary>
	public static void Remember(Guid user, string view, bool reversed) => Shared.Remember(user, view, reversed);

	/// <summary>The direction kept for the view, or null when the user never flipped it this session.</summary>
	public static bool? Recall(Guid user, string view) => Shared.Recall(user, view);
}

/// <summary>
/// Nixie 1.8: the directions the user flipped this session, per user and view (<see cref="SortDirections.Shared"/>; tests make
/// their own). The newest <c>kept</c> are kept, as CollectionPlaces keeps its newest 32.
/// </summary>
public sealed class SortDirectionKeep(int kept = 64)
{
	private readonly Lock _lock = new();
	private readonly List<(Guid User, string View, bool Reversed)> _kept = [];

	public void Remember(Guid user, string view, bool reversed)
	{
		lock (_lock)
		{
			_kept.RemoveAll(x => x.User == user && x.View == view);
			_kept.Add((user, view, reversed));
			if (_kept.Count > kept)
			{
				_kept.RemoveAt(0);
			}
		}
	}

	public bool? Recall(Guid user, string view)
	{
		lock (_lock)
		{
			for (var i = _kept.Count - 1; i >= 0; i--)
			{
				if (_kept[i].User == user && _kept[i].View == view)
				{
					return _kept[i].Reversed;
				}
			}

			return null;
		}
	}
}
