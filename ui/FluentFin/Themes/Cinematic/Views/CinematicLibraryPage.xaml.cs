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
	private double _side;
	private bool _band;

	public CinematicLibraryPage()
	{
		CinematicResources.Ensure();
		InitializeComponent();
		// Nixie 1.7.5: kept whole by the frame for Back to this very page (its grid as it was, at once); a new visit makes it anew
		// (NavigationService.KeptWhole)
		NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		_grid.ForwardWheel(LibraryRoot);
		_host = new LibraryGridHost(this, ViewModel, GridScroller, GridRepeater, _grid, Letters);
		// Nixie 1.4.0 (item 15): the first still's time ([perf]) and the placeholders' cells against the grid's, in the log
		LibraryProbe.Attach(this, Placeholders, GridRepeater, i => LibraryProbe.SlotOf(Placeholders, i, this), i => LibraryProbe.SlotOf(GridRepeater, i, this), logFirstCard: true);
		// Nixie 1.7.4: only while the page is shown (Back hands a kept view model to a new page: WhileLoaded)
		FluentFin.Services.WhileLoaded.Listen(this, ViewModel, (_, e) =>
		{
			switch (e.PropertyName)
			{
				case nameof(LibraryViewModel.TotalCount) or nameof(LibraryViewModel.FocusRequest) or nameof(LibraryViewModel.IsLoading):
					if (e.PropertyName == nameof(LibraryViewModel.IsLoading))
					{
						Layout(); // the stage's room while loading (Layout), and back to its words after
					}

					QueueStage();
					break;
				case nameof(LibraryViewModel.SortBy) or nameof(LibraryViewModel.Order) or nameof(LibraryViewModel.AppliedFilters):
					ShowTabs();
					QueueStage();
					break;
				case nameof(LibraryViewModel.Presets): // 1.6 colpage
					ShowTabs();
					break;
				case nameof(LibraryViewModel.Density) or nameof(LibraryViewModel.Layout) or nameof(LibraryViewModel.Art):
					_narrow = null;
					Layout();
					break;
			}
		});

		// Nixie 1.3.1 (the UX review's C-B1): the stage shows the still the pointer rests on, else the one the keyboard is on in
		// the grid, else the first one in view, and it follows every sort, filter, section, load and scroll; with nothing
		// left to show it goes, Play with it (StageTarget).
		// Nixie 1.7.4: only while the page is shown (WhileLoaded)
		var items = (System.Collections.Specialized.INotifyCollectionChanged)ViewModel.Items;
		System.Collections.Specialized.NotifyCollectionChangedEventHandler itemsChanged = (_, _) => QueueStage();
		FluentFin.Services.WhileLoaded.Hold(this, () => items.CollectionChanged += itemsChanged, () => items.CollectionChanged -= itemsChanged);
		GridRepeater.GotFocus += (_, _) => QueueStage();
		GridRepeater.LostFocus += (_, _) => QueueStage();
		GridRepeater.AddHandler(PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
		{
			_hovered = ModelOf(e.OriginalSource as DependencyObject)?.Dto?.Id;
			_hoveredAt = GridScroller.VerticalOffset;
			QueueStage();
		}), true);
		GridRepeater.PointerExited += (_, _) =>
		{
			_hovered = null;
			QueueStage();
		};
		GridScroller.ViewChanged += (_, _) =>
		{
			// the bug hunt's M5: the wheel moves the stills under a resting pointer, and no PointerEntered comes: what it rested
			// on is not what it rests on now
			if (Math.Abs(GridScroller.VerticalOffset - _hoveredAt) >= 1)
			{
				_hovered = null;
			}

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
	/// <summary>The stage's words' height when last shown, wide and narrow (a typical title's until then).</summary>
	private static readonly double[] s_words = [180, 168];

	private void Layout()
	{
		var height = ActualHeight;
		var width = ActualWidth;
		if (height <= 0 || width <= 0)
		{
			return;
		}

		var narrow = width < CinematicItemPageLayout.NarrowBelow;
		// Nixie 1.4.0 (plan item 13): the theme's page grid: its gutter (48 at 1100 to 96 at 1920) and the title at its y (72)
		var side = PageGrid.GutterAt(width);
		var titleY = PageGrid.Tokens().TitleY;
		var top = titleY + (narrow ? 44 : 52);
		Stage.SetLayout(narrow, top);
		Stage.SetBand(_band);
		// the stage's own rows: under the name, its words; the header line right under them; the art fades past it
		// Nixie 1.4.0 (item 15: the placeholders where the stills land): while the library loads the stage has no words yet, and
		// the grid dropped 170-180 px when they came; their room is kept meanwhile (the last height they had, else a typical one)
		var words = Stage.WordsHeight;
		if (words > 0)
		{
			s_words[narrow ? 1 : 0] = words;
		}
		else if (ViewModel.IsLoading || ViewModel.TotalCount > 0)
		{
			// loading, or loaded with titles whose words are still being drawn (no shrink and grow on the way)
			words = s_words[narrow ? 1 : 0];
		}

		var stageRow = _band ? 96 : Math.Min(Math.Round(height * 0.34), top + words + 12);
		StageRow.Height = new GridLength(Math.Max(96, stageRow));
		// the art ends where the header line starts: the sections and the toolbar sit on the night ground, never on art (C-M2)
		Stage.ArtHeight = Math.Max(96, stageRow) + 8;
		TitleText.FontSize = _band ? 26 : narrow ? 34 : 40;
		TopLine.Margin = new Thickness(side, _band ? 54 : titleY, side, 0);
		if (_narrow != narrow || Math.Abs(_side - side) >= 1)
		{
			_narrow = narrow;
			_side = side;
			HeaderLine.Margin = new Thickness(side, 0, side, 10);
			AppliedFilters.Margin = new Thickness(side, 0, side, 10);
			LetterRow.Margin = new Thickness(side, 0, side, 6);
			ShowGrid(narrow, side);
		}
	}

	/// <summary>Stills (large or small by the window), posters at their 2:3, or the list; the placeholders follow the grid's layout.</summary>
	private void ShowGrid(bool narrow, double side)
	{
		// six stills a row at 1920 (the slots carry their own 12 px sides)
		GridRepeater.Margin = new Thickness(side - 12, 0, narrow ? 24 : 36, 32);
		Placeholders.Margin = new Thickness(side - 12, 0, narrow ? 24 : 36, 0);
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
			GridLayout.MinItemHeight = narrow ? 196 : 218;
			GridRepeater.Layout = GridLayout;
		}

		GridRepeater.ItemTemplate = template;
		LibrarySkeleton.Mirror(GridLayout, SkeletonLayout);
	}

	private Guid? _hovered;
	private double _hoveredAt;
	private bool _stageQueued;

	/// <summary>
	/// Nixie 1.3.1: what the stage shows: the still the pointer rests on, else the one the keyboard is on in the grid, else
	/// (the keyboard on the stage's own buttons) the title it shows, else the first in view, else the first of the list; only
	/// a title the grid shows (filtered out: never), null when it shows none. Nixie 1.4.0 (the bug hunt's M5): the pointer's
	/// and the keyboard's titles count only while they are in view (<paramref name="firstVisible"/> to
	/// <paramref name="lastVisible"/>): a wheel scroll under a resting pointer, or past the focused still, moves the stage on.
	/// </summary>
	public static BaseItemDto? StageTarget(IReadOnlyList<BaseItemViewModel> items, Guid? hovered, Guid? focused, int firstVisible, Guid? kept = null, int lastVisible = int.MaxValue)
	{
		if (items.Count == 0)
		{
			return null;
		}

		bool InView(int index) => firstVisible < 0 || index >= firstVisible && index <= lastVisible;
		foreach (var (wanted, mustBeInView) in new[] { (hovered, true), (focused, true), (kept, false) })
		{
			if (wanted is not { } id)
			{
				continue;
			}

			for (var i = 0; i < items.Count; i++)
			{
				if (items[i].Id == id)
				{
					if (!mustBeInView || InView(i))
					{
						return items[i].Dto;
					}

					break;
				}
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
		var (first, last) = LibraryGridHost.VisibleRange(GridRepeater, GridScroller);
		var kept = XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject on && IsWithin(on, Stage) ? Stage.Current?.Id : null;
		var target = StageTarget(items, _hovered, FocusedInGrid(), first, kept, last < 0 ? int.MaxValue : last);
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
	// ---- the sections: tabs over the shared presets (1.6 colpage, LB2, LB5) -------------------------------------------

	/// <summary>
	/// 1.6 colpage (LB2, LB5): Cinematic's tabs are the library's presets strip (LibraryViewModel.Presets, the same model in
	/// every theme): All · Unwatched · Favorites · Recently added, then the library's Nixie collections. Unwatched lights
	/// whenever its filter is on, whatever else is on (it lit only alone, so the toolbar's switch could be on while the tab was
	/// dark); a lit tab clicked again is off; All takes every filter off and Recently added's sort back.
	/// </summary>
	private void ShowTabs() =>
		SectionTabs.SetTabs([.. ViewModel.Presets.Select(p => new CinematicTab(p.Label, p.Name, p.IsOn, p.Preset))]);

	private void SectionTabs_TabInvoked(object? sender, CinematicTab tab)
	{
		if (tab.Payload is LibraryPreset preset)
		{
			ViewModel.TogglePreset(preset);
			ShowTabs();
		}
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
