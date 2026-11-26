using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.8.1 (the mem review): a card's place in its set for Narrator ("3 of 24": PositionInSet, SizeOfSet), held by the
/// element that holds the card (the Collection page's entry) and read by the card's peer when Narrator asks. The Collection
/// page knows an entry's place when its ItemsRepeater prepares the entry, before the theme's PosterTemplate has made the card
/// inside it (a ContentPresenter's content): a place set on the card then found no card, and since 1.6 cards 2 to 24 said no
/// place at all (after Back, all but the last two). As <see cref="CardCaption"/> hands an entry's caption to its card.
/// </summary>
public static class CardPlace
{
	public static readonly DependencyProperty PositionProperty =
		DependencyProperty.RegisterAttached("Position", typeof(int), typeof(CardPlace), new PropertyMetadata(0));

	public static readonly DependencyProperty SizeProperty =
		DependencyProperty.RegisterAttached("Size", typeof(int), typeof(CardPlace), new PropertyMetadata(0));

	public static int GetPosition(DependencyObject element) => (int)element.GetValue(PositionProperty);

	public static void SetPosition(DependencyObject element, int value) => element.SetValue(PositionProperty, value);

	public static int GetSize(DependencyObject element) => (int)element.GetValue(SizeProperty);

	public static void SetSize(DependencyObject element, int value) => element.SetValue(SizeProperty, value);

	/// <summary><paramref name="holder"/>'s card is the <paramref name="index"/>-th (from 0) of <paramref name="count"/>.</summary>
	public static void Set(DependencyObject holder, int index, int count)
	{
		SetPosition(holder, index + 1);
		SetSize(holder, count);
	}

	/// <summary>The place the nearest holder gives <paramref name="card"/> (a few levels up, as CardCaption looks); null for none.</summary>
	public static (int Position, int Size)? Of(DependencyObject card)
	{
		var node = VisualTreeHelper.GetParent(card);
		for (var depth = 0; node is not null && depth < 6; depth++, node = VisualTreeHelper.GetParent(node))
		{
			if (node.ReadLocalValue(PositionProperty) != DependencyProperty.UnsetValue && GetPosition(node) > 0)
			{
				return (GetPosition(node), GetSize(node));
			}
		}

		return null;
	}

	/// <summary>A card peer's PositionInSet: the card's own (<paramref name="own"/>, AutomationProperties), else its holder's.</summary>
	public static int Position(int own, DependencyObject? card) => own > 0 || card is null ? own : Of(card)?.Position ?? own;

	/// <summary>A card peer's SizeOfSet: the card's own, else its holder's.</summary>
	public static int Size(int own, DependencyObject? card) => own > 0 || card is null ? own : Of(card)?.Size ?? own;
}
