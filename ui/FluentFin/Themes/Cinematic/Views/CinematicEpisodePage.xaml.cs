using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Themes.Cinematic.Controls;
using FluentFin.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Cinematic.Views;

/// <summary>Cinematic's episode page (see CinematicEpisodePage.xaml), over the shared <see cref="EpisodeViewModel"/>.</summary>
public sealed partial class CinematicEpisodePage : Page
{
	public EpisodeViewModel ViewModel { get; } = App.GetService<EpisodeViewModel>();

	private ItemPagePlace? _place;
	private readonly CinematicItemPageLayout _layout;

	public CinematicEpisodePage()
	{
		CinematicResources.Ensure();
		InitializeComponent();
		_layout = new CinematicItemPageLayout(this, PageContent, Stage, narrow =>
		{
			var side = narrow ? 48 : 96;
			NextBlock.Margin = new Thickness(side, 0, 0, 24);
			Body.Margin = new Thickness(side, 24, 48, 56);
		});
	}

	/// <summary>The next episode as the still's item model (none: no still).</summary>
	public static BaseItemViewModel? Wrap(Jellyfin.Sdk.Generated.Models.BaseItemDto? episode) => episode is null ? null : BaseItemViewModel.FromDto(episode);

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

}
