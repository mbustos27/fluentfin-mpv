namespace FluentFin.Core.ViewModels;

/// <summary>
/// Seaglass 1.1.4 (interaction audit, item 5): where the user was on Home when they left it (how far down, and the card: its
/// row's title and its item), so Back puts them there again instead of at the top with the focus on the title bar.
/// </summary>
public sealed record HomePlace(double Offset, string? Row, Guid? ItemId)
{
	private static readonly Lock s_lock = new();
	private static (Guid User, HomePlace Place)? s_place;

	public static void Remember(Guid user, HomePlace place)
	{
		lock (s_lock)
		{
			s_place = (user, place);
		}
	}

	public static HomePlace? Recall(Guid user)
	{
		lock (s_lock)
		{
			return s_place is { } kept && kept.User == user ? kept.Place : null;
		}
	}

	/// <summary>How long Back's restore waits for Home's rows to be laid out as tall as the place, when they have all come.</summary>
	public static readonly TimeSpan LayoutWait = TimeSpan.FromSeconds(1.5);

	/// <summary>How long it waits at most for rows still loading.</summary>
	public static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(4);

	/// <summary>
	/// Whether Back's restore waits a moment more before it scrolls: the page is not yet as tall as the place, and its rows are
	/// still loading (up to <see cref="LoadWait"/>) or, all in, still being laid out (up to <see cref="LayoutWait"/>). Nixie 1.7.4:
	/// Back shows the view model kept (Home has its rows at once), and the restore went at once too, before the rows were laid
	/// out: it scrolled as far as the page then went, the top (Drive-in), and the card left from was not there to take the keyboard.
	/// </summary>
	public static bool Waits(double scrollableHeight, double offset, bool loading, TimeSpan waited) =>
		scrollableHeight < offset - 1 && waited < LoadWait && (loading || waited < LayoutWait);
}
