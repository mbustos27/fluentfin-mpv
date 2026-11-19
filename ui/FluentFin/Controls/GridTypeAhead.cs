using FluentFin.Core.Shortcuts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0 (R in a grid, <see cref="TypeAheadWord"/>): the grid a title is being typed on, and when its last letter was.
/// The window's keys ask <see cref="Continues"/> before R refreshes: while the word is open on the grid that has the
/// keyboard, R goes on to the grid as a letter.
/// </summary>
internal static class GridTypeAhead
{
	private static WeakReference<UIElement>? _owner;
	private static long _at;

	/// <summary>A letter was typed on <paramref name="owner"/>'s cards (its type-to-jump took it).</summary>
	public static void Typed(UIElement owner)
	{
		_owner = new WeakReference<UIElement>(owner);
		_at = Environment.TickCount64;
	}

	/// <summary>Whether a key on <paramref name="source"/> goes on with the word typed there within the last second.</summary>
	public static bool Continues(object? source) =>
		TypeAheadWord.IsOpen(_at, Environment.TickCount64) && _owner?.TryGetTarget(out var owner) == true && IsWithin(source as DependencyObject, owner);

	private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, ancestor))
			{
				return true;
			}
		}

		return false;
	}
}
