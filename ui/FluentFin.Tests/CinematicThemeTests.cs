using System.Reflection;
using FluentFin.Core.ViewModels;
using FluentFin.Themes;
using FluentFin.Themes.Cinematic;
using FluentFin.Themes.Cinematic.Controls;
using FluentFin.Themes.Cinematic.Views;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.0, the Cinematic layout theme: what it registers (its id, name, dictionary and views for the browsing pages,
/// the shared page for everything else), and the parts of its controls that run without a XAML host: the focus zoom's rule
/// and what Narrator reads (a still is the shared card's list item, a lone still a button; the tabs a tab list).
/// </summary>
public sealed class CinematicThemeTests
{
	private static readonly CinematicTheme Theme = new();

	[Fact]
	public void IsTheCinematicTheme_WithItsDictionary()
	{
		Assert.Equal(LayoutThemeId.Cinematic, Theme.Id);
		Assert.Equal("Cinematic", Theme.DisplayName);
		Assert.Equal(new Uri("ms-appx:///Themes/Cinematic/Cinematic.xaml"), Theme.Dictionary);
	}

	[Theory]
	[InlineData(typeof(HomeViewModel), typeof(CinematicHomePage))]
	[InlineData(typeof(SeriesViewModel), typeof(CinematicSeriesPage))]
	[InlineData(typeof(SeasonViewModel), typeof(CinematicSeasonPage))]
	[InlineData(typeof(MovieViewModel), typeof(CinematicMoviePage))]
	[InlineData(typeof(EpisodeViewModel), typeof(CinematicEpisodePage))]
	[InlineData(typeof(LibraryViewModel), typeof(CinematicLibraryPage))]
	public void DrawsTheBrowsingPages_ItsOwnWay(Type viewModel, Type view)
	{
		Assert.Equal(view, Theme.ViewFor(viewModel));
		Assert.True(typeof(Microsoft.UI.Xaml.Controls.Page).IsAssignableFrom(view));
		// the view takes the shared view model, as the shared page does
		Assert.Equal(viewModel, view.GetProperty("ViewModel")!.PropertyType);
	}

	[Theory]
	[InlineData(typeof(SettingsViewModel))]
	[InlineData(typeof(SearchResultsViewModel))]
	[InlineData(typeof(ItemGridViewModel))]
	[InlineData(typeof(FluentFin.ViewModels.LoginViewModel))]
	public void EverythingElse_IsTheSharedPage(Type viewModel) => Assert.Null(Theme.ViewFor(viewModel));

	[Fact]
	public void IsFoundByReflection_LikeEveryTheme()
	{
		// ThemeService finds every ILayoutTheme in the app assembly; the theme needs a public parameterless constructor
		var themes = typeof(CinematicTheme).Assembly.GetTypes().Where(t => typeof(ILayoutTheme).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
		Assert.Contains(typeof(CinematicTheme), themes);
		Assert.NotNull(typeof(CinematicTheme).GetConstructor(Type.EmptyTypes));
	}

	[Fact]
	public void FocusZoom_IsTenPercent_OnlyWhileMotionIsAllowed()
	{
		Assert.Equal(1.1f, FocusZoom.ScaleFor(zoomed: true, motionAllowed: true));
		Assert.Equal(1f, FocusZoom.ScaleFor(zoomed: true, motionAllowed: false)); // Reduced, Solid: the rim alone, nothing moves
		Assert.Equal(1f, FocusZoom.ScaleFor(zoomed: false, motionAllowed: true));
		Assert.Equal(0.4f, FocusZoom.Origin.Y);
	}

	[Fact]
	public void Still_IsReadAsTheSharedCardIs_AListItemThatOpens()
	{
		var create = typeof(CinematicStill).GetMethod("OnCreateAutomationPeer", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.Equal(typeof(CinematicStill), create!.DeclaringType);
		Assert.True(typeof(FrameworkElementAutomationPeer).IsAssignableFrom(typeof(CinematicStillAutomationPeer)));
		Assert.True(typeof(IInvokeProvider).IsAssignableFrom(typeof(CinematicStillAutomationPeer)));

		Assert.Equal(AutomationControlType.ListItem, CinematicStillAutomationPeer.ControlTypeFor(inList: true));
		Assert.Equal(AutomationControlType.Button, CinematicStillAutomationPeer.ControlTypeFor(inList: false));
	}

	[Fact]
	public void PlayerPanel_SubtitleSizes_AreStepsTheBackendTakes_MediumTheDefault()
	{
		Assert.Contains(CinematicPlayerPanel.Sizes, s => s.Label == "Medium" && s.Percent == FluentFin.Core.Mpv.SubtitleSizes.Default);
		Assert.All(CinematicPlayerPanel.Sizes, s => Assert.Equal(FluentFin.Core.Mpv.SubtitleSizes.Clamp(s.Percent), s.Percent));
	}

	[Fact]
	public void PreviewStage_WaitsForTheFocusToRest_TheConceptsWindow()
	{
		// the concept: wait 150 to 250 ms before swapping the backdrop, so arrowing along a row loads nothing on the way
		Assert.InRange(CinematicPreviewStage.Settle.TotalMilliseconds, 150, 250);
	}

	[Fact]
	public void TheIsland_IsDarkGlassOverAHero_TheThemesGlassElsewhere()
	{
		Assert.Equal((Microsoft.UI.Xaml.Visibility.Collapsed, Microsoft.UI.Xaml.Visibility.Visible), CinematicIsland.GlassFor(overHero: true));
		Assert.Equal((Microsoft.UI.Xaml.Visibility.Visible, Microsoft.UI.Xaml.Visibility.Collapsed), CinematicIsland.GlassFor(overHero: false));
	}

	[Fact]
	public void Tabs_AreATabList_WhoseTabsCanBeSelected()
	{
		Assert.True(typeof(ISelectionProvider).IsAssignableFrom(typeof(CinematicTabStripAutomationPeer)));
		Assert.True(typeof(ISelectionItemProvider).IsAssignableFrom(typeof(CinematicTabButtonAutomationPeer)));
		Assert.Equal(typeof(CinematicTabStrip), typeof(CinematicTabStrip).GetMethod("OnCreateAutomationPeer", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
	}
}
