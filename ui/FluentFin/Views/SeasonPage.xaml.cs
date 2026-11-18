using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

public sealed partial class SeasonPage : Page
{
	public SeasonViewModel ViewModel { get; } = App.GetService<SeasonViewModel>();

	public SeasonPage()
	{
		InitializeComponent();
	}

	// 1.2.0: Back (or Forward) to this page gives its scroll position and focus back (ItemPagePlace)
	private ItemPagePlace? _place;

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading));
		_place.Arrived(e);
	}

	protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		_place?.Left();
		base.OnNavigatedFrom(e);
	}
}
