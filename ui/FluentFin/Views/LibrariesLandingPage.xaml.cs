using FluentFin.Core.Theming;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views
{
	public sealed partial class LibrariesLandingPage : Page
	{
		public LibrariesLandingPageViewModel ViewModel { get; } = App.GetService<LibrariesLandingPageViewModel>();

		private bool _focused;

		public LibrariesLandingPage()
		{
			// 1.6 colpage (theme polish): a theme whose stage runs under the top bar (Drive-in's sky) says so before the shell reads it
			Controls.TopBar.SetExtendsUnder(this, ThemeResources.Find(StageUnderBarKey) is true);
			InitializeComponent();
			ApplyThemeParts();
			// 1.6 collections: the "Collections & playlists" heading in the theme's look (the colpage stream's LibrariesGroupHeadingStyle,
			// Drive-in's board face); today's style when the theme gives none
			if (ThemeResources.Find("LibrariesGroupHeadingStyle") is Style groupHeading)
			{
				CollectionsHeading.Style = groupHeading;
			}

			PageContent.SizeChanged += (_, _) => LayoutTiles();
			TitleFrame.SizeChanged += (_, _) => UpdateStage();
			ScrollContent.SizeChanged += (_, _) => UpdateStage();
			ViewModel.Media.CollectionChanged += (_, _) => UpdateStage();
			ViewModel.Media.CollectionChanged += (_, _) => LayoutTiles();
			ViewModel.Collections.CollectionChanged += (_, _) => LayoutTiles();
			// 1.6 topbar: each media library's star follows the top bar's favourites
			ViewModel.Media.CollectionChanged += (_, _) => ShowStars();
			Loaded += (_, _) =>
			{
				_top.PropertyChanged -= OnTopChanged;
				_top.PropertyChanged += OnTopChanged;
				ShowStars();
			};
			Unloaded += (_, _) => _top.PropertyChanged -= OnTopChanged;
			ViewModel.State.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(LoadState.Status) && ViewModel.State.IsLoaded && !_focused)
				{
					// arriving, the focus is on the first tile, not left in the top bar (the review's X8); Nixie 1.4.0 (bug hunt m4):
					// by the app's one arrival rule, never once the user has moved since the page came (ArrivalFocus)
					_focused = true;
					UiDispatch.Enqueue(DispatcherQueue, "the Libraries page's arrival focus",
						() => ArrivalFocus.TryTake(this, "the first tile", FocusFirstTile), Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
				}
			};
		}

		// ---- 1.6 colpage (theme polish): the theme's header stage and title frame ---------------------------------------

		public const string StageTemplateKey = "LibrariesStageTemplate", StageUnderBarKey = "LibrariesStageUnderBar", TitleFrameKey = "PageTitleFrameStyle", TitleFrameForegroundKey = "PageTitleFrameForeground";

		private void ApplyThemeParts()
		{
			var template = ThemeResources.Find(StageTemplateKey) as DataTemplate;
			Stage.ContentTemplate = template;
			Stage.Visibility = template is null ? Visibility.Collapsed : Visibility.Visible;
			if (ThemeResources.Find(TitleFrameKey) is Style { TargetType: var type } frame && (type is null || type.IsAssignableFrom(typeof(ContentControl))))
			{
				TitleFrame.Style = frame;
				// the frame's own title colour (Drive-in's lit board is dark in Light too); high contrast keeps the system's colours
				if (ThemeResources.Find(TitleFrameForegroundKey) is Microsoft.UI.Xaml.Media.Brush ink && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
				{
					TitleText.Foreground = ink;
				}
			}

			UpdateStage();
		}

		/// <summary>The stage as tall as the title's foot and a margin; the frame's words: the libraries in tubes ("6 SCREENS").</summary>
		private void UpdateStage()
		{
			var facts = new PageTitleFacts("", ViewModel.Media.Count, ViewModel.Media.Count == 1 ? "SCREEN ON THE LOT" : "SCREENS ON THE LOT");
			TitleFrame.DataContext = facts;
			if (Stage.Visibility != Visibility.Visible || !TitleFrame.IsLoaded || ScrollContent.ActualWidth <= 0)
			{
				return;
			}

			var bottom = TitleFrame.TransformToVisual(ScrollContent).TransformPoint(new Windows.Foundation.Point(0, TitleFrame.ActualHeight)).Y;
			Stage.Height = Math.Round(bottom + 28);
			Stage.Content = facts;
		}

		private bool FocusFirstTile()
		{
			var view = ViewModel.Media.Count > 0 ? MediaView : CollectionsView;
			return FindContainer(view) is Control first && first.Focus(FocusState.Programmatic);
		}

		private static Control? FindContainer(DependencyObject root)
		{
			var queue = new Queue<DependencyObject>([root]);
			while (queue.Count > 0)
			{
				var node = queue.Dequeue();
				if (node is ItemContainer container)
				{
					return container;
				}

				for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
				{
					queue.Enqueue(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
				}
			}

			return null;
		}

		/// <summary>
		/// The tiles share the rows evenly: as many columns as fit, then as few as still give the same number of rows (six tiles
		/// that fit five a row are three and three: no lone tile on a row of its own, the review's CL-p5), each tile as wide as
		/// the row allows up to <see cref="MaxTileWidth"/>.
		/// </summary>
		private void LayoutTiles()
		{
			// one tile size on the page: the two groups' tiles line up
			var width = PageContent.ActualWidth - PageContent.Padding.Left - PageContent.Padding.Right;
			var media = PageGridMetrics.Tiles(width, ViewModel.Media.Count);
			var collections = PageGridMetrics.Tiles(width, ViewModel.Collections.Count);
			var tile = Math.Min(ViewModel.Media.Count > 0 ? media.TileWidth : double.MaxValue, ViewModel.Collections.Count > 0 ? collections.TileWidth : double.MaxValue);
			Apply(MediaLayout, media.Columns, tile);
			Apply(CollectionsLayout, collections.Columns, tile);
		}

		private static void Apply(UniformGridLayout layout, int columns, double tile)
		{
			if (tile is <= 0 or double.MaxValue)
			{
				return;
			}

			layout.MaximumRowsOrColumns = columns;
			layout.MinItemWidth = tile;
			layout.MinItemHeight = PageGridMetrics.TileHeight(tile);
		}

		// 1.6 topbar
		private readonly TopNavigation _top = App.GetService<TopNavigation>();

		private void OnTopChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(TopNavigation.Favourites))
			{
				UiDispatch.Enqueue(DispatcherQueue, "the Libraries page's stars", ShowStars);
			}
		}

		private void ShowStars()
		{
			foreach (var tile in ViewModel.Media)
			{
				tile.IsFavourite = _top.IsFavourite(tile.Library.Id);
			}
		}

		/// <summary>The tile a star (its button, its menu item) is on: bound to its Tag (by its DataContext alone a click did nothing in the running app).</summary>
		private static LibraryTile? TileOf(object sender) => sender is FrameworkElement element ? element.Tag as LibraryTile ?? element.DataContext as LibraryTile : null;

		/// <summary>When a star was last pressed: the tile under it is not opened by the same click (ItemInvoked).</summary>
		private long _starPressedAt;

		/// <summary>1.6.1 (the review's "a mouse click on a star"): a tile's invoke this soon after a press on its star is the star's click.</summary>
		internal const long StarClickMs = 500;

		/// <summary>1.6.1: whether the tile's invoke at <paramref name="now"/> came from the star pressed at <paramref name="starPressedAt"/>.</summary>
		internal static bool StarTookTheClick(long starPressedAt, long now) => now - starPressedAt < StarClickMs;

		private void OnStarClick(object sender, RoutedEventArgs e)
		{
			_starPressedAt = Environment.TickCount64;
			if (TileOf(sender) is { } tile)
			{
				ToggleStar(tile, (UIElement)sender);
			}
		}

		/// <summary>A press on a star (the button handles it): the tile's own invoke on the same press is ignored, whichever comes first.</summary>
		private void OnStarLoaded(object sender, RoutedEventArgs e)
		{
			if (sender is UIElement star && !_starsHooked.Contains(star))
			{
				_starsHooked.Add(star);
				star.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _starPressedAt = Environment.TickCount64), true);
			}
		}

		private readonly HashSet<UIElement> _starsHooked = new(ReferenceEqualityComparer.Instance);

		private void OnStarMenuClick(object sender, RoutedEventArgs e)
		{
			if (TileOf(sender) is { } tile)
			{
				ToggleStar(tile, FavouritesNote);
			}
		}

		/// <summary>
		/// Nixie 1.6: a tile's star (its button or its menu): the library joins the top bar or leaves it, said to Narrator; a
		/// fourth is refused with a line under the title ("Up to 3 favourites in the top bar. Remove a star to add Anime.").
		/// </summary>
		private void ToggleStar(LibraryTile tile, UIElement near)
		{
			if (tile.Library.Id is not { } id)
			{
				return;
			}

			var change = _top.ToggleFavourite(id);
			ShowStars();
			var said = change switch
			{
				FavouriteChange.Added => $"{tile.Name} added to the top bar",
				FavouriteChange.Removed => $"{tile.Name} removed from the top bar",
				FavouriteChange.Full => TopFavourites.FullMessage(tile.Name),
				FavouriteChange.NotRead => TopFavourites.NotReadMessage, // 1.6.1: it said nothing
				_ => null,
			};
			var noted = change is FavouriteChange.Full or FavouriteChange.NotRead;
			FavouritesNote.Text = noted ? said : "";
			FavouritesNote.Visibility = noted ? Visibility.Visible : Visibility.Collapsed;
			if (said is not null)
			{
				Controls.TopStarAnnouncer.Say(near, said, noted);
			}
		}
		// end 1.6 topbar

		private void ItemsView_ItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
		{
			// 1.6 topbar: the click was the tile's star
			if (StarTookTheClick(_starPressedAt, Environment.TickCount64))
			{
				return;
			}

			if (args.InvokedItem is LibraryTile tile)
			{
				ViewModel.OpenTile(tile); // 1.6 collections: the group's tiles open the Collections page at their section
			}
		}
	}
}
