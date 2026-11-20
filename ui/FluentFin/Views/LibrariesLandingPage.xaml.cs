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
			InitializeComponent();
			PageContent.SizeChanged += (_, _) => LayoutTiles();
			ViewModel.Media.CollectionChanged += (_, _) => LayoutTiles();
			ViewModel.Collections.CollectionChanged += (_, _) => LayoutTiles();
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

		private void ItemsView_ItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
		{
			if (args.InvokedItem is LibraryTile tile)
			{
				ViewModel.NavigateToLibrary(tile.Library);
			}
		}
	}
}
