using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Editorial.Views;

/// <summary>The Editorial theme's episode page (see EditorialEpisodePage.xaml), over the shared <see cref="EpisodeViewModel"/>.</summary>
public sealed partial class EditorialEpisodePage : Page
{
	public EpisodeViewModel ViewModel { get; } = App.GetService<EpisodeViewModel>();

	private ItemPagePlace? _place;

	public EditorialEpisodePage()
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
		EditorialPageLayout.SideBySide(DetailsGrid, Facts, FactsColumn, !narrow);
	}

	private void Header_CastRequested(object? sender, EventArgs e) =>
		Cast.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.2, AnimationDesired = FluentFin.Controls.SafeMotion.Allowed });

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading));
		_place.Arrived(e);
	}

	protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
	{
		_place?.Left();
		base.OnNavigatingFrom(e);
	}

	/// <summary>Over the title: "THE IRON CIRCUS · S1 E3".</summary>
	public static string Kicker(BaseItemDto? dto) =>
		string.Join(" · ", new[] { dto?.SeriesName?.Trim(), FluentFin.Core.ItemLabels.EpisodeCode(dto) }.Where(x => !string.IsNullOrEmpty(x))).ToUpperInvariant();

	/// <summary>Beside Resume: what is left of the episode.</summary>
	public static string Left(BaseItemDto? dto) => dto?.UserData?.PlaybackPositionTicks is > 0 ? EditorialHome.TimeLeft(dto) : "";

	public static Visibility Shown(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
}

