using FluentFin.Controls;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: the SHOWTIMES board's list, a whole season at once (<see cref="Showtime"/> rows): in two columns on a wide
/// window, read down the first column then the second (01 to 04, 05 to 08), one column on a narrow one. One Tab stop per
/// episode as the shared episode list; Up and Down go to the episode before and after, Left and Right to the one beside it in
/// the other column, Home and End to the first and the last; past either end, to what is above or below. A list for Narrator
/// (named by AutomationProperties.Name), its rows "n of m". An ItemsControl (not a virtualizing repeater): Tab reaches them all.
/// </summary>
public sealed partial class ShowtimesList : ItemsControl
{
	public static readonly DependencyProperty NextProperty =
		DependencyProperty.Register(nameof(Next), typeof(object), typeof(ShowtimesList), new PropertyMetadata(null, (d, _) => ((ShowtimesList)d).UpdateNext()));

	/// <summary>The episode Play starts (marked NEXT on its row).</summary>
	public BaseItemDto? Next { get => GetValue(NextProperty) as BaseItemDto; set => SetValue(NextProperty, value); }

	public ShowtimesList()
	{
		IsTabStop = false;
		AddHandler(KeyDownEvent, new KeyEventHandler(List_KeyDown), false);
		BringIntoViewRequested += (_, args) =>
		{
			if (RowOf(args.TargetElement) is null)
			{
				return;
			}

			var rect = args.TargetRect;
			args.TargetRect = new Rect(rect.X, rect.Y - CardGrid.RevealMargin, rect.Width, rect.Height + 2 * CardGrid.RevealMargin);
			args.AnimationDesired = args.AnimationDesired && SafeMotion.Allowed;
		};
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new ShowtimesListAutomationPeer(this);

	/// <summary>The row's "n of m" (0 of 0 for a row not of this list).</summary>
	internal (int Position, int Size) PlaceOf(Showtime row)
	{
		var index = row.Dto is { } dto ? Items.IndexOf(dto) : -1;
		return index < 0 ? (0, 0) : (index + 1, Items.Count);
	}

	internal static ShowtimesList? Of(DependencyObject element)
	{
		for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
		{
			if (parent is ShowtimesList list)
			{
				return list;
			}
		}

		return null;
	}

	/// <summary>The keyboard on the row of <paramref name="episode"/> (or the NEXT row, or the first); false when there is none.</summary>
	public bool FocusEpisode(BaseItemDto? episode, FocusState state = FocusState.Keyboard)
	{
		var index = episode is null ? -1 : Items.IndexOf(episode);
		if (index < 0 && Next is { } next)
		{
			index = Items.IndexOf(next);
		}

		return RowAt(Math.Max(0, index)) is { } row && row.Focus(state);
	}

	private void UpdateNext()
	{
		for (var i = 0; i < Items.Count; i++)
		{
			RowAt(i)?.UpdateNext();
		}
	}

	private Showtime? RowAt(int index) => ContainerFromIndex(index) is DependencyObject container ? Find(container) : null;

	private static Showtime? Find(DependencyObject node)
	{
		if (node is Showtime row)
		{
			return row;
		}

		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
		{
			if (Find(VisualTreeHelper.GetChild(node, i)) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private static Showtime? RowOf(DependencyObject? element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is Showtime row)
			{
				return row;
			}

			if (node is ShowtimesList)
			{
				return null;
			}
		}

		return null;
	}

	/// <summary>How many columns the panel draws (1 or 2).</summary>
	private int Columns => ItemsPanelRoot is ShowtimesPanel panel ? panel.Columns : 1;

	/// <summary>
	/// The row a key goes to from <paramref name="current"/> in a board of <paramref name="count"/> rows in
	/// <paramref name="columns"/> columns read column by column: null when the key leaves the board (past an end, or Left/Right
	/// with no column there).
	/// </summary>
	public static int? KeyTarget(VirtualKey key, int current, int count, int columns)
	{
		if (count <= 0 || current < 0)
		{
			return null;
		}

		var rows = ShowtimesPanel.RowsFor(count, columns);
		int? target = key switch
		{
			VirtualKey.Up or VirtualKey.GamepadDPadUp => current - 1,
			VirtualKey.Down or VirtualKey.GamepadDPadDown => current + 1,
			VirtualKey.Left or VirtualKey.GamepadDPadLeft when columns > 1 => current - rows,
			VirtualKey.Right or VirtualKey.GamepadDPadRight when columns > 1 => current + rows,
			VirtualKey.Home => 0,
			VirtualKey.End => count - 1,
			_ => null,
		};
		return target is { } t && t >= 0 && t < count ? t : null;
	}

	private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

	private void List_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (RowOf(e.OriginalSource as DependencyObject) is not { } row || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.Control))
		{
			return;
		}

		if (e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End
			or VirtualKey.GamepadDPadUp or VirtualKey.GamepadDPadDown or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight))
		{
			return;
		}

		var current = PlaceOf(row).Position - 1;
		if (KeyTarget(e.Key, current, Items.Count, Columns) is { } target)
		{
			RowAt(target)?.Focus(FocusState.Keyboard);
			e.Handled = true;
			return;
		}

		// past the board's top or bottom: the season tubes above, what follows below
		if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.GamepadDPadUp or VirtualKey.GamepadDPadDown)
		{
			e.Handled = CardGrid.MoveFocusOut(this, e.Key is VirtualKey.Up or VirtualKey.GamepadDPadUp ? FocusNavigationDirection.Up : FocusNavigationDirection.Down);
		}
		else
		{
			e.Handled = true; // no column there: stay
		}
	}
}

/// <summary>The board's list is a List for Narrator; its rows are its items.</summary>
internal sealed partial class ShowtimesListAutomationPeer(ShowtimesList owner) : FrameworkElementAutomationPeer(owner)
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

	protected override string GetClassNameCore() => nameof(ShowtimesList);

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override bool IsKeyboardFocusableCore() => false;
}

/// <summary>
/// Drive-in: the SHOWTIMES board's layout: two columns from <see cref="TwoColumnsFrom"/> DIP wide (read down the first,
/// then the second), else one; each row as tall as the taller of the two beside it.
/// </summary>
public sealed partial class ShowtimesPanel : Panel
{
	public const double TwoColumnsFrom = 1100, ColumnGap = 36;

	public int Columns { get; private set; } = 1;

	/// <summary>The rows of a board of <paramref name="count"/> in <paramref name="columns"/> columns.</summary>
	public static int RowsFor(int count, int columns) => Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, columns)));

	/// <summary>Where item <paramref name="index"/> goes: its column and its row.</summary>
	public static (int Column, int Row) CellOf(int index, int count, int columns)
	{
		var rows = RowsFor(count, columns);
		return (index / rows, index % rows);
	}

	private double[] _rowHeights = [];

	protected override Size MeasureOverride(Size availableSize)
	{
		var width = double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width;
		Columns = width >= TwoColumnsFrom && Children.Count > 1 ? 2 : 1;
		var columnWidth = (width - (Columns - 1) * ColumnGap) / Columns;
		var rows = RowsFor(Children.Count, Columns);
		_rowHeights = new double[rows];
		for (var i = 0; i < Children.Count; i++)
		{
			Children[i].Measure(new Size(columnWidth, double.PositiveInfinity));
			var (_, row) = CellOf(i, Children.Count, Columns);
			_rowHeights[row] = Math.Max(_rowHeights[row], Children[i].DesiredSize.Height);
		}

		return new Size(width, _rowHeights.Sum());
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var columnWidth = (finalSize.Width - (Columns - 1) * ColumnGap) / Columns;
		for (var i = 0; i < Children.Count; i++)
		{
			var (column, row) = CellOf(i, Children.Count, Columns);
			var top = _rowHeights.Take(row).Sum();
			Children[i].Arrange(new Rect(column * (columnWidth + ColumnGap), top, columnWidth, row < _rowHeights.Length ? _rowHeights[row] : 0));
		}

		return finalSize;
	}
}
