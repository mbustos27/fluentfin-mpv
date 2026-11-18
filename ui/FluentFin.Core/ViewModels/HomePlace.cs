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
}
