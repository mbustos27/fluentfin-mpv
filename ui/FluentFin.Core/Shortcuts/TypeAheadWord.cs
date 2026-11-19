namespace FluentFin.Core.Shortcuts;

/// <summary>
/// Nixie 1.4.0 (R in a grid; the coordinator's rule, the user having asked for R to refresh): R refreshes on every page,
/// grids included, so the key does one thing everywhere; while a title is being typed in a grid (a letter typed on a card
/// within the last second) R is a letter of it ("m", "a", "r" goes to "Mar…"). Only a first-letter R refreshes; titles that
/// start with R are on the A to Z's R, which a letter key on the A to Z presses (one key away).
/// </summary>
public static class TypeAheadWord
{
	/// <summary>How long after a letter the next one still adds to the word (the grids' type-to-jump uses the same).</summary>
	public const long WindowMs = 1000;

	/// <summary>Whether a word typed on a grid is still open: its last letter <paramref name="lastTypedAt"/> (ms) within <see cref="WindowMs"/> of <paramref name="now"/>.</summary>
	public static bool IsOpen(long lastTypedAt, long now) => lastTypedAt > 0 && now >= lastTypedAt && now - lastTypedAt <= WindowMs;
}
