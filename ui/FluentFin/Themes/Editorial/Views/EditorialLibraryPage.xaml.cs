using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.Themes.Editorial.Controls;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace FluentFin.Themes.Editorial.Views;

/// <summary>The Editorial theme's Library (see EditorialLibraryPage.xaml), over the shared <see cref="LibraryViewModel"/>.</summary>
public sealed partial class EditorialLibraryPage : Page, IEditorialDetailsHost
{
	public LibraryViewModel ViewModel { get; } = App.GetService<LibraryViewModel>();

	public static readonly DependencyProperty PosterWidthProperty =
		DependencyProperty.Register(nameof(PosterWidth), typeof(double), typeof(EditorialLibraryPage), new PropertyMetadata(EditorialLibrary.PosterWidth));

	/// <summary>The posters' width: the row's width shared out (about 136 px at the medium density).</summary>
	public double PosterWidth
	{
		get => (double)GetValue(PosterWidthProperty);
		set => SetValue(PosterWidthProperty, value);
	}

	public static readonly DependencyProperty PosterHeightProperty =
		DependencyProperty.Register(nameof(PosterHeight), typeof(double), typeof(EditorialLibraryPage), new PropertyMetadata(EditorialLibrary.PosterWidth * 1.5));

	/// <summary>The posters' art height (2:3), for the placeholders.</summary>
	public double PosterHeight
	{
		get => (double)GetValue(PosterHeightProperty);
		set => SetValue(PosterHeightProperty, value);
	}

	public static IReadOnlyList<int> Slots(int count) => [.. Enumerable.Range(0, count)];

	// the lines the grid shows; opening or closing details inserts or removes their one line, so the rows stay (and the focus)
	private readonly System.Collections.ObjectModel.ObservableCollection<EditorialLibraryLine> _lines = [];
	private int _columns;
	private Guid? _opened;
	private int _lastIndex;
	private bool _rebuildQueued;
	private FocusState _jumpFocus = FocusState.Keyboard;
	private bool _jumped;
	private bool _focusOnLoad;
	private bool _toTop;
	private bool _compact;
	private double _gridWidth;
	private string _typed = "";
	private long _typedAt;

	public EditorialLibraryPage()
	{
		InitializeComponent();
		// Nixie 1.4.0 (item 15): the first poster's time ([perf]) and the placeholders' cells against the posters', in the log
		// (the artwork: a poster's 2:3 art against the placeholder's; their captions differ in lines, not in place)
		// (the keyboard's poster is lifted and scaled: its art measured from the poster's own place, not as drawn)
		LibraryProbe.Attach(this, Skeleton, Lines, i => LibraryProbe.RectOf(SkeletonArt(i), this), i => PosterArtAt(i), logFirstCard: true);
		((System.Collections.Specialized.INotifyCollectionChanged)ViewModel.Items).CollectionChanged += (_, _) => QueueRebuild();
		ViewModel.PropertyChanged += (_, e) =>
		{
			switch (e.PropertyName)
			{
				case nameof(LibraryViewModel.Facets) or nameof(LibraryViewModel.AppliedFilters) or nameof(LibraryViewModel.CountText):
					UpdateChips();
					break;
				case nameof(LibraryViewModel.SortBy) or nameof(LibraryViewModel.Order):
					_toTop = !ViewModel.IsLoading;
					QueueRebuild();
					break;
				case nameof(LibraryViewModel.FocusRequest) or nameof(LibraryViewModel.IsLoading):
					FocusRequested();
					if (e.PropertyName == nameof(LibraryViewModel.IsLoading))
					{
						Layout(ActualWidth); // the genre row keeps its place while loading (the grid would drop 46 px when it came)
					}

					break;
				case nameof(LibraryViewModel.Density):
					Fit(_gridWidth);
					break;
			}
		};
		ViewModel.Filter.PropertyChanged += (_, _) =>
		{
			// a narrower or wider library starts at its top (the old place would land mid-library)
			_toTop = !ViewModel.IsLoading;
			UpdateChips();
		};
		// R or F5 (a new view model for the same library): the sort, the filters and the poster are put back, as for Back
		// (R and F5 keep the place; the lit tab chosen again starts at the top: LastRefreshKeepsPlace false)
		ViewModel.ReloadedInPlace = () => App.GetService<Contracts.Services.INavigationService>() is NavigationService { LastNavigationWasRefresh: true, LastRefreshKeepsPlace: true };
		Lines.ElementPrepared += Lines_ElementPrepared;
		GridScroller.ViewChanged += (_, _) =>
		{
			UpdateBar();
			// Nixie 1.4.0 (the UX review's E-M5, item 12): scrolled, the head folds to one compact line; at the top it opens
			var compact = GridScroller.VerticalOffset > (_compact ? 8 : 40);
			if (compact != _compact)
			{
				_compact = compact;
				Layout(ActualWidth);
			}
		};
		GridScroller.SizeChanged += (_, e) => Fit(e.NewSize.Width - GridScroller.Padding.Left - GridScroller.Padding.Right);
		ChipScroller.ViewChanged += (_, _) => ShowChipCue();
		ChipScroller.SizeChanged += (_, _) => ShowChipCue();
		ChipStack.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ShowChipCue);
		// Nixie 1.3.1: before the posters' rows see the key (they moved the focus by the arrows' geometry first, so Right from a
		// letter's last title went to the nearest poster below and right, skipping the next title)
		GridList.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Grid_KeyDown), true);
		GridList.GettingFocus += GridList_GettingFocus;
		GridList.CharacterReceived += GridList_CharacterReceived;
		// the poster the keyboard is on is the one Back and R come back to, however it got the focus (a click, Narrator, Tab)
		GridList.GotFocus += (_, e) =>
		{
			if (e.OriginalSource is EditorialPoster { Item: { } item } && IndexOf(item) is >= 0 and var index)
			{
				_lastIndex = index;
				UpdateBar(); // the letter and the readout follow the keyboard
			}
		};
		// Nixie 1.4.0 (the UX review's E-m1, item 4): Right from the last genre goes on to More filters (it stopped there)
		ChipList.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
		{
			if (e.Key == VirtualKey.Right && _chips.Count > 0 && XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), _chips[^1]))
			{
				ChipScroller.ChangeView(ChipScroller.ScrollableWidth, null, null, true);
				e.Handled = MoreFilters.Focus(FocusState.Keyboard);
			}
		}), true);
		Letters.Jumped += (_, e) =>
		{
			_jumpFocus = e.FocusState;
			_jumped = true;
		};
		SizeChanged += (_, e) => Layout(e.NewSize.Width);
		Loaded += (_, _) =>
		{
			AmbientBackdrop.Set(null);
			Layout(ActualWidth);
			UpdateChips();
			QueueRebuild();
			if (_focusOnLoad)
			{
				_focusOnLoad = false;
				FocusRequested();
			}
		};
	}

	// ---- the head: the summary, the chips; folded to one line once the grid scrolls ------------------------------------

	private void Layout(double width)
	{
		if (width <= 0)
		{
			return;
		}

		var narrow = width < 1400;
		// Nixie 1.4.0 (plan item 13): the theme's page grid: its gutter (40 at 1100 to 56 at 1920) and the title at its y (56)
		var margin = PageGrid.GutterAt(width);
		var pageTop = XamlRoot?.Content is UIElement root ? TransformToVisual(root).TransformPoint(default).Y : FluentFin.Controls.TopBar.Height;
		Head.Padding = new Thickness(margin, _compact ? 6 : Core.Theming.PageGridMetrics.TitleTop(PageGrid.Tokens().TitleY, pageTop), margin, 0);
		GridScroller.Padding = new Thickness(margin, 0, margin, 0);
		Skeleton.Margin = new Thickness(margin, 8, margin, 0);
		AppliedFilters.Margin = new Thickness(margin, _compact ? 6 : 12, margin, 0);
		LetterRow.Margin = new Thickness(margin, _compact ? 6 : 12, margin, 8);
		TitleText.FontSize = _compact ? 34 : narrow ? 64 : 88;
		TitleText.LineHeight = _compact ? 38 : narrow ? 64 : 84;
		Arc.Width = narrow ? 190 : 250;
		Arc.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
		SummaryText.Margin = new Thickness(0, 0, 0, _compact ? 6 : 26);
		SortFilterBar.Margin = new Thickness(0, 0, 0, _compact ? 2 : 22);
		// Nixie 1.4.0 (item 15: the placeholders where the posters land): while loading the genre row keeps its height, empty
		ChipRow.Visibility = _compact || (!ViewModel.IsLoading && ViewModel.EditorialTotal == 0) ? Visibility.Collapsed : Visibility.Visible;
		ChipRow.MinHeight = ViewModel.IsLoading ? (double)Application.Current.Resources["NixieCompactButtonHeight"] : 0;
		MoreFilters.Visibility = ViewModel.IsLoading ? Visibility.Collapsed : Visibility.Visible;
	}

	public static string Upper(string? title) => (title ?? "").Trim().ToUpper(System.Globalization.CultureInfo.CurrentCulture);

	private readonly List<EditorialTabButton> _chips = [];

	/// <summary>The chips: All, then each genre with its count against the other filters; the one genre picked lit (several: none).</summary>
	private void UpdateChips()
	{
		SummaryText.Text = ViewModel.EditorialSummary();
		var genres = ViewModel.EditorialGenreChips();
		var names = genres.Select(g => g.Value).Prepend("").ToList();
		if (_chips.Count != names.Count || _chips.Zip(names).Any(x => (string)x.First.Tag != x.Second))
		{
			ChipStack.Children.Clear();
			_chips.Clear();
			foreach (var name in names)
			{
				var chip = new EditorialTabButton { Tag = name, Style = (Style)Application.Current.Resources["EditorialChipStyle"] };
				chip.Click += (sender, _) => ViewModel.EditorialPickGenre((string)((FrameworkElement)sender).Tag is { Length: > 0 } genre ? genre : null);
				_chips.Add(chip);
				ChipStack.Children.Add(chip);
			}
		}

		for (var i = 0; i < _chips.Count; i++)
		{
			var (label, count) = i == 0 ? ("All", ViewModel.EditorialTotal) : (genres[i - 1].Value, genres[i - 1].Count);
			_chips[i].Content = ChipContent(label, count);
			_chips[i].IsSelected = i == 0 ? ViewModel.Filter.Genres.Count == 0 : ViewModel.EditorialGenre == genres[i - 1].Value;
			// Nixie 1.4.0: a genre that would leave nothing is greyed, not hidden (its count follows the other filters)
			_chips[i].IsEnabled = i == 0 || genres[i - 1].IsAvailable;
			AutomationProperties.SetName(_chips[i], i == 0 ? $"All, {count} titles" : genres[i - 1].Name);
			AutomationProperties.SetPositionInSet(_chips[i], i + 1);
			AutomationProperties.SetSizeOfSet(_chips[i], _chips.Count);
		}

		Layout(ActualWidth);
		DispatcherQueue.TryEnqueue(ShowChipCue);
	}

	private static StackPanel ChipContent(string label, int? count)
	{
		var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
		panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
		if (count is { } n)
		{
			panel.Children.Add(new TextBlock
			{
				Text = n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
				FontSize = 13,
				VerticalAlignment = VerticalAlignment.Center,
				Foreground = (Brush)Application.Current.Resources["GlassTextSecondaryBrush"],
			});
		}

		return panel;
	}

	/// <summary>Nixie 1.4.0 (the UX review's E-M1): the chips' row says it goes on (a fade and a chevron), and the chevron turns back at its end.</summary>
	private void ShowChipCue()
	{
		var more = ChipScroller.ScrollableWidth > 1;
		var atEnd = ChipScroller.HorizontalOffset >= ChipScroller.ScrollableWidth - 1;
		ChipFade.Visibility = more && !atEnd ? Visibility.Visible : Visibility.Collapsed;
		ChipMore.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
		ChipMoreGlyph.Glyph = atEnd ? "\uE76B" : "\uE76C";
		var name = atEnd ? "Back to the first genres" : "More genres";
		AutomationProperties.SetName(ChipMore, name);
		ToolTipService.SetToolTip(ChipMore, name);
	}

	private void ChipMore_Click(object sender, RoutedEventArgs e)
	{
		var atEnd = ChipScroller.HorizontalOffset >= ChipScroller.ScrollableWidth - 1;
		ChipScroller.ChangeView(atEnd ? 0 : ChipScroller.HorizontalOffset + ChipScroller.ViewportWidth * 0.8, null, null, !SafeMotion.Allowed);
	}

	/// <summary>More filters: the shared Filter panel (years, ratings, tags, several genres).</summary>
	private void MoreFilters_Click(object sender, RoutedEventArgs e) => SortFilterBar.OpenFilter();

	// ---- the grid's lines ----------------------------------------------------------------------------------------------

	private void Fit(double width)
	{
		if (width <= 0)
		{
			return;
		}

		_gridWidth = width;
		// a narrow window's posters are smaller (the concept's 122 px at 1100), so the row still holds seven; small and large a step either way
		var medium = width < 1300 ? 122 : EditorialLibrary.PosterWidth;
		var poster = LibraryDensities.Width(ViewModel.Density, medium);
		var columns = EditorialLibrary.Columns(width, poster);
		PosterWidth = Math.Floor((width - (columns - 1) * EditorialLibrary.Gap) / columns);
		PosterHeight = Math.Round(PosterWidth * 1.5);
		SkeletonLayout.MinItemWidth = PosterWidth;
		if (columns != _columns)
		{
			_columns = columns;
			QueueRebuild();
		}
	}

	// Nixie 1.4.0: the letters tag the first title of each (sorted by title; a playlist in its own order has none)
	private bool ByLetter => ViewModel.SortBy == ItemSortBy.Name && !ViewModel.InPlaylistOrder;

	private void QueueRebuild()
	{
		if (_rebuildQueued)
		{
			return;
		}

		_rebuildQueued = true;
		DispatcherQueue.TryEnqueue(Rebuild);
	}

	/// <summary>The lines again (the titles, the width, the sort or the open details changed): rows flowing across letters.</summary>
	private void Rebuild()
	{
		_rebuildQueued = false;
		if (_columns == 0)
		{
			return;
		}

		// the poster the keyboard is on keeps it across the rebuild (a window resized, another sort): the rows are made anew,
		// and the focus fell to the title bar
		var keep = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is EditorialPoster { Item: { } kept } poster && IsInside(poster, Lines)
			? (Item: kept, State: poster.FocusState) : default;
		var lines = EditorialLibrary.Flow(ViewModel.Items, _columns, ByLetter, _opened);
		_lines.Clear();
		foreach (var line in lines)
		{
			_lines.Add(line);
		}

		if (Lines.ItemsSource is null)
		{
			Lines.ItemsSource = _lines;
		}

		if (_toTop)
		{
			_toTop = false;
			GridScroller.ChangeView(null, 0, null, disableAnimation: true);
		}

		if (keep.Item is { } item && IndexOf(item) is >= 0 and var index)
		{
			var state = keep.State is FocusState.Unfocused ? FocusState.Programmatic : keep.State;
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
			{
				Lines.UpdateLayout();
				if (!FocusIndex(index, state))
				{
					_jumped = true;
					_jumpFocus = state;
					ViewModel.FocusRequest = index;
				}

				UpdateBar();
			});
		}

		UpdateBar();
	}

	private void Lines_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
	{
		if (args.Element is InlineDetailsRow details && details.Tag is null)
		{
			details.Tag = this;
			details.CloseRequested += (_, _) => CloseDetails(returnFocus: true);
			details.UpRequested += (_, _) => FocusIndex(_lastIndex, FocusState.Keyboard);
		}
	}

	/// <summary>The letter in view (the poster the keyboard is on, else the first row in view) marked in the A to Z, and where the view is.</summary>
	private void UpdateBar()
	{
		if (ViewModel.IsLoading || _lines.Count == 0)
		{
			return;
		}

		if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is EditorialPoster { Item: { } focused } poster && IsInside(poster, Lines))
		{
			ViewModel.ShowingFrom(IndexOf(focused));
			return;
		}

		var top = GridScroller.VerticalOffset;
		for (var i = 0; i < _lines.Count; i++)
		{
			if (_lines[i] is EditorialRowLine row && Lines.TryGetElement(i) is FrameworkElement element && element.ActualOffset.Y + element.ActualHeight * 0.5 > top)
			{
				ViewModel.ShowingFrom(row.FirstIndex);
				return;
			}
		}
	}

	// ---- details in place ----------------------------------------------------------------------------------------------

	public bool IsOpen(BaseItemViewModel item) => _opened is { } id && item.Id == id;

	public void ToggleDetails(BaseItemViewModel item, EditorialPoster poster)
	{
		var index = IndexOf(item);
		if (index >= 0)
		{
			_lastIndex = index;
		}

		var byKeyboard = poster.FocusState is FocusState.Keyboard;
		if (IsOpen(item))
		{
			CloseDetails(returnFocus: byKeyboard);
			return;
		}

		RemoveDetailsLine();
		_opened = item.Id;
		if (Find(index) is not { } at)
		{
			return;
		}

		_lines.Insert(at.Line + 1, new EditorialDetailsLine(item));
		RefreshOpenMarks();
		var line = at.Line + 1;
		// a panel just made is not in the window yet: brought into view and given the keyboard once it is
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (line >= _lines.Count || _lines[line] is not EditorialDetailsLine || Lines.GetOrCreateElement(line) is not InlineDetailsRow details)
			{
				return;
			}

			details.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = SafeMotion.Allowed, VerticalAlignmentRatio = 0.85 });
			if (!byKeyboard)
			{
				return;
			}

			if (details.IsLoaded)
			{
				details.FocusPlay();
				return;
			}

			void FocusWhenLoaded(object sender, RoutedEventArgs e)
			{
				details.Loaded -= FocusWhenLoaded;
				details.FocusPlay();
			}

			details.Loaded += FocusWhenLoaded;
		});
	}

	/// <summary>Each drawn poster shows whether its details are the open ones (the ring and the notch).</summary>
	private void RefreshOpenMarks()
	{
		foreach (var poster in Posters(Lines))
		{
			poster.IsOpen = poster.Item is { } item && IsOpen(item);
		}
	}

	private void RemoveDetailsLine()
	{
		for (var i = _lines.Count - 1; i >= 0; i--)
		{
			if (_lines[i] is EditorialDetailsLine)
			{
				_lines.RemoveAt(i);
			}
		}
	}

	private void CloseDetails(bool returnFocus)
	{
		if (_opened is null)
		{
			return;
		}

		_opened = null;
		RemoveDetailsLine();
		RefreshOpenMarks();
		if (returnFocus)
		{
			Lines.UpdateLayout();
			FocusIndex(_lastIndex, FocusState.Keyboard);
		}
	}

	// ---- the keyboard in the grid ----------------------------------------------------------------------------------------

	private int IndexOf(BaseItemViewModel item)
	{
		for (var i = 0; i < ViewModel.Items.Count; i++)
		{
			if (ReferenceEquals(ViewModel.Items[i], item) || ViewModel.Items[i].Id == item.Id)
			{
				return i;
			}
		}

		return -1;
	}

	private (int Line, int Column)? Find(int index) => index < 0 ? null : EditorialLibrary.Find(_lines, index);

	/// <summary>
	/// Title <paramref name="index"/>'s poster in its row: by its item. Nixie 1.3.1: by its place in the row's children it was
	/// sometimes the wrong one (an ItemsRepeater's children are not in its items' order), so Right from the last title of a
	/// letter skipped the first title of the next.
	/// </summary>
	private EditorialPoster? PosterOf(FrameworkElement row, int index) =>
		index >= 0 && index < ViewModel.Items.Count && ViewModel.Items[index] is { } item
			? Posters(row).FirstOrDefault(p => p.Item is { } shown && (ReferenceEquals(shown, item) || shown.Id == item.Id))
			: null;

	/// <summary>Gives title <paramref name="index"/> the focus (its row made if it is not drawn yet; the focus scrolls it in).</summary>
	private bool FocusIndex(int index, FocusState state)
	{
		if (Find(index) is not { } at || Lines.GetOrCreateElement(at.Line) is not FrameworkElement row)
		{
			return false;
		}

		row.UpdateLayout();
		var poster = PosterOf(row, index);
		if (poster is null)
		{
			return false;
		}

		_lastIndex = index;
		var focused = poster.Focus(state);
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => KeepClearOfBar(poster));
		return focused;
	}

	/// <summary>A focused poster is never left under the section bar (nor below the view): the grid scrolls by what is hidden.</summary>
	private void KeepClearOfBar(FrameworkElement poster)
	{
		if (poster.XamlRoot is null || !poster.IsLoaded)
		{
			return;
		}

		var top = poster.TransformToVisual(GridScroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
		const double clear = 10;
		var bottom = top + poster.ActualHeight;
		double? to = top < clear ? GridScroller.VerticalOffset - (clear - top)
			: bottom > GridScroller.ViewportHeight - 10 ? GridScroller.VerticalOffset + (bottom - GridScroller.ViewportHeight + 10) : null;
		if (to is { } offset)
		{
			GridScroller.ChangeView(null, Math.Clamp(offset, 0, GridScroller.ScrollableHeight), null, disableAnimation: !SafeMotion.Allowed);
		}
	}

	private static IEnumerable<EditorialPoster> Posters(DependencyObject root)
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is EditorialPoster poster)
			{
				yield return poster;
			}
			else
			{
				foreach (var inner in Posters(child))
				{
					yield return inner;
				}
			}
		}
	}

	private void Grid_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Handled || e.OriginalSource is not EditorialPoster { Item: { } item })
		{
			return;
		}

		var index = IndexOf(item);
		var key = e.Key switch
		{
			VirtualKey.Left or VirtualKey.GamepadDPadLeft => "Left",
			VirtualKey.Right or VirtualKey.GamepadDPadRight => "Right",
			VirtualKey.Up or VirtualKey.GamepadDPadUp => "Up",
			VirtualKey.Down or VirtualKey.GamepadDPadDown => "Down",
			VirtualKey.Home => "Home",
			VirtualKey.End => "End",
			// Nixie 1.3.1: a page of rows, as the other themes' grids (CardGrid's Page Up and Page Down)
			VirtualKey.PageUp or VirtualKey.GamepadLeftShoulder => "PageUp",
			VirtualKey.PageDown or VirtualKey.GamepadRightShoulder => "PageDown",
			_ => null,
		};
		if (key is null || index < 0)
		{
			return;
		}

		e.Handled = true;
		// Down from the opened poster's row: into its details
		if (key == "Down" && _opened is not null && Find(index) is { } at && at.Line + 1 < _lines.Count && _lines[at.Line + 1] is EditorialDetailsLine
			&& Lines.TryGetElement(at.Line + 1) is InlineDetailsRow details)
		{
			_lastIndex = index;
			details.FocusPlay();
			return;
		}

		// Up from the first row: out of the grid, to the A-Z (the letter in view) or the chips
		if (key == "Up" && EditorialLibrary.Move(_lines, index, ViewModel.Items.Count, "Up") is null)
		{
			_ = (Letters.Visibility == Visibility.Visible && Letters.FocusCurrent(FocusState.Keyboard))
				|| (ChipRow.Visibility == Visibility.Visible && (_chips.FirstOrDefault(c => c.IsSelected) ?? _chips.FirstOrDefault())?.Focus(FocusState.Keyboard) == true)
				|| SortFilterBar.Focus(FocusState.Keyboard);
			return;
		}

		if (EditorialLibrary.Move(_lines, index, ViewModel.Items.Count, key, RowsPerPage()) is { } target)
		{
			// Nixie 1.3.1: Home from far down the library: the top's rows are long gone (virtualized), and a poster made out
			// there did not take the focus; the grid goes to its top first, and the focus is asked again once laid out
			if (key == "Home")
			{
				GridScroller.ChangeView(null, 0, null, disableAnimation: true);
				GridScroller.UpdateLayout();
			}

			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateBar);

			if (!FocusIndex(target, FocusState.Keyboard))
			{
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusIndex(target, FocusState.Keyboard));
			}
		}
	}

	/// <summary>How many rows of posters the view shows at once (Page Up and Page Down move by them), at least one.</summary>
	private int RowsPerPage()
	{
		var stride = 0d;
		foreach (var poster in Posters(Lines))
		{
			stride = Math.Max(stride, poster.ActualHeight);
		}

		return stride > 0 && GridScroller.ViewportHeight > 0 ? Math.Max(1, (int)Math.Floor(GridScroller.ViewportHeight / (stride + 24))) : 1;
	}

	/// <summary>The keyboard comes into the grid from outside: to the poster it left (or the first).</summary>
	private void GridList_GettingFocus(UIElement sender, GettingFocusEventArgs args)
	{
		if (args.FocusState != FocusState.Keyboard || args.OldFocusedElement is DependencyObject old && IsInside(old, GridList) || ViewModel.Items.Count == 0)
		{
			return;
		}

		var index = Math.Clamp(_lastIndex, 0, ViewModel.Items.Count - 1);
		if (Find(index) is { } at && Lines.TryGetElement(at.Line) is FrameworkElement row && PosterOf(row, index) is { } poster
			&& !ReferenceEquals(poster, args.NewFocusedElement))
		{
			args.TrySetNewFocusedElement(poster);
		}
	}

	private static bool IsInside(DependencyObject element, DependencyObject ancestor)
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

	/// <summary>Nixie 1.4.0 (item 4): letters typed on a poster (within a second of each other) go to the first title they start; one letter again goes on.</summary>
	private void GridList_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
	{
		if (char.IsControl(e.Character) || e.OriginalSource is not EditorialPoster { Item: { } item })
		{
			return;
		}

		var now = Environment.TickCount64;
		if (!FluentFin.Core.Shortcuts.TypeAheadWord.IsOpen(_typedAt, now))
		{
			_typed = "";
		}

		if (e.Character == ' ' && _typed.Length == 0)
		{
			return; // Space presses the poster
		}

		_typedAt = now;
		_typed += e.Character;
		FluentFin.Controls.GridTypeAhead.Typed(sender); // 1.4.0: an R within the second is a letter of the word, not Refresh
		var current = IndexOf(item);
		var query = _typed.Length > 1 && _typed.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(_typed[0])) ? _typed[..1] : _typed;
		if (ViewModel.TypeAhead(query, query.Length == 1 ? current + 1 : Math.Max(current, 0)) is { } target)
		{
			e.Handled = true;
			// a title whose row is drawn: straight to it; one far down goes the A to Z's way (its row made and put at the top
			// once laid out, then focused: a poster made out there from inside the key's handler lost the focus to the title bar)
			if (Find(target) is { } at && Lines.TryGetElement(at.Line) is not null && FocusIndex(target, FocusState.Keyboard))
			{
				return;
			}

			_jumped = true;
			_jumpFocus = FocusState.Keyboard;
			ViewModel.FocusRequest = target;
		}
	}

	/// <summary>Nixie 1.4.0 (item 15, LibraryProbe): the <paramref name="index"/>th poster's art box: the poster's place, the art's size.</summary>
	private Windows.Foundation.Rect? PosterArtAt(int index) =>
		PosterAt(index) is { } poster && LibraryProbe.RectOf(poster, this) is { } at
			? new Windows.Foundation.Rect(at.X, at.Y, at.Width, poster.ArtElement.ActualHeight)
			: null;

	/// <summary>Nixie 1.4.0 (item 15, LibraryProbe): the art placeholder of the <paramref name="index"/>th skeleton cell.</summary>
	private FrameworkElement? SkeletonArt(int index) =>
		Skeleton.TryGetElement(index) is Panel { Children.Count: > 0 } cell ? cell.Children[0] as FrameworkElement : null;

	/// <summary>Nixie 1.4.0 (item 15, LibraryProbe): the <paramref name="index"/>th poster drawn, in reading order (null when not drawn).</summary>
	private EditorialPoster? PosterAt(int index)
	{
		var seen = 0;
		for (var line = 0; line < _lines.Count && line < 8; line++)
		{
			if (Lines.TryGetElement(line) is not { } element)
			{
				continue;
			}

			foreach (var poster in FluentFin.Helpers.ThemeAnchors.Descendants(element).OfType<EditorialPoster>())
			{
				if (seen++ == index)
				{
					return poster;
				}
			}
		}

		return null;
	}

	// ---- A-Z, arrival, Back ------------------------------------------------------------------------------------------

	/// <summary>The title a jump, the arrival or a Back asked for: a jump's row to the top, the title focused (once the lines are there).</summary>
	private void FocusRequested()
	{
		if (ViewModel.IsLoading || (ViewModel.FocusRequest < 0 && ViewModel.ReturnOffset is null))
		{
			return;
		}

		if (!IsLoaded)
		{
			_focusOnLoad = true;
			return;
		}

		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (_rebuildQueued || _columns == 0)
			{
				Rebuild();
			}

			Lines.UpdateLayout();
			var index = ViewModel.FocusRequest;
			ViewModel.FocusRequest = -1;
			var arrival = ViewModel.ArrivalFocus;
			ViewModel.ArrivalFocus = false;
			var jumped = _jumped;
			_jumped = false;
			var state = arrival ? FocusState.Programmatic : _jumpFocus;
			_jumpFocus = FocusState.Keyboard;
			if (ViewModel.ReturnOffset is { } offset)
			{
				ViewModel.ReturnOffset = null;
				GridScroller.UpdateLayout();
				GridScroller.ChangeView(null, Math.Min(offset, GridScroller.ScrollableHeight), null, disableAnimation: true);
				GridScroller.UpdateLayout();
			}
			else if (jumped && index >= 0 && Find(index) is { } at && Lines.GetOrCreateElement(at.Line) is FrameworkElement row)
			{
				// the letter's row to the top of the grid (Nixie 1.4.0: rows flow on, the letter's title may be mid-row)
				Lines.UpdateLayout();
				GridScroller.ChangeView(null, Math.Min(row.ActualOffset.Y, GridScroller.ScrollableHeight), null, disableAnimation: true);
				GridScroller.UpdateLayout();
			}

			if (index >= 0)
			{
				FocusIndex(index, state);
			}

			UpdateBar();
		});
	}

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		ViewModel.ComingBack = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward;
		_jumpFocus = FocusState.Keyboard;
		_jumped = false;
		base.OnNavigatedTo(e);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		var item = _lastIndex >= 0 && _lastIndex < ViewModel.Items.Count ? ViewModel.Items[_lastIndex] : null;
		ViewModel.LeftAt(item, GridScroller.VerticalOffset);
		base.OnNavigatedFrom(e);
	}
}
