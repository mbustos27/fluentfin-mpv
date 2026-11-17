using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

public sealed partial class SeriesPage : Page
{
	public SeriesViewModel ViewModel { get; } = App.GetService<SeriesViewModel>();

	public SeriesPage()
	{
		InitializeComponent();
	}

	// Seaglass scrolling: a row's "See all" opens its items as a grid
	private void SimilarSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("More Like This", [.. ViewModel.Similar]));

	private void SeasonsSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Seasons", [.. ViewModel.Seasons]));
}
