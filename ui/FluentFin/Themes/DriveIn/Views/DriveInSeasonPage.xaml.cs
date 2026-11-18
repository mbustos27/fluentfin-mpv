using FluentFin.Core.ViewModels;
using FluentFin.Themes.DriveIn.Controls;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.DriveIn.Views;

/// <summary>Drive-in: a season (see DriveInSeasonPage.xaml), over the shared <see cref="SeasonViewModel"/>.</summary>
public sealed partial class DriveInSeasonPage : Page
{
	public SeasonViewModel ViewModel { get; } = App.GetService<SeasonViewModel>();

	private ItemPagePlace? _place;

	public DriveInSeasonPage()
	{
		InitializeComponent();
		SizeChanged += (_, e) => Body.Padding = new Thickness(ItemStage.MarginFor(e.NewSize.Width), 8, ItemStage.MarginFor(e.NewSize.Width), 56);
	}

	/// <summary>The episode Play starts in this season (NEXT on the board, its time left beside Play).</summary>
	public static BaseItemDto? NextOf(List<BaseItemDto>? episodes) => DriveInItems.NextEpisode(episodes ?? [], null);

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading));
		_place.Arrived(e);
	}

	protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
	{
		_place?.Left();
		base.OnNavigatingFrom(e);
	}
}
