namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0 (the library UX review, X9): which card has the keyboard's focus, in every theme (the shared card, Cinematic's
/// still, Editorial's poster). While one has it, a card under a resting pointer only brightens: it never lifts, zooms or shows
/// its actions or a Play glyph, so the keyboard's card is the one card that looks current. One window, one owner.
/// </summary>
internal static class CardFocus
{
	private static WeakReference<object>? _owner;

	/// <summary>The keyboard's card changed (another card took it, or it left the cards).</summary>
	public static event EventHandler? Changed;

	/// <summary>Whether a card other than <paramref name="card"/> has the keyboard's focus.</summary>
	public static bool IsElsewhere(object card) => Owner is { } owner && !ReferenceEquals(owner, card);

	internal static object? Owner => _owner is not null && _owner.TryGetTarget(out var owner) ? owner : null;

	/// <summary><paramref name="card"/> has the keyboard's focus now.</summary>
	public static void Take(object card)
	{
		if (ReferenceEquals(Owner, card))
		{
			return;
		}

		_owner = new WeakReference<object>(card);
		Changed?.Invoke(null, EventArgs.Empty);
	}

	/// <summary><paramref name="card"/> lost the keyboard's focus (to something that is not one of its own parts).</summary>
	public static void Release(object card)
	{
		if (!ReferenceEquals(Owner, card))
		{
			return;
		}

		_owner = null;
		Changed?.Invoke(null, EventArgs.Empty);
	}
}
