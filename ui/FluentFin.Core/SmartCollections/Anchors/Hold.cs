namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>
/// Nixie 1.8 (P1-2, the report's "Stability"): personal slots hold for three days or more unless their anchor changes. A
/// title finished in the last three calendar days comes first (the newest first: "after each finished title"); the rest of the
/// latest anchors take turns, one turn a three-day block of the local calendar, so the slot is the same all those days and
/// the next anchor's after. Pure: the same anchors and date give the same order.
/// </summary>
public static class Hold
{
	/// <summary>How long a personal slot holds.</summary>
	public const int Days = 3;

	/// <summary>The three-day block of a date (its own calendar day): the same for three days, the next one after.</summary>
	public static long Block(DateTimeOffset now) => now.Date.Ticks / TimeSpan.TicksPerDay / Days;

	/// <summary>
	/// The anchors (the latest played first, <see cref="AnchorFinder.Of"/>) in the order the personal slots take them: those
	/// finished on one of the last <see cref="Days"/> calendar days (<see cref="DaysBetween"/>), newest first; then the <see cref="AnchorFinder.Recent"/> latest of the
	/// rest (with every undated one when the dated don't fill them: their order isn't known), turned by the block; then the
	/// older ones.
	/// </summary>
	public static IReadOnlyList<Anchor> Order(IReadOnlyList<Anchor> anchors, DateTimeOffset now)
	{
		bool Fresh(Anchor x) => x.When is { } when && when <= now && DaysBetween(when, now) < Days;
		var fresh = anchors.Where(Fresh).ToList();
		var rest = anchors.Where(x => !Fresh(x)).ToList();
		var dated = rest.Where(x => x.When is not null).ToList();
		List<Anchor> pool = dated.Count >= AnchorFinder.Recent ? [.. dated.Take(AnchorFinder.Recent)] : [.. dated, .. rest.Where(x => x.When is null)];
		var inPool = new HashSet<Anchor>(pool, ReferenceEqualityComparer.Instance);
		var older = rest.Where(x => !inPool.Contains(x));
		return [.. fresh, .. Turned(pool, now), .. older];
	}

	/// <summary>
	/// Calendar days from <paramref name="when"/> to <paramref name="now"/>, both on <paramref name="now"/>'s clock: a title
	/// finished on Saturday evening is fresh on Saturday, Sunday and Monday, all day. Review (Minor): 72 hours from the finish
	/// changed the slot between 09:00 and 22:00 of one day.
	/// </summary>
	public static int DaysBetween(DateTimeOffset when, DateTimeOffset now) =>
		(int)((now.Date.Ticks - when.ToOffset(now.Offset).Date.Ticks) / TimeSpan.TicksPerDay);

	/// <summary>The list turned by the date's block: block 0 as it is, block 1 from its second, and so on.</summary>
	public static IReadOnlyList<T> Turned<T>(IReadOnlyList<T> items, DateTimeOffset now)
	{
		if (items.Count < 2)
		{
			return items;
		}

		var start = (int)(Block(now) % items.Count);
		return [.. items.Skip(start), .. items.Take(start)];
	}
}
