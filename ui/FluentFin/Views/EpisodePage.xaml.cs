using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;


namespace FluentFin.Views;

public sealed partial class EpisodePage : Page
{
	public EpisodeViewModel ViewModel { get; } = App.GetService<EpisodeViewModel>();

	public EpisodePage()
	{
		InitializeComponent();
	}

	// 1.2.0: Back (or Forward) to this page gives its scroll position and focus back (ItemPagePlace)
	private ItemPagePlace? _place;

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading), primary: () => PlayButton);
		_place.Arrived(e);
	}

	// before the page goes: by OnNavigatedFrom it is unloaded and the focus has already left it (for the title bar)
	protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
	{
		_place?.Left();
		base.OnNavigatingFrom(e);
	}
}
