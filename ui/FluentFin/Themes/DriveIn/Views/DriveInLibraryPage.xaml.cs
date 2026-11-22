using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;


namespace FluentFin.Themes.DriveIn.Views;


/// <summary>
/// Drive-in: the Library (see DriveInLibraryPage.xaml), over the shared <see cref="LibraryViewModel"/>. The grid's keyboard,
/// Narrator and wheel model, the jump, the arrival's focus and Back's place are the shared host's (<see cref="LibraryGridHost"/>).
/// </summary>
public sealed partial class DriveInLibraryPage : Page
{
	public LibraryViewModel ViewModel { get; } = App.GetService<LibraryViewModel>();

	/// <summary>The medium poster cell (2:3 art and the caption).</summary>
	private const double CardWidth = 206, CardHeight = 372;

	// Seaglass scrolling: the grid's keyboard and Narrator model, and the wheel over the rest of the page
	private readonly CardGrid _grid;
	private readonly LibraryGridHost _host;
	private bool _band;
	private double _margin = 96;

	// Seaglass 1.1.4: when the page was made, for the first card's time in the log ("[perf] library: ..."), which the harness's
	// then-library-speed reads: the app's own time, without UI Automation's
	private readonly long _openedAt = System.Diagnostics.Stopwatch.GetTimestamp();
	private bool _firstCardLogged;

	public DriveInLibraryPage()
	{
		InitializeComponent();

		GridRepeater.ElementPrepared += (_, _) =>
		{
			if (!_firstCardLogged)
			{
				_firstCardLogged = true;
				Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(App.GetService<Microsoft.Extensions.Logging.ILogger<DriveInLibraryPage>>(),
					"[perf] library: first card {Ms} ms after the page was opened", (int)System.Diagnostics.Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds);
			}
		};

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		_grid.ForwardWheel(LibraryRoot);
		_host = new LibraryGridHost(this, ViewModel, GridScroller, GridRepeater, _grid, Letters);
		// Nixie 1.4.0 (item 15): the placeholders' cells against the grid's, in the log
		LibraryProbe.Attach(this, SkeletonRepeater, GridRepeater, i => LibraryProbe.SlotOf(SkeletonRepeater, i, this), i => LibraryProbe.SlotOf(GridRepeater, i, this), logFirstCard: false);
		// the readout ("M · 171 of 320") sits on the sky: on a board tile's plate, in the board's ink (the tiles' own colours in
		// Dark and Light; high contrast keeps the system's)
		if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
		{
			Letters.ReadoutPlate = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2A, 0x08, 0x14));
			Letters.ReadoutBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xF4, 0xEE));
		}
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(LibraryViewModel.Density) or nameof(LibraryViewModel.Layout))
			{
				ShowView();
			}
		};
		ShowView();

		ViewModel.State.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(LoadState.Status))
			{
				ShowPresets();
			}
		};
		// 1.6.1: the room kept for the strip while the titles load is its height as it showed
		PresetStrip.SizeChanged += (_, e) =>
		{
			if (e.NewSize.Height > 0)
			{
				_presetRoom = e.NewSize.Height;
			}
		};
		ShowPresets();

		// the header's strip of sky follows its size; the page's margins follow the window's width
		Header.SizeChanged += (_, e) => Sky.Layout = Controls.DriveInLayout.Strip(e.NewSize.Width, e.NewSize.Height);
		SizeChanged += (_, e) =>
		{
			// Nixie 1.4.0 (plan item 13): the theme's page grid (PageGutterNarrow to PageGutterWide), as every Drive-in page
			_margin = PageGrid.GutterAt(e.NewSize.Width);
			Fold(_band);
			GridScroller.Padding = new Thickness(_margin - 8, 24, _margin - 8, 24);
			SkeletonRepeater.Margin = new Thickness(_margin - 8, 24, _margin - 8, 0);
			AppliedFilters.Margin = new Thickness(_margin, 16, _margin, 0);
		};

		// a jump past the first line folds the sky first, so the letter's row lands at the top of the grid as it will be
		Letters.Jumped += (_, e) =>
		{
			if (e.Jump.Index >= _grid.ColumnCount && !_band)
			{
				Fold(true);
				UpdateLayout();
			}
		};

		// Nixie 1.4.0 (the UX review's DI-M7, item 12): the sky folds to a band once the grid has scrolled, and opens at its top
		GridScroller.ViewChanged += (_, _) =>
		{
			var band = GridScroller.VerticalOffset > (_band ? 8 : 40);
			if (band != _band)
			{
				Fold(band);
			}
		};
	}

	/// <summary>
	/// 1.6 colpage: the presets fold away with the sky (the band is the marquee, the tubes and the A to Z), and wait for the
	/// titles. 1.6.1 (colpage queue): while they load, the strip's row keeps its room (<see cref="_presetRoom"/>): the loaded
	/// page was 46 px taller than its placeholders, and the posters landed 46 px under them.
	/// </summary>
	private void ShowPresets()
	{
		var status = ViewModel.State.Status;
		PresetStrip.Visibility = _band || status != LoadStatus.Loaded ? Visibility.Collapsed : Visibility.Visible;
		HeaderContent.RowDefinitions[1].MinHeight = !_band && status is LoadStatus.Idle or LoadStatus.Loading ? _presetRoom : 0;
	}

	/// <summary>1.6.1: the strip's height (the board's 42 px tiles and the strip's 4 under them), as it last showed.</summary>
	private double _presetRoom = 46;

	/// <summary>The band's top: 8 under the 48 px bar (the band is a compact strip, not a title).</summary>
	public const double BandTop = 56;

	/// <summary>The header at rest (the large marquee) or as a band (the compact one, smaller tubes, tighter rows).</summary>
	private void Fold(bool band)
	{
		_band = band;
		// the marquee at the theme's title y at rest (1.6 colpage, L2: 88, 40 under the bar it runs under); the band keeps 56
		HeaderContent.Padding = new Thickness(_margin, band ? BandTop : PageGrid.Tokens().TitleY, _margin, band ? 10 : 18);
		HeaderContent.RowSpacing = band ? 10 : 22;
		ShowPresets();
		TitleSign.Style = (Style)Application.Current.Resources[band ? "DriveInCompactMarqueeStyle" : "DriveInLargeMarqueeStyle"];
		CountTubes.DigitHeight = band ? 28 : 40;
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
