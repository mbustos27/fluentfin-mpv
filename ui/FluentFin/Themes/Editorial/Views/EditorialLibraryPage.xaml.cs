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

	/// <summary>The posters' width: the row's width shared out (about 136 px, twelve across at 1920).</summary>
	public double PosterWidth
	{
		get => (double)GetValue(PosterWidthProperty);
		set => SetValue(PosterWidthProperty, value);
	}

	// the lines the grid shows; opening or closing details inserts or removes their one line, so the rows stay (and the focus)
	private readonly System.Collections.ObjectModel.ObservableCollection<EditorialLibraryLine> _lines = [];
	private int _columns;
	private Guid? _opened;
	private int _lastIndex;
	private bool _rebuildQueued;
	private FocusState _jumpFocus = FocusState.Keyboard;
	private bool _focusOnLoad;
	private bool _toTop;

	public EditorialLibraryPage()
	{
		InitializeComponent();
		((System.Collections.Specialized.INotifyCollectionChanged)ViewModel.Items).CollectionChanged += (_, _) => QueueRebuild();
		ViewModel.PropertyChanged += (_, e) =>
		{
			switch (e.PropertyName)
			{
				case nameof(LibraryViewModel.Facets) or nameof(LibraryViewModel.ActiveFilters) or nameof(LibraryViewModel.CountText):
					UpdateChips();
					break;
				case nameof(LibraryViewModel.SortBy) or nameof(LibraryViewModel.Order):
					_toTop = true;
					UpdateSortMenu();
					QueueRebuild();
					break;
				case nameof(LibraryViewModel.FocusRequest) or nameof(LibraryViewModel.IsLoading):
					FocusRequested();
					break;
			}
		};
		ViewModel.Filter.PropertyChanged += (_, _) =>
		{
			// a narrower or wider library starts at its top (the old place would land mid-section)
			_toTop = true;
			UpdateChips();
		};
		Lines.ElementPrepared += Lines_ElementPrepared;
		GridScroller.ViewChanged += (_, _) => UpdateBar();
		GridScroller.SizeChanged += (_, e) => Fit(e.NewSize.Width - GridScroller.Padding.Left - GridScroller.Padding.Right);
		// Nixie 1.3.1: before the posters' rows see the key (they moved the focus by the arrows' geometry first, so Right from a
		// letter's last title went to the nearest poster below and right, skipping the next title)
		GridList.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Grid_KeyDown), true);
		GridList.GettingFocus += GridList_GettingFocus;
		LetterRepeater.ElementPrepared += (_, e) =>
		{
			AutomationProperties.SetPositionInSet(e.Element, e.Index + 1);
			AutomationProperties.SetSizeOfSet(e.Element, ViewModel.Letters.Count);
			UpdateBar();
		};
		SizeChanged += (_, e) => Layout(e.NewSize.Width);
		Loaded += (_, _) =>
		{
			AmbientBackdrop.Set(null);
			Layout(ActualWidth);
			UpdateSortMenu();
			UpdateChips();
			QueueRebuild();
			if (_focusOnLoad)
			{
				_focusOnLoad = false;
				FocusRequested();
			}
		};
	}

	// ---- the head: the summary, the sort, the chips -------------------------------------------------------------------

	private void Layout(double width)
	{
		if (width <= 0)
		{
			return;
		}

		var narrow = width < 1400;
		var margin = narrow ? 40 : 56;
		Head.Padding = new Thickness(margin, narrow ? 16 : 22, margin, 0);
		GridScroller.Padding = new Thickness(margin, 0, margin, 0);
		SectionBar.Padding = new Thickness(margin, 10, margin, 0);
		Skeleton.Padding = new Thickness(margin, 74, margin, 0);
		TitleText.FontSize = narrow ? 64 : 88;
		TitleText.LineHeight = narrow ? 64 : 84;
		Arc.Width = narrow ? 190 : 250;
	}

	public static string Upper(string? title) => (title ?? "").Trim().ToUpper(System.Globalization.CultureInfo.CurrentCulture);

	private readonly List<EditorialTabButton> _chips = [];

	/// <summary>The chips: All, then each genre with its count; the picked one lit. Unwatched and Favourites are switches.</summary>
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
			AutomationProperties.SetName(_chips[i], i == 0 ? $"All, {count} titles" : genres[i - 1].Name);
			AutomationProperties.SetPositionInSet(_chips[i], i + 1);
			AutomationProperties.SetSizeOfSet(_chips[i], _chips.Count);
		}

		UnwatchedChip.Content = ChipContent("Unwatched", ViewModel.EditorialUnwatched);
		UnwatchedChip.IsChecked = ViewModel.Filter.Unwatched;
		AutomationProperties.SetName(UnwatchedChip, $"Unwatched only, {ViewModel.EditorialUnwatched} titles");
		FavoritesChip.Content = ChipContent("Favorites", null);
		FavoritesChip.IsChecked = ViewModel.Filter.Favorites;
		AutomationProperties.SetName(FavoritesChip, "Favorites only");
		ChipScroller.Visibility = ViewModel.EditorialTotal > 0 ? Visibility.Visible : Visibility.Collapsed;
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
				FontSize = 12,
				VerticalAlignment = VerticalAlignment.Center,
				Foreground = (Brush)Application.Current.Resources["GlassTextSecondaryBrush"],
			});
		}

		return panel;
	}

	private void UnwatchedChip_Click(object sender, RoutedEventArgs e) => ViewModel.Filter.Unwatched = UnwatchedChip.IsChecked == true;

	private void FavoritesChip_Click(object sender, RoutedEventArgs e) => ViewModel.Filter.Favorites = FavoritesChip.IsChecked == true;

	/// <summary>The sort menu: the keys, then the direction (the shared Library's).</summary>
	private void UpdateSortMenu()
	{
		SortMenu.Items.Clear();
		foreach (var key in new[] { ItemSortBy.Name, ItemSortBy.DateCreated, ItemSortBy.PremiereDate, ItemSortBy.CommunityRating, ItemSortBy.CriticRating, ItemSortBy.DatePlayed, ItemSortBy.PlayCount, ItemSortBy.Runtime })
		{
			var item = new RadioMenuFlyoutItem { Text = LibraryBrowse.SortName(key), GroupName = "EditorialSort", IsChecked = ViewModel.SortBy == key };
			item.Click += (_, _) => ViewModel.UpdateSortByCommand.Execute(key);
			SortMenu.Items.Add(item);
		}

		SortMenu.Items.Add(new MenuFlyoutSeparator());
		foreach (var order in new[] { SortOrder.Ascending, SortOrder.Descending })
		{
			var item = new RadioMenuFlyoutItem { Text = order == SortOrder.Ascending ? "Ascending" : "Descending", GroupName = "EditorialOrder", IsChecked = ViewModel.Order == order };
			item.Click += (_, _) => ViewModel.UpdateSortOrderCommand.Execute(order);
			SortMenu.Items.Add(item);
		}
	}

	// ---- the grid's lines ----------------------------------------------------------------------------------------------

	private void Fit(double width)
	{
		if (width <= 0)
		{
			return;
		}

		// a narrow window's posters are smaller (the concept's 122 px at 1100), so the row still holds seven
		var columns = EditorialLibrary.Columns(width, width < 1300 ? 122 : EditorialLibrary.PosterWidth);
		PosterWidth = Math.Floor((width - (columns - 1) * EditorialLibrary.Gap) / columns);
		if (columns != _columns)
		{
			_columns = columns;
			QueueRebuild();
		}
	}

	// Nixie 1.3.1: a playlist in its own order has no letter sections
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

	/// <summary>The lines again (the titles, the width, the sort or the open details changed).</summary>
	private void Rebuild()
	{
		_rebuildQueued = false;
		if (_columns == 0)
		{
			return;
		}

		var lines = EditorialLibrary.Lines(ViewModel.Items, _columns, ByLetter, _opened, ViewModel.Items.Count > 0 ? KindOf(ViewModel.Items[0]) : null);
		_lines.Clear();
		foreach (var line in lines)
		{
			_lines.Add(line);
		}

		if (Lines.ItemsSource is null)
		{
			Lines.ItemsSource = _lines;
		}
		SectionBar.Visibility = ByLetter && _lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		Lines.Margin = new Thickness(0, ByLetter ? 0 : 12, 0, 40);
		if (_toTop)
		{
			_toTop = false;
			GridScroller.ChangeView(null, 0, null, disableAnimation: true);
		}

		UpdateBar();
	}

	private static BaseItemDto_CollectionType? KindOf(BaseItemViewModel item) => item.Dto.Type switch
	{
		BaseItemDto_Type.Movie => BaseItemDto_CollectionType.Movies,
		BaseItemDto_Type.Series => BaseItemDto_CollectionType.Tvshows,
		_ => null,
	};

	private void Lines_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
	{
		if (args.Element is InlineDetailsRow details && details.Tag is null)
		{
			details.Tag = this;
			details.CloseRequested += (_, _) => CloseDetails(returnFocus: true);
			details.UpRequested += (_, _) => FocusIndex(_lastIndex, FocusState.Keyboard);
		}
	}

	/// <summary>The bar shows the section under it: its letter, its count, and the letter underlined in the A-Z.</summary>
	private void UpdateBar()
	{
		if (!ByLetter || _lines.Count == 0)
		{
			return;
		}

		// the first row (drawn) that reaches below the bar: its letter is the section under the bar
		var top = GridScroller.VerticalOffset + SectionBar.Height;
		string? letter = null;
		for (var i = 0; i < _lines.Count && letter is null; i++)
		{
			if (_lines[i] is EditorialRowLine row && Lines.TryGetElement(i) is FrameworkElement element && element.ActualOffset.Y + element.ActualHeight > top)
			{
				letter = row.Letter;
			}
		}

		var current = _lines.OfType<EditorialSectionLine>().FirstOrDefault(s => letter is null || s.Letter == letter);
		if (current is null)
		{
			return;
		}

		BarLetter.Text = current.Letter;
		BarCaption.Text = current.Caption;
		for (var i = 0; i < ViewModel.Letters.Count; i++)
		{
			if (LetterRepeater.TryGetElement(i) is Button { Content: Grid grid } && grid.Children.Count > 1)
			{
				grid.Children[1].Opacity = ViewModel.Letters[i].Letter == current.Letter ? 1 : 0;
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
		var clear = SectionBar.Visibility == Visibility.Visible ? SectionBar.Height + 10 : 10;
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

		// Up from the first row: out of the grid, to the A-Z or the chips
		if (key == "Up" && EditorialLibrary.Move(_lines, index, ViewModel.Items.Count, "Up") is null)
		{
			_ = (SectionBar.Visibility == Visibility.Visible && LetterRepeater.TryGetElement(0) is Control letter && letter.Focus(FocusState.Keyboard))
				|| (_chips.FirstOrDefault(c => c.IsSelected) ?? _chips.FirstOrDefault())?.Focus(FocusState.Keyboard) == true;
			return;
		}

		if (EditorialLibrary.Move(_lines, index, ViewModel.Items.Count, key, RowsPerPage()) is { } target)
		{
			FocusIndex(target, FocusState.Keyboard);
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

	// ---- A-Z, Back ---------------------------------------------------------------------------------------------------

	private void Letter_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: LetterJump jump } button)
		{
			_jumpFocus = button.FocusState is FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
			ViewModel.JumpToCommand.Execute(jump);
		}
	}

	/// <summary>The title a jump or a Back asked for: its section to the top, the title focused (once the lines are there).</summary>
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
			var state = _jumpFocus;
			_jumpFocus = FocusState.Keyboard;
			if (ViewModel.ReturnOffset is { } offset)
			{
				ViewModel.ReturnOffset = null;
				GridScroller.UpdateLayout();
				GridScroller.ChangeView(null, Math.Min(offset, GridScroller.ScrollableHeight), null, disableAnimation: true);
				GridScroller.UpdateLayout();
			}
			else if (index >= 0 && ByLetter && index < ViewModel.Items.Count
				&& EditorialLibrary.SectionOf(_lines, LibraryBrowse.LetterOf(LibraryBrowse.SortKey(ViewModel.Items[index]))) is >= 0 and var section
				&& Lines.GetOrCreateElement(section) is FrameworkElement heading)
			{
				// the letter's section heading to the top, under the bar (it takes the heading's place there)
				Lines.UpdateLayout();
				GridScroller.ChangeView(null, Math.Min(heading.ActualOffset.Y, GridScroller.ScrollableHeight), null, disableAnimation: true);
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
		base.OnNavigatedTo(e);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		var item = _lastIndex >= 0 && _lastIndex < ViewModel.Items.Count ? ViewModel.Items[_lastIndex] : null;
		ViewModel.LeftAt(item, GridScroller.VerticalOffset);
		base.OnNavigatedFrom(e);
	}
}
