using FluentFin.Controls;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using FluentFin.Themes.Cinematic.Controls;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Cinematic.Views;

/// <summary>Cinematic's library (see CinematicLibraryPage.xaml), over the shared <see cref="LibraryViewModel"/>.</summary>
public sealed partial class CinematicLibraryPage : Page
{
	public LibraryViewModel ViewModel { get; } = App.GetService<LibraryViewModel>();

	private readonly CardGrid _grid;
	private FocusState _jumpFocus = FocusState.Keyboard;
	private bool _focusOnLoad;
	private int _returnScroll = -1;
	private (int Index, FocusState State) _returnFocus = (-1, FocusState.Keyboard);
	private bool? _narrow;

	public CinematicLibraryPage()
	{
		CinematicResources.Ensure();
		InitializeComponent();

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		GridScroller.ScrollCompleted += GridScroller_ScrollCompleted;
		void FocusOnLoad(object sender, RoutedEventArgs e)
		{
			if (_focusOnLoad)
			{
				_focusOnLoad = false;
				FocusRequested();
			}
		}

		Loaded += FocusOnLoad;
		GridScroller.Loaded += FocusOnLoad;
		_grid.ForwardWheel(LibraryRoot);
		_grid.SetPlace(0, ViewModel.TotalCount);
		ViewModel.PropertyChanged += (_, e) =>
		{
			switch (e.PropertyName)
			{
				case nameof(LibraryViewModel.TotalCount):
					_grid.SetPlace(0, ViewModel.TotalCount);
					QueueStage();
					break;
				case nameof(LibraryViewModel.FocusRequest) or nameof(LibraryViewModel.IsLoading):
					FocusRequested();
					QueueStage();
					break;
				case nameof(LibraryViewModel.SortBy) or nameof(LibraryViewModel.Order) or nameof(LibraryViewModel.CountText) or nameof(LibraryViewModel.Title):
					ShowTabs();
					ShowPlace();
					break;
			}
		};
		ViewModel.Filter.PropertyChanged += (_, _) => ShowTabs();

		// Nixie 1.3.1 (the UX review's C-B1): the stage shows the still the pointer rests on, else the one the keyboard is on in
		// the grid, else the first one in view, and it follows every sort, filter, section, load and scroll; with nothing
		// left to show it goes, Play with it (StageTarget). The place line and the letter mark follow the first title in view.
		((System.Collections.Specialized.INotifyCollectionChanged)ViewModel.Items).CollectionChanged += (_, _) => QueueStage();
		GridRepeater.GotFocus += (_, _) => QueueStage();
		GridRepeater.LostFocus += (_, _) => QueueStage();
		GridRepeater.AddHandler(PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
		{
			_hovered = StillOf(e.OriginalSource as DependencyObject)?.Model?.Dto?.Id;
			QueueStage();
		}), true);
		GridRepeater.PointerExited += (_, _) =>
		{
			_hovered = null;
			QueueStage();
		};
		GridScroller.ViewChanged += (_, _) => QueueStage();
		LetterRepeater.ElementPrepared += (_, e) =>
		{
			AutomationProperties.SetPositionInSet(e.Element, e.Index + 1);
			AutomationProperties.SetSizeOfSet(e.Element, ViewModel.Letters.Count);
			MarkLetter(_markedLetter); // a letter made after the mark was set gets it too
		};
		SizeChanged += (_, _) => Layout();
		Stage.WordsSizeChanged += (_, _) => Layout();
		ShowTabs();
	}

	public static string LineFor(BaseItemDto? dto)
	{
		if (dto is null)
		{
			return "";
		}

		var parts = new List<string>();
		if (dto.Type == BaseItemDto_Type.Series && Converters.BaseItemDtoConverters.SeriesYears(dto) is { Length: > 0 } years)
		{
			parts.Add(years);
		}
		else if (dto.ProductionYear is > 0 and var year)
		{
			parts.Add(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}

		if (dto.Type != BaseItemDto_Type.Series && dto.RunTimeTicks is > 0 and var ticks && HeroSlides.Runtime(TimeSpan.FromTicks(ticks)) is { } runtime)
		{
			parts.Add(runtime);
		}

		return string.Join(" · ", parts);
	}

	public static IReadOnlyList<int> Slots(int count) => [.. Enumerable.Range(0, count)];

	/// <summary>The stage takes the top of the window (660 of 1080, never under 420), the grid the rest.</summary>
	private void Layout()
	{
		var height = ActualHeight;
		var width = ActualWidth;
		if (height <= 0 || width <= 0)
		{
			return;
		}

		var narrow = width < CinematicItemPageLayout.NarrowBelow;
		var art = Math.Clamp(Math.Round(height * 0.61), 420, 720);
		Stage.ArtHeight = art;
		var top = narrow ? 118 : Math.Max(150, art * 0.28);
		Stage.SetLayout(narrow, top);
		// the grid starts under the preview's words (their height, with room for the header line), never above the art's foot
		StageRow.Height = new GridLength(Math.Max(art - (narrow ? 96 : 112), top + Stage.WordsHeight + 14));
		if (_narrow != narrow)
		{
			_narrow = narrow;
			var side = narrow ? 48 : 96;
			TopLine.Margin = new Thickness(side, 70, side, 0);
			HeaderLine.Margin = new Thickness(side, 0, side - 12, 6);
			GridRepeater.Margin = new Thickness(side - 12, 0, narrow ? 24 : 36, 32);
			GridRepeater.ItemTemplate = (DataTemplate)Resources[narrow ? "GridSmall" : "GridLarge"];
			GridLayout.MinItemWidth = narrow ? 256 : 296;
			GridLayout.MinItemHeight = narrow ? 210 : 240;
			Placeholders.Margin = new Thickness(side, 14, side - 26, 0);
		}
	}

	private Guid? _hovered;
	private bool _stageQueued;
	private string? _markedLetter;

	/// <summary>
	/// Nixie 1.3.1: what the stage shows: the still the pointer rests on, else the one the keyboard is on in the grid, else
	/// (the keyboard on the stage's own buttons) the title it shows, else the first in view, else the first of the list; only
	/// a title the grid shows (filtered out: never), null when it shows none.
	/// </summary>
	public static BaseItemDto? StageTarget(IReadOnlyList<BaseItemViewModel> items, Guid? hovered, Guid? focused, int firstVisible, Guid? kept = null)
	{
		if (items.Count == 0)
		{
			return null;
		}

		foreach (var wanted in new[] { hovered, focused, kept })
		{
			if (wanted is { } id && items.FirstOrDefault(x => x.Id == id) is { } item)
			{
				return item.Dto;
			}
		}

		return items[firstVisible >= 0 && firstVisible < items.Count ? firstVisible : 0].Dto;
	}

	/// <summary>The stage and the place line again, once this turn's changes (a sort, a filter, a scroll) are laid out.</summary>
	private void QueueStage()
	{
		if (_stageQueued)
		{
			return;
		}

		_stageQueued = true;
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			_stageQueued = false;
			RefreshStage();
		});
	}

	private void RefreshStage()
	{
		if (ViewModel.IsLoading)
		{
			return;
		}

		var items = ViewModel.Items;
		var first = FirstVisibleIndex();
		var kept = XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject on && IsWithin(on, Stage) ? Stage.Current?.Id : null;
		var target = StageTarget(items, _hovered, FocusedInGrid(), first, kept);
		if (target is null)
		{
			Stage.ShowNothing();
		}
		else
		{
			Stage.Show(target);
		}

		ShowPlace(first >= 0 && first < items.Count ? items[first].Dto : target);
	}

	/// <summary>The title of the still the keyboard is on, when it is in the grid.</summary>
	private Guid? FocusedInGrid() =>
		XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject focused
			&& StillOf(focused) is { Model.Dto.Id: { } id } still && IsWithin(still, GridRepeater) ? id : null;

	private static CinematicStill? StillOf(DependencyObject? element)
	{
		for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			if (node is CinematicStill still)
			{
				return still;
			}
		}

		return null;
	}

	private static bool IsWithin(DependencyObject element, DependencyObject container)
	{
		for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, container))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The index of the first still in view (its foot below the view's top), -1 when none is drawn.</summary>
	private int FirstVisibleIndex()
	{
		var best = -1;
		var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(GridRepeater);
		for (var i = 0; i < count; i++)
		{
			if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(GridRepeater, i) is not FrameworkElement { ActualHeight: > 0 } element
				|| GridRepeater.GetElementIndex(element) is not (>= 0 and var index))
			{
				continue;
			}

			try
			{
				var top = element.TransformToVisual(GridScroller).TransformPoint(default).Y;
				if (top + element.ActualHeight * 0.5 > 0 && top < GridScroller.ViewportHeight && (best < 0 || index < best))
				{
					best = index;
				}
			}
			catch (ArgumentException)
			{
			}
		}

		return best;
	}

	// ---- the sections: tabs over the shared filter and sort --------------------------------------------------------

	private enum Section
	{
		All,
		Unwatched,
		Favorites,
		Recent,
	}

	private Section Current =>
		ViewModel.Filter.Favorites && !ViewModel.Filter.Unwatched ? Section.Favorites
		: ViewModel.Filter.Unwatched && !ViewModel.Filter.Favorites ? Section.Unwatched
		: ViewModel.SortBy == ItemSortBy.DateCreated && ViewModel.Order == SortOrder.Descending && !ViewModel.Filter.Unwatched && !ViewModel.Filter.Favorites ? Section.Recent
		: Section.All;

	private void ShowTabs()
	{
		var current = Current;
		SectionTabs.SetTabs(
		[
			new CinematicTab("All", "All titles", current == Section.All, Section.All),
			new CinematicTab("Unwatched", "Unwatched", current == Section.Unwatched, Section.Unwatched),
			new CinematicTab("Favorites", "Favorites", current == Section.Favorites, Section.Favorites),
			new CinematicTab("Recently added", "Recently added", current == Section.Recent, Section.Recent),
		]);
	}

	private void SectionTabs_TabInvoked(object? sender, CinematicTab tab)
	{
		if (tab.Payload is not Section section || section == Current)
		{
			return;
		}

		ViewModel.Filter.Unwatched = section == Section.Unwatched;
		ViewModel.Filter.Favorites = section == Section.Favorites;
		if (section == Section.Recent)
		{
			ViewModel.UpdateSortBy(ItemSortBy.DateCreated);
			ViewModel.UpdateSortOrder(SortOrder.Descending);
		}
		else if (ViewModel.SortBy == ItemSortBy.DateCreated && ViewModel.Order == SortOrder.Descending)
		{
			ViewModel.UpdateSortBy(ItemSortBy.Name);
			ViewModel.UpdateSortOrder(SortOrder.Ascending);
		}

		ShowTabs();
	}

	/// <summary>"B · 23 movies · Name, A to Z": the first title in view's letter (sorted by name), the count, the sort.</summary>
	private void ShowPlace(BaseItemDto? focused = null)
	{
		focused ??= Stage.Current;
		var parts = new List<string>();
		// Nixie 1.3.1: the letter the title files under (its sort name), as the A-Z jump's
		var letter = ViewModel.SortBy == ItemSortBy.Name && !ViewModel.InPlaylistOrder && focused is not null
			? LibraryBrowse.LetterOf(string.IsNullOrWhiteSpace(focused.SortName) ? focused.Name : focused.SortName) : null;
		if (letter is { } l)
		{
			parts.Add(l);
		}

		if (!string.IsNullOrEmpty(ViewModel.CountText))
		{
			parts.Add(ViewModel.CountText);
		}

		parts.Add(ViewModel.InPlaylistOrder ? ViewModel.SortName
			: $"{ViewModel.SortName}, {(ViewModel.Order == SortOrder.Ascending ? ViewModel.SortBy == ItemSortBy.Name ? "A to Z" : "low to high" : ViewModel.SortBy == ItemSortBy.Name ? "Z to A" : "high to low")}");
		PlaceText.Text = string.Join(" · ", parts);
		MarkLetter(letter);
	}

	/// <summary>The letter of the title in focus is underlined in the A-Z jump.</summary>
	private void MarkLetter(string? letter)
	{
		_markedLetter = letter;
		for (var i = 0; i < ViewModel.Letters.Count; i++)
		{
			if (LetterRepeater.TryGetElement(i) is Button { Content: Grid { Children: [_, Border mark] } } button && button.Tag is LetterJump jump)
			{
				var current = string.Equals(jump.Letter, letter, StringComparison.OrdinalIgnoreCase);
				mark.Opacity = current ? 1 : 0;
				AutomationProperties.SetItemStatus(button, current ? "Shown" : "");
			}
		}
	}

	// ---- the shared library's place, jump and focus (LibraryPage) -----------------------------------------------------

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		ViewModel.ComingBack = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward;
		_jumpFocus = FocusState.Keyboard;
		base.OnNavigatedTo(e);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		ViewModel.LeftAt(_grid.LastItem as BaseItemViewModel, GridScroller.VerticalOffset);
		base.OnNavigatedFrom(e);
	}

	private void Letter_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: LetterJump jump } button)
		{
			_jumpFocus = button.FocusState is FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
			ViewModel.JumpToCommand.Execute(jump);
		}
	}

	private void FocusRequested()
	{
		if (ViewModel.IsLoading || (ViewModel.FocusRequest < 0 && ViewModel.ReturnOffset is null))
		{
			return;
		}

		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (ViewModel.IsLoading)
			{
				return;
			}

			if (XamlRoot is null || !IsLoaded || GridScroller.XamlRoot is null || !GridScroller.IsLoaded)
			{
				_focusOnLoad = true;
				return;
			}

			UpdateLayout();
			var index = ViewModel.FocusRequest;
			ViewModel.FocusRequest = -1;
			var state = _jumpFocus;
			_jumpFocus = FocusState.Keyboard;
			if (ViewModel.ReturnOffset is { } offset && Math.Abs(Math.Min(offset, GridScroller.ScrollableHeight) - GridScroller.VerticalOffset) >= 1)
			{
				ViewModel.ReturnOffset = null;
				GridScroller.UpdateLayout();
				_returnScroll = GridScroller.ScrollTo(0, Math.Min(offset, GridScroller.ScrollableHeight), new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
				_returnFocus = (index, state);
				return;
			}

			ViewModel.ReturnOffset = null;
			if (index >= 0)
			{
				_grid.FocusAndHold(index, state);
			}
		});
	}

	private void GridScroller_ScrollCompleted(ScrollView sender, ScrollingScrollCompletedEventArgs args)
	{
		if (args.CorrelationId != _returnScroll)
		{
			return;
		}

		_returnScroll = -1;
		if (_returnFocus.Index >= 0)
		{
			GridRepeater.UpdateLayout();
			_grid.FocusAndHold(_returnFocus.Index, _returnFocus.State);
		}

		_returnFocus = (-1, FocusState.Keyboard);
	}
}
