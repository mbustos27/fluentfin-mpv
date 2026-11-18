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
					PreviewFirst();
					ShowPlace();
					break;
				case nameof(LibraryViewModel.FocusRequest) or nameof(LibraryViewModel.IsLoading):
					FocusRequested();
					PreviewFirst();
					break;
				case nameof(LibraryViewModel.SortBy) or nameof(LibraryViewModel.Order) or nameof(LibraryViewModel.CountText) or nameof(LibraryViewModel.Title):
					ShowTabs();
					ShowPlace();
					break;
			}
		};
		ViewModel.Filter.PropertyChanged += (_, _) => ShowTabs();

		// the preview follows the grid's focus (the still the keyboard or a click is on)
		GridRepeater.GotFocus += (_, e) =>
		{
			if (e.OriginalSource is CinematicStill { Model.Dto: { } dto })
			{
				Stage.Show(dto);
				ShowPlace(dto);
			}
		};
		LetterRepeater.ElementPrepared += (_, e) =>
		{
			AutomationProperties.SetPositionInSet(e.Element, e.Index + 1);
			AutomationProperties.SetSizeOfSet(e.Element, ViewModel.Letters.Count);
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

	private void PreviewFirst()
	{
		if (Stage.Current is null && !ViewModel.IsLoading && ViewModel.Items.Count > 0)
		{
			Stage.Show(ViewModel.Items[0].Dto);
			ShowPlace(ViewModel.Items[0].Dto);
		}
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

	/// <summary>"B · 23 movies · Name, A to Z": the focused title's letter (sorted by name), the count, the sort.</summary>
	private void ShowPlace(BaseItemDto? focused = null)
	{
		focused ??= Stage.Current;
		var parts = new List<string>();
		var letter = ViewModel.SortBy == ItemSortBy.Name && focused?.Name is { Length: > 0 } name ? char.ToUpperInvariant(name.TrimStart()[0]) : (char?)null;
		if (letter is { } l)
		{
			parts.Add(char.IsLetter(l) ? l.ToString() : "#");
		}

		if (!string.IsNullOrEmpty(ViewModel.CountText))
		{
			parts.Add(ViewModel.CountText);
		}

		parts.Add($"{ViewModel.SortName}, {(ViewModel.Order == SortOrder.Ascending ? ViewModel.SortBy == ItemSortBy.Name ? "A to Z" : "low to high" : ViewModel.SortBy == ItemSortBy.Name ? "Z to A" : "high to low")}");
		PlaceText.Text = string.Join(" · ", parts);
		MarkLetter(letter is { } c ? (char.IsLetter(c) ? c.ToString() : "#") : null);
	}

	/// <summary>The letter of the title in focus is underlined in the A-Z jump.</summary>
	private void MarkLetter(string? letter)
	{
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
