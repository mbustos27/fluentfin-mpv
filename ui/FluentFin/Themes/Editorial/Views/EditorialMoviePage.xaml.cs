using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Editorial.Views;

/// <summary>The Editorial theme's film page (see EditorialMoviePage.xaml), over the shared <see cref="MovieViewModel"/>.</summary>
public sealed partial class EditorialMoviePage : Page
{
	public MovieViewModel ViewModel { get; } = App.GetService<MovieViewModel>();

	private ItemPagePlace? _place;

	public EditorialMoviePage()
	{
		InitializeComponent();
		SizeChanged += (_, e) => Layout(e.NewSize.Width);
		Loaded += (_, _) => Layout(ActualWidth);
	}

	private void Layout(double width)
	{
		if (width <= 0)
		{
			return;
		}

		var narrow = width < 1400;
		Header.IsNarrow = narrow;
		Body.Margin = new Thickness(narrow ? 40 : 56, 8, narrow ? 40 : 56, 56);
		SimilarShelf.IsNarrow = narrow;
		EditorialPageLayout.SideBySide(DetailsGrid, Facts, FactsColumn, !narrow);
	}

	private void Header_CastRequested(object? sender, EventArgs e) =>
		Cast.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.2, AnimationDesired = FluentFin.Controls.SafeMotion.Allowed });

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading), primary: () => PlayButton);
		_place.Arrived(e);
	}

	protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
	{
		_place?.Left();
		base.OnNavigatingFrom(e);
	}

	private void SimilarSeeAll(object? sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("More Like This", [.. ViewModel.Similar]));

	/// <summary>Beside Resume: what is left of the film.</summary>
	public static string Left(BaseItemDto? dto) => dto?.UserData?.PlaybackPositionTicks is > 0 ? EditorialHome.TimeLeft(dto) : "";

	public static Visibility Shown(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Editorial: the item pages' two-column blocks, one column in a narrow window.</summary>
internal static class EditorialPageLayout
{
	public static void SideBySide(Grid grid, FrameworkElement second, ColumnDefinition secondColumn, bool wide)
	{
		Grid.SetColumn(second, wide ? 1 : 0);
		Grid.SetRow(second, wide ? 0 : 1);
		secondColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
		grid.ColumnSpacing = wide ? 56 : 0;
	}
}
