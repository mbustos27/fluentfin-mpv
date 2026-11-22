using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Controls;

/// <summary>
/// 1.6 colpage (LB1): a card whose caption can lead with its place in a set ("1 · Wild Canyons"): the shared card and each
/// theme's own poster. The art's top-left square stays for counts only.
/// </summary>
public interface ICaptionPrefixed
{
	string CaptionPrefix { get; set; }
}

/// <summary>
/// 1.6 colpage (LB1): a place for the card inside an element (the Collection page's entry): <c>lc:CardCaption.Prefix="{x:Bind
/// CaptionPrefix}"</c> on the entry hands it to the card the theme's PosterTemplate made in it, whichever comes first: the
/// value pushed down when it changes (an entry recycled for another film), or pulled up by the card when it loads (a card
/// made after the value was set).
/// </summary>
public static class CardCaption
{
	public static readonly DependencyProperty PrefixProperty =
		DependencyProperty.RegisterAttached("Prefix", typeof(string), typeof(CardCaption), new PropertyMetadata(null, OnPrefixChanged));

	public static string? GetPrefix(DependencyObject element) => (string?)element.GetValue(PrefixProperty);

	public static void SetPrefix(DependencyObject element, string? value) => element.SetValue(PrefixProperty, value);

	private static void OnPrefixChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (Find(d) is { } card)
		{
			card.CaptionPrefix = e.NewValue as string ?? "";
		}
	}

	/// <summary>A card that just loaded takes the place its nearest holder gives (a few levels up), if any.</summary>
	public static void Pull(DependencyObject card)
	{
		if (card is not ICaptionPrefixed captioned)
		{
			return;
		}

		var node = VisualTreeHelper.GetParent(card);
		for (var depth = 0; node is not null && depth < 6; depth++, node = VisualTreeHelper.GetParent(node))
		{
			if (node.ReadLocalValue(PrefixProperty) != DependencyProperty.UnsetValue)
			{
				captioned.CaptionPrefix = GetPrefix(node) ?? "";
				return;
			}
		}
	}

	private static ICaptionPrefixed? Find(DependencyObject root)
	{
		var queue = new Queue<(DependencyObject Node, int Depth)>([(root, 0)]);
		while (queue.Count > 0)
		{
			var (node, depth) = queue.Dequeue();
			if (!ReferenceEquals(node, root) && node is ICaptionPrefixed card)
			{
				return card;
			}

			if (depth < 6)
			{
				for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
				{
					queue.Enqueue((VisualTreeHelper.GetChild(node, i), depth + 1));
				}
			}
		}

		return null;
	}
}
