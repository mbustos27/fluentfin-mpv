using FluentFin.Core.ViewModels;
using FluentFin.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;


namespace FluentFin.Themes.DriveIn.Views;

/// <summary>Drive-in: an episode: the shared page's sections under the marquee board (ItemStage), over the shared <see cref="EpisodeViewModel"/>.</summary>
public sealed partial class DriveInEpisodePage : Page
{
	public EpisodeViewModel ViewModel { get; } = App.GetService<EpisodeViewModel>();

	public DriveInEpisodePage()
	{
		InitializeComponent();
		SizeChanged += (_, e) => Body.Padding = new Thickness(Controls.DriveInLayout.MarginFor(e.NewSize.Width), 8, Controls.DriveInLayout.MarginFor(e.NewSize.Width), 56);
	}

	// 1.2.0: Back (or Forward) to this page gives its scroll position and focus back (ItemPagePlace)
	private ItemPagePlace? _place;

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading));
		_place.Arrived(e);
	}

	// before the page goes: by OnNavigatedFrom it is unloaded and the focus has already left it (for the title bar)
	protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
	{
		_place?.Left();
		base.OnNavigatingFrom(e);
	}
}
