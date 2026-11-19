using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;


namespace FluentFin.Views;


/// <summary>
/// Classic: the Library (see LibraryPage.xaml). Nixie 1.4.0: the toolbar, the applied filters, the A to Z and the no-matches
/// state are the shared library controls; the focus, the jump and Back's place the shared host's (<see cref="LibraryGridHost"/>).
/// </summary>
public sealed partial class LibraryPage : Page
{
	public LibraryViewModel ViewModel { get; } = App.GetService<LibraryViewModel>();

	/// <summary>The medium cell (the card's 2:3 art and its caption).</summary>
	private const double CardWidth = 220, CardHeight = 390;

	// Seaglass scrolling: the grid's keyboard and Narrator model, and the wheel over the rest of the page
	private readonly CardGrid _grid;
	private readonly LibraryGridHost _host;

	// Seaglass 1.1.4: when the page was made, for the first card's time in the log ("[perf] library: ..."), which the harness's
	// then-library-speed reads: the app's own time, without UI Automation's
	private readonly long _openedAt = System.Diagnostics.Stopwatch.GetTimestamp();
	private bool _firstCardLogged;

	public LibraryPage()
	{
		InitializeComponent();

		GridRepeater.ElementPrepared += (_, _) =>
		{
			if (!_firstCardLogged)
			{
				_firstCardLogged = true;
				Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(App.GetService<Microsoft.Extensions.Logging.ILogger<LibraryPage>>(),
					"[perf] library: first card {Ms} ms after the page was opened", (int)System.Diagnostics.Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds);
			}
		};

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		_grid.ForwardWheel(LibraryRoot);
		_host = new LibraryGridHost(this, ViewModel, GridScroller, GridRepeater, _grid, Letters);
		// Nixie 1.4.0 (item 15): the placeholders' cells against the grid's, in the log
		LibraryProbe.Attach(this, SkeletonRepeater, GridRepeater, i => LibraryProbe.SlotOf(SkeletonRepeater, i, this), i => LibraryProbe.SlotOf(GridRepeater, i, this), logFirstCard: false);
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(LibraryViewModel.Density) or nameof(LibraryViewModel.Layout))
			{
				ShowView();
			}
		};
		ShowView();
	}

	/// <summary>Nixie 1.4.0 (item 13): the grid's density, or the list; the placeholders follow the grid's layout.</summary>
	private void ShowView()
	{
		LibraryDensities.Apply(GridLayout, ViewModel.Density, CardWidth, CardHeight);
		LibrarySkeleton.Mirror(GridLayout, SkeletonLayout);
		var list = ViewModel.IsList;
		var template = (DataTemplate)Resources[list ? "RowTemplate" : "CardTemplate"];
		if (!ReferenceEquals(GridRepeater.ItemTemplate, template))
		{
			GridRepeater.ItemTemplate = template;
			GridRepeater.Layout = list ? new StackLayout { Spacing = 4 } : GridLayout;
		}
	}

	// Seaglass 1.1.4: the sort menu shows the sort in use (also the one a Back put back)
	public static bool IsSort(ItemSortBy current, ItemSortBy item) => current == item;

	public static bool IsOrder(SortOrder current, SortOrder item) => current == item;

	/// <summary>1.2.0 (the button review, L15): the Sort button shows the sort in use. Nixie 1.4.0: with its direction, "Title · A to Z".</summary>
	public static string SortLabel(string sortText) => sortText;

	public static bool AnyOn(int activeFilters) => activeFilters > 0;

	public static Visibility LettersVisibility(IReadOnlyList<LetterJump> letters, LoadStatus status) =>
		letters.Count > 1 && BarVisibility(status) == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>Seaglass 1.1.4 (layout audit, item 22): the bar (count, sort, filter, A-Z) only over a library that has titles to show.</summary>
	public static Visibility BarVisibility(LoadStatus status) => status is LoadStatus.Empty or LoadStatus.Failed ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>Seaglass 1.1.4: coming back to the library (Back, Forward) puts the user where they were: the view model needs to know before it loads.</summary>
	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_host.OnNavigatedTo(e);
		base.OnNavigatedTo(e);
	}

	/// <summary>Seaglass 1.1.4: leaving for an item: the card the user was on (the grid's last-focused card) is kept for Back.</summary>
	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		_host.OnNavigatedFrom();
		base.OnNavigatedFrom(e);
	}
}
