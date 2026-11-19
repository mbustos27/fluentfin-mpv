using System.Globalization;
using FluentFin.Converters;
using FluentFin.Core.ViewModels;
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

	public CollectionPage()
	{
		InitializeComponent();

		// each card's place for Narrator ("3 of 6"): the card (the theme's poster) is the element it reads
		GridRepeater.ElementPrepared += (_, e) =>
		{
			SetPlace(e.Element, e.Index);
			if (_itemWidth <= 0 && e.Element is FrameworkElement card)
			{
				card.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
				_itemWidth = card.DesiredSize.Width;
				LayoutGrid();
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

		ViewModel.State.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(LoadState.Status) && ViewModel.State.IsLoaded && !_focusedOnArrival)
			{
				// arriving, the focus is in the page, on its one Primary (the UX review's item 4), once the page is drawn
				_focusedOnArrival = true;
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
				{
					if (PlayButton.IsLoaded && PlayButton.IsEnabled)
					{
						PlayButton.Focus(FocusState.Programmatic);
					}
				});
			}
		};

		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(CollectionViewModel.FocusRequest) && ViewModel.FocusRequest >= 0)
			{
				FocusCard(ViewModel.FocusRequest);
			}
			else if (e.PropertyName == nameof(CollectionViewModel.Entries))
			{
				DispatcherQueue.TryEnqueue(UpdatePlaces);
			}
		};
	}

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_focusedOnArrival = false;
		base.OnNavigatedTo(e);
	}

	private double _itemWidth;

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

	private void FocusCard(int index)
	{
		if (index < 0 || index >= ViewModel.Entries.Count)
		{
			return;
		}

		ViewModel.FocusRequest = -1;
		if (GridRepeater.GetOrCreateElement(index) is UIElement element)
		{
			GridRepeater.UpdateLayout();
			element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
			CardOf(element)?.Focus(FocusState.Keyboard);
		}
	}

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
