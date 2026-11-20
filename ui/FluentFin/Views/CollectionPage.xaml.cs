using System.ComponentModel;
using System.Globalization;
using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Views;

/// <summary>Nixie 1.4.0: the shared Collection page (CollectionViewModel; the XAML says what is where).</summary>
public sealed partial class CollectionPage : Page
{
	public CollectionViewModel ViewModel { get; } = App.GetService<CollectionViewModel>();

	private bool _focusedOnArrival;

	// the card the user left the page from, per collection: Back (or Forward) puts the keyboard on it again, as the library does
	private static readonly Dictionary<Guid, Guid> LeftFrom = [];
	private Guid? _returnTo;
	private Guid? _lastEntry;

	public CollectionPage()
	{
		InitializeComponent();

		GridList.GotFocus += (_, e) => Remember(e.OriginalSource as DependencyObject);
		GridList.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => Remember(e.OriginalSource as DependencyObject)), true);

		// each card's place for Narrator ("3 of 6"): the card (the theme's poster) is the element it reads
		GridRepeater.ElementPrepared += (_, e) =>
		{
			SetPlace(e.Element, e.Index);
			if (_itemWidth <= 0 && !_widthQueued && e.Element is FrameworkElement)
			{
				// Nixie 1.4.0 (bug hunt M3): the poster's width is measured, and the grid's spacing set, after the repeater's
				// layout pass, not inside it (one of the crash's suspects; not its cause, but a layout changed inside its own
				// pass is how layout cycles start)
				_widthQueued = true;
				var card = e.Element;
				UiDispatch.Enqueue(DispatcherQueue, "the Collection grid's spacing", () =>
				{
					_widthQueued = false;
					if (_itemWidth > 0)
					{
						return;
					}

					card.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
					_itemWidth = card.DesiredSize.Width;
					LayoutGrid();
				}, Microsoft.UI.Dispatching.DispatcherQueuePriority.High);
			}
		};
		PageContent.SizeChanged += (_, _) =>
		{
			ArtFrame.Width = ArtWidth(PageContent.ActualWidth);
			ArtFrame.Height = ArtHeight(PageContent.ActualWidth);
			LayoutGrid();
		};
		GridRepeater.ElementIndexChanged += (_, e) => SetPlace(e.Element, e.NewIndex);
		LetterRepeater.ElementPrepared += (_, e) =>
		{
			AutomationProperties.SetPositionInSet(e.Element, e.Index + 1);
			AutomationProperties.SetSizeOfSet(e.Element, ViewModel.Letters.Count);
		};
	}

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_focusedOnArrival = false;
		var set = e.Parameter is BaseItemDto { Id: { } given } ? given : e.Parameter as Guid?;
		_returnTo = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward && set is { } key && LeftFrom.TryGetValue(key, out var item) ? item : null;
		// Nixie 1.4.0 (bug hunt m8): the page follows its view model while it is shown, and lets go when it is left (it was held
		// from the constructor on: a page left behind would still focus cards)
		ViewModel.State.PropertyChanged -= OnStateChanged;
		ViewModel.PropertyChanged -= OnViewModelChanged;
		ViewModel.State.PropertyChanged += OnStateChanged;
		ViewModel.PropertyChanged += OnViewModelChanged;
		base.OnNavigatedTo(e);
		if (ViewModel.State.IsLoaded)
		{
			// a view model handed over already loaded (a theme switch) has no Status change to wait for
			OnStateChanged(ViewModel.State, new PropertyChangedEventArgs(nameof(LoadState.Status)));
		}
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		ViewModel.State.PropertyChanged -= OnStateChanged;
		ViewModel.PropertyChanged -= OnViewModelChanged;
		if (ViewModel.Collection?.Id is { } set && _lastEntry is { } item)
		{
			LeftFrom[set] = item;
		}

		base.OnNavigatedFrom(e);
	}

	private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(LoadState.Status) && ViewModel.State.IsLoaded && !_focusedOnArrival)
		{
			// arriving, the focus is in the page (the UX review's item 4), once the page is drawn: back from a film, on its card;
			// else on the one Primary; with nothing to play (an album of songs), on the first card
			_focusedOnArrival = true;
			UiDispatch.Enqueue(DispatcherQueue, "the Collection page's arrival focus", FocusOnArrival, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
		}
	}

	private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(CollectionViewModel.FocusRequest) && ViewModel.FocusRequest >= 0)
		{
			FocusCard(ViewModel.FocusRequest);
		}
		else if (e.PropertyName == nameof(CollectionViewModel.Entries))
		{
			UiDispatch.Enqueue(DispatcherQueue, "the Collection cards' places", UpdatePlaces);
		}
	}

	/// <summary>
	/// The arrival's focus, by the app's one rule (<see cref="ArrivalFocus"/>): never while the user has moved since the page
	/// came (Ctrl+F and a letter in the search box while a slow collection loaded: bug hunt M1), nor after R (its restore
	/// puts the keyboard back).
	/// </summary>
	private void FocusOnArrival()
	{
		if (!IsLoaded)
		{
			// loaded before it was drawn (a quick server): once it is
			void OnLoaded(object sender, RoutedEventArgs e)
			{
				Loaded -= OnLoaded;
				UiDispatch.Enqueue(DispatcherQueue, "the Collection page's arrival focus", FocusOnArrival, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
			}

			Loaded += OnLoaded;
			return;
		}

		var back = _returnTo is { } id ? IndexOf(id) : -1;
		if (back >= 0)
		{
			ArrivalFocus.TryTake(this, "the card Back returns to", () => FocusCard(back));
		}
		else if (PlayButton.IsLoaded && PlayButton.IsEnabled)
		{
			ArrivalFocus.TryTake(this, "Play", () => PlayButton.Focus(FocusState.Programmatic));
		}
		else if (ViewModel.Entries.Count > 0)
		{
			ArrivalFocus.TryTake(this, "the first card", () => FocusCard(0));
		}
	}

	private int IndexOf(Guid item)
	{
		for (var i = 0; i < ViewModel.Entries.Count; i++)
		{
			if (ViewModel.Entries[i].Item.Id == item)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>The entry a card (or anything in it) belongs to: the last one the keyboard or the pointer was on.</summary>
	private void Remember(DependencyObject? source)
	{
		for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is UIElement element && VisualTreeHelper.GetParent(node) == GridRepeater)
			{
				var index = GridRepeater.GetElementIndex(element);
				if (index >= 0 && index < ViewModel.Entries.Count)
				{
					_lastEntry = ViewModel.Entries[index].Item.Id;
				}

				return;
			}
		}
	}

	private double _itemWidth;
	private bool _widthQueued;

	/// <summary>
	/// The grid's columns fill its row: as many of the theme's posters as fit with at least 16 px between them, the space left
	/// shared between the gaps, so the last column ends at the toolbar's right edge and a short last row stays in its columns
	/// (UniformGridLayout's SpaceBetween spread a short row across the whole width).
	/// </summary>
	private void LayoutGrid()
	{
		var width = GridList.ActualWidth > 0 ? GridList.ActualWidth : PageContent.ActualWidth - PageContent.Padding.Left - PageContent.Padding.Right;
		GridLayout.MinColumnSpacing = Core.Theming.PageGridMetrics.ColumnSpacing(width, _itemWidth);
	}

	// ---- what the XAML shows ------------------------------------------------------------------------------------------

	/// <summary>The poster's width: 200 on a wide page, 152 on a narrow one (both on the 4 epx grid).</summary>
	public static double ArtWidth(double pageWidth) => pageWidth is > 0 and < 1100 ? 152 : 200;

	public static double ArtHeight(double pageWidth) => ArtWidth(pageWidth) * 3 / 2;

	public static ImageSource? Art(BaseItemDto? collection) => BaseItemDtoConverters.GetImage(collection, ImageType.Primary, 450);

	public static Visibility Shown(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>"Collection · 6 films · 1979–2016 · 1 of 6 watched".</summary>
	public static string KindAndSummary(string kind, string summary) => string.IsNullOrEmpty(summary) ? kind : $"{kind} · {summary}";

	public static string ProgressName(double percent) => $"{Math.Round(percent).ToString(CultureInfo.CurrentCulture)} percent watched";

	/// <summary>The actions and the toolbar: over a collection that has something to play or sort.</summary>
	public static Visibility ActionsVisibility(LoadStatus status) => status == LoadStatus.Loaded ? Visibility.Visible : Visibility.Collapsed;

	public static Visibility ToolbarVisibility(LoadStatus status) => ActionsVisibility(status);

	public static string ShuffleTip(string kind) => kind == "Collection" ? "Play a film of this collection not watched yet, at random" : $"Play an item of this {kind.ToLowerInvariant()} not watched yet, at random";

	public static string WatchedName(string kind) => kind == "Collection" ? "Watched: every film" : "Watched: every item";

	public static bool Is(CollectionOrder current, CollectionOrder item) => current == item;

	/// <summary>The set's own order in the Order menu: "Collection order", "Playlist order", "Album order" (always offered: the way back).</summary>
	public static string OwnOrderText(string kind) => $"{kind} order";

	public static Visibility LettersVisibility(IReadOnlyList<LetterJump> letters) => letters.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>The grid's name for Narrator: "Copper and Glass, collection".</summary>
	public static string ListName(string title, string kind) => string.IsNullOrEmpty(title) ? kind : $"{title}, {kind.ToLowerInvariant()}";

	// ---- the page's own handlers ------------------------------------------------------------------------------------

	private void OverviewText_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
	{
		if (OverviewText.MaxLines != 0)
		{
			MoreButton.Visibility = OverviewText.IsTextTrimmed ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private void MoreButton_Click(object sender, RoutedEventArgs e)
	{
		var expand = OverviewText.MaxLines != 0;
		OverviewText.MaxLines = expand ? 0 : 3;
		MoreButton.Content = expand ? "Less" : "More";
	}

	private async void WatchedToggle_Click(object sender, RoutedEventArgs e) => await ViewModel.SetWatched(WatchedToggle.IsChecked == true);

	private async void FavoriteToggle_Click(object sender, RoutedEventArgs e) => await ViewModel.SetFavorite(FavoriteToggle.IsChecked == true);

	private void Order_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<CollectionOrder>(tag, out var order))
		{
			ViewModel.SetOrder(order);
		}
	}

	private void Letter_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: LetterJump jump })
		{
			ViewModel.JumpTo(jump);
		}
	}

	/// <summary>
	/// The card at <paramref name="index"/> gets the keyboard, brought into view; false when it cannot (the page not drawn, the
	/// grid not holding that many cards). Nixie 1.4.0 (bug hunt M3): never asks the repeater for a card it does not have, and a
	/// failure is logged, not thrown (thrown from a queued callback, it ended the app).
	/// </summary>
	private bool FocusCard(int index)
	{
		if (!CanFocusCard(index, ViewModel.Entries.Count, GridRepeater.ItemsSourceView?.Count ?? -1, IsLoaded && GridRepeater.IsLoaded))
		{
			return false;
		}

		ViewModel.FocusRequest = -1;
		try
		{
			// Nixie 1.4.0 (bug hunt M3, reproduced: Back to a collection, its card past the first, the page just loaded): the
			// grid lays its cards out first and the card is taken from those. Asked for before the grid had laid any out
			// (GetOrCreateElement making it an anchor outside what the repeater had realized), the next layout pass never
			// settled ("Layout cycle detected", 150 ms after the card took the keyboard) and the app ended (0xc000027b in
			// CoreMessagingXP). GetOrCreateElement only for a card the grid has not made (far down a long playlist), as CardGrid does.
			UpdateLayout();
			var element = GridRepeater.TryGetElement(index) ?? GridRepeater.GetOrCreateElement(index);
			if (element is not null)
			{
				GridRepeater.UpdateLayout();
				element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
				return CardOf(element)?.Focus(FocusState.Keyboard) == true;
			}
		}
		catch (Exception ex)
		{
			UiDispatch.Failed($"the Collection page's card {index + 1} of {ViewModel.Entries.Count} taking the keyboard", ex);
		}

		return false;
	}

	/// <summary>Whether a card can be asked of the grid: a place among the entries, the grid holding them, both drawn.</summary>
	internal static bool CanFocusCard(int index, int entries, int inGrid, bool drawn) =>
		drawn && index >= 0 && index < entries && index < inGrid;

	private void SetPlace(UIElement element, int index)
	{
		if (CardOf(element) is { } card)
		{
			AutomationProperties.SetPositionInSet(card, index + 1);
			AutomationProperties.SetSizeOfSet(card, ViewModel.Entries.Count);
		}
	}

	private void UpdatePlaces()
	{
		var n = VisualTreeHelper.GetChildrenCount(GridRepeater);
		for (var i = 0; i < n; i++)
		{
			if (VisualTreeHelper.GetChild(GridRepeater, i) is UIElement element && GridRepeater.GetElementIndex(element) is >= 0 and var index)
			{
				SetPlace(element, index);
			}
		}
	}

	/// <summary>The theme's poster in an entry (the first control under it, which takes the focus and is what Narrator reads).</summary>
	private static Control? CardOf(DependencyObject root)
	{
		var queue = new Queue<DependencyObject>([root]);
		while (queue.Count > 0)
		{
			var node = queue.Dequeue();
			if (node is Control control && !ReferenceEquals(node, root))
			{
				return control;
			}

			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
			{
				queue.Enqueue(VisualTreeHelper.GetChild(node, i));
			}
		}

		return null;
	}
}
