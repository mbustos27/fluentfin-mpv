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
	private readonly LibraryGridHost _host;
	private bool? _narrow;
	private bool _band;

	public CinematicLibraryPage()
	{
		CinematicResources.Ensure();
		InitializeComponent();

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		_grid.ForwardWheel(LibraryRoot);
		_host = new LibraryGridHost(this, ViewModel, GridScroller, GridRepeater, _grid, Letters);
		ViewModel.PropertyChanged += (_, e) =>
		{
			switch (e.PropertyName)
			{
				case nameof(LibraryViewModel.TotalCount) or nameof(LibraryViewModel.FocusRequest) or nameof(LibraryViewModel.IsLoading):
					QueueStage();
					break;
				case nameof(LibraryViewModel.SortBy) or nameof(LibraryViewModel.Order) or nameof(LibraryViewModel.AppliedFilters):
					ShowTabs();
					QueueStage();
					break;
				case nameof(LibraryViewModel.Density) or nameof(LibraryViewModel.Layout) or nameof(LibraryViewModel.Art):
					_narrow = null;
					Layout();
					break;
			}
		};

		// Nixie 1.3.1 (the UX review's C-B1): the stage shows the still the pointer rests on, else the one the keyboard is on in
		// the grid, else the first one in view, and it follows every sort, filter, section, load and scroll; with nothing
		// left to show it goes, Play with it (StageTarget).
		((System.Collections.Specialized.INotifyCollectionChanged)ViewModel.Items).CollectionChanged += (_, _) => QueueStage();
		GridRepeater.GotFocus += (_, _) => QueueStage();
		GridRepeater.LostFocus += (_, _) => QueueStage();
		GridRepeater.AddHandler(PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
		{
			_hovered = ModelOf(e.OriginalSource as DependencyObject)?.Dto?.Id;
			QueueStage();
		}), true);
		GridRepeater.PointerExited += (_, _) =>
		{
			_hovered = null;
			QueueStage();
		};
		GridScroller.ViewChanged += (_, _) =>
		{
			QueueStage();
			// Nixie 1.4.0 (the UX review's C-M1, item 12): the stage folds to a band once the grid scrolls, and opens at its top
			var band = GridScroller.VerticalOffset > (_band ? 8 : 40);
			if (band != _band)
			{
				_band = band;
				Layout();
			}
		};
		// Nixie 1.4.0 (item 4): Ctrl+Up from a still: the stage's Play (the stage previews the title the keyboard is on)
		GridList.AddHandler(KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
		{
			if (e.Key == Windows.System.VirtualKey.Up && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
				&& !_band && Stage.FocusPrimary(FocusState.Keyboard))
			{
				e.Handled = true;
			}
		}), true);
		// a jump past the first line folds the stage first, so the letter's row lands at the top of the grid as it will be
		Letters.Jumped += (_, e) =>
		{
			if (e.Jump.Index >= _grid.ColumnCount && !_band)
			{
				_band = true;
				Layout();
				UpdateLayout();
			}
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

	/// <summary>
	/// Nixie 1.4.0 (the UX review's C-M1, item 12): the stage takes a third of the window at most (it took 61 %: 12 stills on
	/// screen), its words under the library's name, the header line on the night ground under them; three rows of stills at
	/// 1080. Scrolled, it is a 96 px band (the name only). The grid shows stills, posters or the list (View).
	/// </summary>
	private void Layout()
	{
		var height = ActualHeight;
		var width = ActualWidth;
		if (height <= 0 || width <= 0)
		{
			return;
		}

		var narrow = width < CinematicItemPageLayout.NarrowBelow;
		var side = narrow ? 48 : 96;
		var top = narrow ? 104 : 112;
		Stage.SetLayout(narrow, top);
		Stage.SetBand(_band);
		// the stage's own rows: under the name, its words; the header line right under them; the art fades past it
		var stageRow = _band ? 96 : Math.Min(Math.Round(height * 0.34), top + Stage.WordsHeight + 12);
		StageRow.Height = new GridLength(Math.Max(96, stageRow));
		Stage.ArtHeight = _band ? 150 : Math.Max(96, stageRow) + 90;
		TitleText.FontSize = _band ? 26 : narrow ? 34 : 40;
		TopLine.Margin = new Thickness(side, _band ? 54 : 60, side, 0);
		if (_narrow != narrow)
		{
			_narrow = narrow;
			HeaderLine.Margin = new Thickness(side, 0, side, 10);
			AppliedFilters.Margin = new Thickness(side, 0, side, 10);
			LetterRow.Margin = new Thickness(side, 0, side, 6);
			ShowGrid(narrow, side);
		}
	}

	/// <summary>Stills (large or small by the window), posters at their 2:3, or the list; the placeholders follow the grid's layout.</summary>
	private void ShowGrid(bool narrow, double side)
	{
		GridRepeater.Margin = new Thickness(side - 12, 0, side - 12, 32);
		Placeholders.Margin = new Thickness(side - 12, 0, side - 12, 0);
		DataTemplate template;
		if (ViewModel.IsList)
		{
			template = (DataTemplate)Resources["ListRow"];
			GridRepeater.Layout = new StackLayout { Spacing = 4 };
		}
		else if (ViewModel.Art == LibraryArt.Posters)
		{
			template = (DataTemplate)Resources["PosterCard"];
			GridLayout.ItemsStretch = UniformGridLayoutItemsStretch.Uniform;
			GridLayout.MinColumnSpacing = GridLayout.MinRowSpacing = 8;
			LibraryDensities.Apply(GridLayout, ViewModel.Density, narrow ? 170 : 190, narrow ? 315 : 345);
			GridRepeater.Layout = GridLayout;
		}
		else
		{
			template = (DataTemplate)Resources[narrow ? "GridSmall" : "GridLarge"];
			GridLayout.ItemsStretch = UniformGridLayoutItemsStretch.None;
			GridLayout.MinColumnSpacing = GridLayout.MinRowSpacing = 0;
			GridLayout.MinItemWidth = narrow ? 256 : 296;
			GridLayout.MinItemHeight = narrow ? 196 : 224;
			GridRepeater.Layout = GridLayout;
		}

		GridRepeater.ItemTemplate = template;
		LibrarySkeleton.Mirror(GridLayout, SkeletonLayout);
	}

	private Guid? _hovered;
	private bool _stageQueued;

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
		var first = LibraryGridHost.FirstVisibleIndex(GridRepeater, GridScroller);
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

	}

	/// <summary>The title of the still (or poster, or row) the keyboard is on, when it is in the grid.</summary>
	private Guid? FocusedInGrid() =>
		XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject focused
			&& IsWithin(focused, GridRepeater) && ModelOf(focused) is { Dto.Id: { } id } ? id : null;

	/// <summary>The title of the still, poster or row <paramref name="element"/> is in.</summary>
	private static BaseItemViewModel? ModelOf(DependencyObject? element)
	{
		for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			switch (node)
			{
				case CinematicStill still:
					return still.Model;
				case BaseItemDtoCard card:
					return card.Model;
				case LibraryListRow row:
					return row.Model;
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
	// ---- the sections: tabs over the shared filter and sort --------------------------------------------------------

	private enum Section
	{
		All,
		Unwatched,
		Favorites,
		Recent,
	}


	/// <summary>
	/// Nixie 1.4.0 (the UX review's C-M3, item 2): a section is lit only when it is exactly what is shown: All, no filter (in any
	/// sort but Recently added's); Unwatched or Favorites, that switch alone; Recently added, no filter, newest added first.
	/// A genre ticked, or two switches, light none (they lit All).
	/// </summary>
	private Section? Current
	{
		get
		{
			var f = ViewModel.Filter;
			var recent = ViewModel.SortBy == ItemSortBy.DateCreated && ViewModel.Order == SortOrder.Descending && !ViewModel.InPlaylistOrder;
			return ViewModel.ActiveFilters switch
			{
				0 => recent ? Section.Recent : Section.All,
				1 when f.Unwatched => Section.Unwatched,
				1 when f.Favorites => Section.Favorites,
				_ => null,
			};
		}
	}

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

	// the sort in use before Recently added put its own: All puts it back (a preset never resets the user's sort to Name)
	private (ItemSortBy SortBy, SortOrder Order)? _sortBeforeRecent;

	private void SectionTabs_TabInvoked(object? sender, CinematicTab tab)
	{
		if (tab.Payload is not Section section || section == Current)
		{
			return;
		}

		var wasRecent = Current == Section.Recent;
		ViewModel.ClearFilters();
		switch (section)
		{
			case Section.Unwatched:
				ViewModel.Filter.Unwatched = true;
				break;
			case Section.Favorites:
				ViewModel.Filter.Favorites = true;
				break;
			case Section.Recent:
				_sortBeforeRecent ??= (ViewModel.SortBy, ViewModel.Order);
				ViewModel.SetSort(ItemSortBy.DateCreated, SortOrder.Descending);
				break;
			case Section.All when wasRecent:
				var (sortBy, order) = _sortBeforeRecent ?? (ItemSortBy.Name, SortOrder.Ascending);
				ViewModel.SetSort(sortBy, order == SortOrder.Descending && sortBy == ItemSortBy.DateCreated ? SortOrder.Ascending : order);
				break;
		}

		if (section != Section.Recent)
		{
			_sortBeforeRecent = null;
		}

		ShowTabs();
	}

	// ---- the shared library's place, jump and focus (LibraryGridHost) ------------------------------------------------

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_host.OnNavigatedTo(e);
		base.OnNavigatedTo(e);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		_host.OnNavigatedFrom();
		base.OnNavigatedFrom(e);
	}
}
