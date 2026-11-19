using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Themes.Cinematic.Controls;
using FluentFin.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Cinematic.Views;

/// <summary>Cinematic's movie page (see CinematicMoviePage.xaml), over the shared <see cref="MovieViewModel"/>.</summary>
public sealed partial class CinematicMoviePage : Page
{
	public MovieViewModel ViewModel { get; } = App.GetService<MovieViewModel>();

	private ItemPagePlace? _place;
	private readonly CinematicItemPageLayout _layout;

	public CinematicMoviePage()
	{
		CinematicResources.Ensure();
		InitializeComponent();
		_layout = new CinematicItemPageLayout(this, PageContent, Stage, narrow =>
		{
			var side = narrow ? 48 : 96;
			SimilarShelf.Margin = new Thickness(side - 12, 0, 0, 24);
			Body.Margin = new Thickness(side, 24, 48, 56);
		});
	}

	private void SimilarShelf_Loaded(object sender, RoutedEventArgs e) => CinematicShelf.Fit(SimilarShelf, 12, null);

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

	private void SimilarSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("More Like This", [.. ViewModel.Similar]));
}
