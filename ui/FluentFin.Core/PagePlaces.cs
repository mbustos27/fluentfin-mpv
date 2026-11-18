namespace FluentFin.Core;

/// <summary>
/// 1.2.0: where the user was on a page when they left it: how far down it was scrolled and what had the keyboard
/// focus (a card or an episode row by its item, anything else by its place in the page's element tree).
/// </summary>
/// <param name="FocusFromKeyboard">The focus got there by the keyboard (Tab, the arrows): it comes back as keyboard focus,
/// with its ring and a card's buttons; focus a click gave comes back without them (items review, item 5).</param>
/// <param name="SelectedSeasonId">The series page's shown season (items review, item 1).</param>
public sealed record PagePlace(double VerticalOffset, Guid? FocusedItemId, IReadOnlyList<int>? FocusedPath, bool FocusFromKeyboard = false, Guid? SelectedSeasonId = null);

/// <summary>
/// 1.2.0 (1.1.3 UX audit, interaction item 5): the item pages give their place back when the user returns to them
/// with Back (or Forward): the scroll position and the focus, which went to the top and to the title bar because every Back
/// makes the page again. One place per page and item, the latest kept; a new visit (not Back or Forward) starts at the top.
/// </summary>
public static class PagePlaces
{
	/// <summary>Enough for any Back history a person walks; the oldest places go first.</summary>
	public const int Capacity = 64;

	private static readonly object Gate = new();
	private static readonly LinkedList<(string Key, PagePlace Place)> Places = new();

	public static string Key(string page, Guid? itemId) => $"{page}|{itemId?.ToString("N") ?? "-"}";

	public static void Save(string key, PagePlace place)
	{
		lock (Gate)
		{
			Remove(key);
			Places.AddFirst((key, place));
			while (Places.Count > Capacity)
			{
				Places.RemoveLast();
			}
		}
	}

	/// <param name="returning">The page is shown again by Back or Forward; a new visit gets nothing (it starts at the top).</param>
	public static PagePlace? ForReturn(string key, bool returning)
	{
		if (!returning)
		{
			return null;
		}

		lock (Gate)
		{
			return Places.FirstOrDefault(x => x.Key == key).Place;
		}
	}

	/// <summary>Forgets every place (a sign-out, a test).</summary>
	public static void Clear()
	{
		lock (Gate)
		{
			Places.Clear();
		}
	}

	private static void Remove(string key)
	{
		for (var node = Places.First; node is not null; node = node.Next)
		{
			if (node.Value.Key == key)
			{
				Places.Remove(node);
				return;
			}
		}
	}

	/// <summary>
	/// The offset to scroll back to once the page is tall enough: the saved one, or as far as the page goes when it is shorter now
	/// (the content changed); null while the content is still arriving and the page might yet grow (<paramref name="loaded"/> false).
	/// </summary>
	public static double? OffsetToRestore(double saved, double scrollableHeight, bool loaded)
	{
		if (saved <= 0)
		{
			return 0;
		}

		if (scrollableHeight >= saved - 0.5)
		{
			return saved;
		}

		return loaded ? Math.Max(0, scrollableHeight) : null;
	}
}
