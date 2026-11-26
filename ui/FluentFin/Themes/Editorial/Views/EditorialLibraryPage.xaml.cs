using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.Themes.Editorial.Controls;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
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
	// Nixie 1.4.0 (the release's layout cycle): the posters' width, read by each row as it is drawn (EditorialGridMetrics)
	private readonly EditorialGridMetrics _metrics = new();
	private int _viewTicket;
	private bool _followPending;
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
		// Nixie 1.7.5: kept whole by the frame for Back to this very page (its grid as it was, at once); a new visit makes it anew
		// (NavigationService.KeptWhole)
		NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
		// Nixie 1.4.0 (item 15): the first poster's time ([perf]) and the placeholders' cells against the posters', in the log
		// (the artwork: a poster's 2:3 art against the placeholder's; their captions differ in lines, not in place)
		// (the keyboard's poster is lifted and scaled: its art measured from the poster's own place, not as drawn)
		LibraryProbe.Attach(this, Skeleton, Lines, i => LibraryProbe.RectOf(SkeletonArt(i), this), i => PosterArtAt(i), logFirstCard: true);
		System.Collections.Specialized.NotifyCollectionChangedEventHandler itemsChanged = (_, e) =>
		{
			// Nixie 1.4.1: a big library's later pages insert titles before the poster the keyboard was on: its place follows
			// (Back and R come back to the poster, not to whatever moved into its place)
			if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewStartingIndex >= 0 && e.NewStartingIndex <= _lastIndex)
			{
				_lastIndex += e.NewItems?.Count ?? 1;
			}

			// a page's titles: only the lines from the first one they changed are made again (Rebuild)
			_pageArrived |= ViewModel.UpdatingFromPage;
			QueueRebuild();
		};
		// Nixie 1.7.4: only while the page is shown (Back hands a kept view model to a new page: WhileLoaded)
		var items = (System.Collections.Specialized.INotifyCollectionChanged)ViewModel.Items;
		FluentFin.Services.WhileLoaded.Hold(this, () => items.CollectionChanged += itemsChanged, () => items.CollectionChanged -= itemsChanged);
		// Nixie 1.7.4: only while the page is shown (Back hands a kept view model to a new page: WhileLoaded)
		FluentFin.Services.WhileLoaded.Listen(this, ViewModel, (_, e) =>
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
				case nameof(LibraryViewModel.Density) or nameof(LibraryViewModel.Layout): // 1.6 colpage: the list view
					_toTop = !ViewModel.IsLoading;
					Fit(_gridWidth);
					break;
				case nameof(LibraryViewModel.IsLoadingMore) when !ViewModel.IsLoadingMore:
					// Nixie 1.4.1: every page in: the lines once more, whole (each poster's "n of m" counts every title)
					QueueRebuild();
					break;
			}
		});
		// Nixie 1.7.4: only while the page is shown (Back hands a kept view model to a new page: WhileLoaded)
		FluentFin.Services.WhileLoaded.Listen(this, ViewModel.Filter, (_, _) =>
		{
			// a narrower or wider library starts at its top (the old place would land mid-library)
			_toTop = !ViewModel.IsLoading;
			UpdateChips();
		});
		// R or F5 (a new view model for the same library): the sort, the filters and the poster are put back, as for Back
		// (R and F5 keep the place; the lit tab chosen again starts at the top: LastRefreshKeepsPlace false)
		ViewModel.ReloadedInPlace = () => App.GetService<Contracts.Services.INavigationService>() is NavigationService { LastNavigationWasRefresh: true, LastRefreshKeepsPlace: true };
		// after R, a user who moved first keeps the keyboard where they put it (the refresh's own restore yields the same way)
		ViewModel.FocusYields = () => App.GetService<Contracts.Services.INavigationService>() is NavigationService { LastNavigationWasRefresh: true } && PageRefresh.UserMovedSinceRefresh;
		Lines.ElementPrepared += Lines_ElementPrepared;
		GridScroller.ViewChanged += (_, e) =>
		{
			if (!e.IsIntermediate)
			{
				FollowFarScroll();
			}

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
		// Nixie 1.7.4: the rows' wheel (the page's while it can move, else the strip's) and a mouse drag; the chips keep their
		// own fade and chevron for the pointer (EdgeScroller.WheelAndDrag)
		EdgeScroller.WheelAndDrag(ChipScroller, ChipScroller, ChipList);
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
			// (Nixie 1.7.5: or a place asked for while out of the window: the page kept whole for Back hears it only from here)
			if (_focusOnLoad || ViewModel.FocusRequest >= 0 || ViewModel.ReturnOffset is not null)
			{
				_focusOnLoad = false;
				NavigationService.PlaceStep(this, "loaded"); // 1.7.5
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
		// 1.6 colpage (review Major 4): the presets take at most half the chips' row (up to 12 Nixie collections scroll in it), the genres the rest
		PresetStrip.MaxWidth = Math.Max(320, Math.Floor((width - (2 * margin)) * 0.5));
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

	/// <summary>
	/// The chips: each genre with its count against the other filters; the one genre picked lit (several: none). 1.6 colpage:
	/// the presets before them hold the row's All; the lit genre clicked again is off.
	/// </summary>
	private void UpdateChips()
	{
		SummaryText.Text = ViewModel.EditorialSummary();
		var genres = ViewModel.EditorialGenreChips();
		var names = genres.Select(g => g.Value).ToList();
		if (_chips.Count != names.Count || _chips.Zip(names).Any(x => (string)x.First.Tag != x.Second))
		{
			ChipStack.Children.Clear();
			_chips.Clear();
			foreach (var name in names)
			{
				var chip = new EditorialTabButton { Tag = name, Style = (Style)Application.Current.Resources["EditorialChipStyle"] };
				chip.Click += (sender, _) => ViewModel.EditorialPickGenre((string)((FrameworkElement)sender).Tag is { Length: > 0 } genre && genre != ViewModel.EditorialGenre ? genre : null);
				_chips.Add(chip);
				ChipStack.Children.Add(chip);
			}
		}

		for (var i = 0; i < _chips.Count; i++)
		{
			var (label, count) = (genres[i].Value, genres[i].Count);
			_chips[i].Content = ChipContent(label, count);
			_chips[i].IsSelected = ViewModel.EditorialGenre == genres[i].Value;
			// Nixie 1.4.0: a genre that would leave nothing is greyed, not hidden (its count follows the other filters)
			_chips[i].IsEnabled = genres[i].IsAvailable || _chips[i].IsSelected;
			AutomationProperties.SetName(_chips[i], genres[i].Name);
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
		// (the chevron's room after the last chip is not more to see)
		var more = ChipScroller.ScrollableWidth - ChipList.Margin.Right > 1;
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
		// 1.6 colpage (theme polish): the list view is one title a line
		var list = ViewModel.IsList;
		var columns = list ? 1 : EditorialLibrary.Columns(width, poster);
		if (_metrics.ListForm != list)
		{
			_metrics.ListForm = list;
			_columns = 0; // the lines again, in the other form
		}

		PosterWidth = list ? poster : Math.Floor((width - (columns - 1) * EditorialLibrary.Gap) / columns);
		PosterHeight = Math.Round(PosterWidth * 1.5);
		SkeletonLayout.MinItemWidth = PosterWidth;
		if (_metrics.PosterWidth != PosterWidth)
		{
			// the rows drawn from now on read it as they are made; the posters drawn already (and those kept for reuse) change now
			_metrics.PosterWidth = PosterWidth;
			foreach (var drawn in Posters(Lines))
			{
				drawn.PosterWidth = PosterWidth;
			}
		}

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
		var lines = EditorialLibrary.Flow(ViewModel.Items, _columns, ByLetter, _opened, _metrics);
		if (_pageArrived && ViewModel.IsLoadingMore && !_toTop && Lines.ItemsSource is not null)
		{
			// Nixie 1.4.1: a big library's page, while more are coming: a line it left as it was stays (drawn, and the
			// keyboard's poster or the open details keep the keyboard); only a line it changed is replaced, in its place. Each
			// line was made anew on every page, and the keyboard fell to the title bar every couple of seconds while the
			// library loaded. (The last page's rebuild is whole: each poster's "n of m" counts every title.)
			var common = Math.Min(_lines.Count, lines.Count);
			for (var i = 0; i < common; i++)
			{
				if (!SameLine(_lines[i], lines[i]))
				{
					_lines[i] = lines[i];
				}
			}

			for (var i = _lines.Count - 1; i >= lines.Count; i--)
			{
				_lines.RemoveAt(i);
			}

			for (var i = common; i < lines.Count; i++)
			{
				_lines.Add(lines[i]);
			}
		}
		else if (!(_lines.Count == lines.Count && _lines.Zip(lines).All(x => SameDrawn(x.First, x.Second))))
		{
			// (Nixie 1.7.5: the same lines stay as they are: the page kept whole for Back is loaded again with its lines drawn)
			_lines.Clear();
			foreach (var line in lines)
			{
				_lines.Add(line);
			}
		}

		_pageArrived = false;

		if (keep.Item is not null && XamlRoot is { } still && FocusManager.GetFocusedElement(still) is EditorialPoster { Item: var now } && ReferenceEquals(now, keep.Item))
		{
			keep = default; // its line stayed: it has the keyboard still, where it is (no scroll back to it)
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

	/// <summary>Nixie 1.7.5: the same line, drawn the same ("n of m" too): a rebuild leaves it as it is.</summary>
	private static bool SameDrawn(EditorialLibraryLine a, EditorialLibraryLine b) =>
		SameLine(a, b) && (a, b) switch { (EditorialRowLine x, EditorialRowLine y) => x.Total == y.Total, _ => true };

	/// <summary>Nixie 1.4.1: the same line, as a page's rebuild sees it: the same posters in the same place (their "n of m" aside: it is redone once every page is in).</summary>
	private static bool SameLine(EditorialLibraryLine a, EditorialLibraryLine b) => (a, b) switch
	{
		(EditorialRowLine x, EditorialRowLine y) => x.FirstIndex == y.FirstIndex && x.Letter == y.Letter
			&& x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => ReferenceEquals(p.First, p.Second))
			&& (x.Tags ?? []).SequenceEqual(y.Tags ?? []),
		(EditorialDetailsLine x, EditorialDetailsLine y) => ReferenceEquals(x.Item, y.Item),
		_ => a == b,
	};

	/// <summary>The rebuild queued comes from a page arriving (only its changed lines are made again).</summary>
	private bool _pageArrived;

	private void Lines_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
	{
		if (args.Element is InlineDetailsRow details && details.Tag is null)
		{
			details.Tag = this;
			details.CloseRequested += (sender, _) => WhenInView(OpenedIndex(), sender as FrameworkElement, () => CloseDetails(returnFocus: true));
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
		// Nixie 1.4.0 (the release's layout cycle): a poster out of view (Narrator's Invoke; Enter on a poster the scroll bar
		// left far above) is brought into view first; its details open, or close, once the grid is laid out there
		WhenInView(index, poster, () => Toggle(item, byKeyboard));
	}

	private void Toggle(BaseItemViewModel item, bool byKeyboard)
	{
		var index = IndexOf(item);
		if (index >= 0)
		{
			_lastIndex = index;
		}

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

	/// <summary>The opened details' title's place in the library as shown, or -1.</summary>
	private int OpenedIndex()
	{
		for (var i = 0; _opened is { } id && i < ViewModel.Items.Count; i++)
		{
			if (ViewModel.Items[i].Id == id)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>
	/// Nixie 1.4.0 (the release's layout cycle): runs <paramref name="change"/> (details opened or closed: a line in or out of the
	/// grid) at once when <paramref name="shown"/> (the poster or the details it comes from) shows in the view, at least in part.
	/// Else title <paramref name="index"/>'s row comes to the top of the view first, and the change waits until the grid is laid
	/// out there (a line put in or taken out far from the view moved the whole grid while the view was elsewhere). A newer
	/// request, or leaving the page, drops a waiting one.
	/// </summary>
	private void WhenInView(int index, FrameworkElement? shown, Action change)
	{
		var ticket = ++_viewTicket;
		if (index < 0 || Find(index) is not { } at || (shown is not null && Shows(shown)))
		{
			change();
			return;
		}

		BringRowToTop(at.Line);
		var tries = 0;
		var lastOffset = double.NaN;
		void WhenLaidOut()
		{
			if (ticket != _viewTicket || !IsLoaded)
			{
				return;
			}

			// laid out and still: the view at the same place twice running, the row in it (a row just made sits in the view
			// for a pass before the grid has scrolled to its real place)
			GridScroller.UpdateLayout();
			var still = Math.Abs(GridScroller.VerticalOffset - lastOffset) < 0.5;
			lastOffset = GridScroller.VerticalOffset;
			if (still && Find(index) is { } now && Lines.TryGetElement(now.Line) is FrameworkElement row && Shows(row))
			{
				change();
				return;
			}

			if (++tries < 12)
			{
				if (tries == 6 && Find(index) is { } again)
				{
					BringRowToTop(again.Line); // the view moved on meanwhile (the wheel): once more
				}

				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, WhenLaidOut);
				return;
			}

			// it never came into view: nothing changes (opening it far from the view is what hung the window)
			Serilog.Log.Warning("[library] Editorial: title {Index}'s details not changed: its row did not come into view", index);
		}

		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, WhenLaidOut);
	}

	/// <summary>
	/// Nixie 1.4.0 (the release's layout cycle): the keyboard follows a far scroll. When the view settles more than a view away
	/// from the poster the keyboard is on (the scroll bar, Narrator, the wheel: a scroll the keyboard did not make), the keyboard
	/// goes to the first poster in view, as it was (the keyboard's, or the program's). The grid keeps the keyboard's row drawn
	/// wherever the view goes; the first row kept far above made every later layout pass lay out all the rows from the top again
	/// (each image that arrived: 1 to 2 s frozen, for up to 30 s). Keys move the keyboard and the view together, so they never
	/// leave it a view away.
	/// </summary>
	private void FollowFarScroll()
	{
		// the poster the keyboard was on when the view moved; checked once the grid is laid out at the new place (when the view
		// reports the change its content has not moved yet), before any later pass: not on a callback for when it is idle (a
		// grid laying its rows out again is never idle: one came 5 s late), nor after a forced layout (the keyboard fell to the
		// title bar's Back)
		if (_followPending || XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not EditorialPoster { Item: not null } focused
			|| !IsInside(focused, Lines))
		{
			return;
		}

		_followPending = true;
		var state = focused.FocusState is FocusState.Unfocused ? FocusState.Programmatic : focused.FocusState;
		void LaidOut(object? sender, object e)
		{
			GridScroller.LayoutUpdated -= LaidOut;
			_followPending = false;
			var now = FocusManager.GetFocusedElement(root) as DependencyObject;
			// that poster is far now (or gone), and the keyboard is still on it or fell out of the grid with it (nobody put it
			// elsewhere in the grid)
			var far = !WithinAView(focused) && (ReferenceEquals(now, focused) || now is null || !IsInside(now, GridList));
			if (far && IsLoaded && FirstPosterInView() is { } first)
			{
				first.Focus(state);
			}
		}

		GridScroller.LayoutUpdated += LaidOut;
	}

	/// <summary>Whether <paramref name="element"/> (in the grid) lies within one view of the view (a poster kept far away for the keyboard is parked at no size: not).</summary>
	private bool WithinAView(FrameworkElement element)
	{
		if (!element.IsLoaded || element.XamlRoot is null || element.ActualHeight <= 0)
		{
			return false;
		}

		var top = element.TransformToVisual(GridScroller).TransformPoint(default).Y;
		return EditorialLibrary.WithinAViewOf(top, element.ActualHeight, GridScroller.ViewportHeight);
	}

	/// <summary>The first poster of the first row whose top shows in the view (else of the first row that shows at all).</summary>
	private EditorialPoster? FirstPosterInView()
	{
		FrameworkElement? partly = null;
		var partlyIndex = -1;
		for (var i = 0; i < _lines.Count; i++)
		{
			if (_lines[i] is not EditorialRowLine line || Lines.TryGetElement(i) is not FrameworkElement { ActualHeight: > 0 } row || !Shows(row))
			{
				continue;
			}

			if (row.TransformToVisual(GridScroller).TransformPoint(default).Y >= -1)
			{
				return PosterOf(row, line.FirstIndex);
			}

			if (partly is null)
			{
				partly = row;
				partlyIndex = line.FirstIndex;
			}
		}

		return partly is null ? null : PosterOf(partly, partlyIndex);
	}

	/// <summary>Whether <paramref name="element"/> (in the grid) shows in its view, at least in part.</summary>
	private bool Shows(FrameworkElement element)
	{
		if (!element.IsLoaded || element.XamlRoot is null || !IsInside(element, Lines))
		{
			return false;
		}

		var top = element.TransformToVisual(GridScroller).TransformPoint(default).Y;
		return EditorialLibrary.PartlyInView(top, element.ActualHeight, GridScroller.ViewportHeight);
	}

	/// <summary>
	/// Line <paramref name="line"/> to the top of the view, without animation: a line laid out in its place, by its place; one not
	/// drawn, or one the grid only keeps for the keyboard (parked out of the way at no size), is made in its place and brought
	/// in the repeater's own way (its place is known only once the grid has scrolled there).
	/// </summary>
	private void BringRowToTop(int line)
	{
		if (Lines.TryGetElement(line) is FrameworkElement { ActualHeight: > 0 } row)
		{
			GridScroller.ChangeView(null, EditorialLibrary.RowToTop(row.ActualOffset.Y + Lines.Margin.Top, GridScroller.ScrollableHeight), null, disableAnimation: true);
			return;
		}

		if (Lines.GetOrCreateElement(line) is FrameworkElement made)
		{
			made.UpdateLayout();
			made.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
		}
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
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			// 1.8.1: while Back's place lands, the clearance waits for it (read from where the grid was, it moved the page away)
			if (PlaceHoldNow() == PlaceHold.Hold)
			{
				_clearOnLanding = poster;
				SayHeld("the bar's clearance");
				return;
			}

			KeepClearOfBar(poster);
		});
		return focused;
	}

	// ---- 1.8.1 (1.7.5's release gate, Editorial's library at max): Back's place held while its scroll lands -----------------
	// Back put the grid back at 1,253 px, then it moved to 302 px: the poster left from was given the keyboard while the scroll
	// was still on its way, and its bring-into-view (the focus's, and the bar's clearance) was worked out from where the grid
	// was, below the view: once the place had landed, the page moved to bring the poster up from the view's foot. Until the
	// place lands (or HoldMs passes, or the user moves) the grid's bring-into-view is let go and the clearance waits for it;
	// the poster is in view at its place, as it was left.

	// the offset Back's place is landing on (null: none landing), until when it is held, and the arrival it is held for
	private double? _placeLanding;
	private long _placeHeldUntil;
	private long _placeHeldArrived;
	private FrameworkElement? _clearOnLanding;
	private bool _placeWatched;
	private long _placeAskedAt;
	private string? _placeHeld; // what the hold first held this Back (null: nothing yet)

	private void HoldPlace(double target)
	{
		if (!_placeWatched)
		{
			_placeWatched = true;
			GridScroller.ViewChanged += (_, e) =>
			{
				if (!e.IsIntermediate)
				{
					_ = PlaceHoldNow(); // landed (the waiting nudge runs), or over (it goes), or still on its way
				}
			};
			GridList.BringIntoViewRequested += (_, args) =>
			{
				if (PlaceHoldNow() == PlaceHold.Hold)
				{
					args.Handled = true;
					SayHeld("a bring-into-view");
				}
			};
		}

		_placeHeld = null;
		_placeLanding = target;
		_placeAskedAt = Environment.TickCount64;
		_placeHeldUntil = _placeAskedAt + FluentFin.Views.CollectionPage.HoldMs;
		_placeHeldArrived = ArrivalFocus.ArrivedAt;
		_ = PlaceHoldNow(); // (already there: nothing to hold)
	}

	/// <summary>1.8.1: what Back's place hold is at a moment.</summary>
	public enum PlaceHold
	{
		/// <summary>No place is landing.</summary>
		None,

		/// <summary>Still on its way: the grid's bring-into-view is let go and the bar's clearance waits.</summary>
		Hold,

		/// <summary>The scroll is where the place asked: the clearance that waited runs, from there.</summary>
		Landed,

		/// <summary>Over without landing (<see cref="FluentFin.Views.CollectionPage.HoldMs"/> passed, or the user moved): what waited goes.</summary>
		Ended,
	}

	/// <summary>
	/// 1.8.1 (the review): the hold's step. It ends when the user moves (theirs is theirs) or its time is up, landed or not: a
	/// place still noted after that, and its waiting nudge, went with it (a later scroll to that offset nudged the grid).
	/// </summary>
	public static PlaceHold HoldStep(bool landing, bool atTarget, bool userMoved, long now, long until) =>
		!landing ? PlaceHold.None
		: userMoved || now > until ? PlaceHold.Ended
		: atTarget ? PlaceHold.Landed
		: PlaceHold.Hold;

	// the hold's step now, and what follows from it: a landing runs the waiting clearance, an end drops it
	private PlaceHold PlaceHoldNow()
	{
		var step = HoldStep(_placeLanding is not null, _placeLanding is { } landing && Math.Abs(GridScroller.VerticalOffset - landing) < 1,
			ArrivalFocus.UserActedAt > _placeHeldArrived + ArrivalFocus.GraceMs, Environment.TickCount64, _placeHeldUntil);
		if (step is PlaceHold.Landed or PlaceHold.Ended)
		{
			_placeLanding = null;
			var poster = _clearOnLanding;
			_clearOnLanding = null;
			if (_placeHeld is { } what)
			{
				App.GetService<ILogger<EditorialLibraryPage>>().LogInformation("[perf] back: EditorialLibraryPage's place hold {End} after {Ms} ms (it held {What})",
					step == PlaceHold.Landed ? "landed" : "ended without landing", Environment.TickCount64 - _placeAskedAt, what);
			}

			if (step == PlaceHold.Landed && poster is not null)
			{
				KeepClearOfBar(poster); // from where the place put the grid: a poster under the bar or past the view's foot is nudged
			}
		}

		return step;
	}

	// rule 10: once a Back, when the hold first holds something (a duration only)
	private void SayHeld(string what)
	{
		if (_placeHeld is not null)
		{
			return;
		}

		_placeHeld = what;
		App.GetService<ILogger<EditorialLibraryPage>>().LogInformation("[perf] back: EditorialLibraryPage held {What} while its place landed, {Ms} ms after the place was asked",
			what, Environment.TickCount64 - _placeAskedAt);
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
		// Nixie 1.4.0: Esc on the poster whose details are open closes them (the keyboard can be on the poster with its details
		// open: Up from them, or the grid moved it there when a far scroll let the details go), the poster keeps the keyboard
		if (e.Key is VirtualKey.Escape or VirtualKey.GamepadB && IsOpen(item) && index >= 0)
		{
			e.Handled = true;
			WhenInView(index, e.OriginalSource as FrameworkElement, () => CloseDetails(returnFocus: true));
			return;
		}

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
				|| (ChipRow.Visibility == Visibility.Visible && (_chips.FirstOrDefault(c => c.IsSelected) ?? (Control?)PresetStrip.Buttons.FirstOrDefault(b => b.IsChecked == true) ?? _chips.FirstOrDefault())?.Focus(FocusState.Keyboard) == true)
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

		// Nixie 1.7.5: Back's place (a kept library shown as it was) at the normal priority: on the low one it waited for the
		// posters' work, and the page showed its top for up to 2.5 s under load before it moved to where it was
		DispatcherQueue.TryEnqueue(FluentFin.Controls.LibraryGridHost.BackPlacePriority(ViewModel.ReturnOffset is not null, NavigationService.PuttingBack(this)), () =>
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
			// Nixie 1.4.1: a place put back late takes the keyboard only from the grid or from nothing (else it only scrolls)
			var late = ViewModel.LateFocus;
			ViewModel.LateFocus = false;
			var elsewhere = late && XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused
				&& !ReferenceEquals(focused, this) && !IsInside(focused, GridScroller);
			var jumped = _jumped;
			_jumped = false;
			var state = arrival ? FocusState.Programmatic : _jumpFocus;
			_jumpFocus = FocusState.Keyboard;
			if (ViewModel.ReturnOffset is { } offset)
			{
				ViewModel.ReturnOffset = null;
				GridScroller.UpdateLayout();
				NavigationService.PlaceStep(this, $"scroll asked ({(int)Math.Min(offset, GridScroller.ScrollableHeight)} of {(int)offset} px)"); // 1.7.5
				var target = Math.Min(offset, GridScroller.ScrollableHeight);
				GridScroller.ChangeView(null, target, null, disableAnimation: true);
				GridScroller.UpdateLayout();
				HoldPlace(target); // 1.8.1
			}
			else if (jumped && index >= 0 && Find(index) is { } at && Lines.GetOrCreateElement(at.Line) is FrameworkElement row)
			{
				// the letter's row to the top of the grid (Nixie 1.4.0: rows flow on, the letter's title may be mid-row)
				Lines.UpdateLayout();
				GridScroller.ChangeView(null, Math.Min(row.ActualOffset.Y, GridScroller.ScrollableHeight), null, disableAnimation: true);
				GridScroller.UpdateLayout();
			}

			if (index >= 0 && ViewModel.FocusYields?.Invoke() != true && !elsewhere)
			{
				FocusIndex(index, state);
			}

			UpdateBar();
			if (index >= 0)
			{
				NavigationService.PlacePutBack(this, "place"); // (a pass with nothing asked of it: no line)
			}
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
