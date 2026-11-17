namespace FluentFin.ViewModels;

/// <summary>
/// Seaglass 1.1.3 (review): which rail item is marked after a navigation. A rail destination marks itself. A page that is not
/// on the rail (an item page, Jellyseerr) keeps the section it was opened from marked (Home or Libraries, as the UX review
/// wants), but never a footer item: after Settings, then Back to an item page, Settings is no longer marked.
/// </summary>
public static class RailSelection
{
	/// <param name="current">The item marked now (null: none).</param>
	/// <param name="target">The rail item of the page navigated to, or null when that page is not on the rail.</param>
	/// <param name="isFooter">Whether an item is one of the rail's footer items (Settings).</param>
	public static T? Next<T>(T? current, T? target, Func<T, bool> isFooter) where T : class =>
		target ?? (current is not null && isFooter(current) ? null : current);
}
