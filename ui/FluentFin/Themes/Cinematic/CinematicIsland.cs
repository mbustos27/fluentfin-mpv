using CommunityToolkit.WinUI;
using FluentFin.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.Cinematic;

/// <summary>
/// The island's glass follows the bar (TopBarStyle sets <c>cin:CinematicIsland.Follows</c> on the chrome): over a hero the
/// bar's text is the dark theme's, so the island shows its dark glass (HeroGlass) in every theme; elsewhere the theme's own
/// glass (ContentGlass). From code: neither the template's visual state nor a binding to IsOverHero took effect.
/// </summary>
public static class CinematicIsland
{
	public static readonly DependencyProperty FollowsProperty =
		DependencyProperty.RegisterAttached("Follows", typeof(bool), typeof(CinematicIsland), new PropertyMetadata(false, OnFollowsChanged));

	public static bool GetFollows(DependencyObject element) => (bool)element.GetValue(FollowsProperty);

	public static void SetFollows(DependencyObject element, bool value) => element.SetValue(FollowsProperty, value);

	private static readonly DependencyProperty TokenProperty =
		DependencyProperty.RegisterAttached("Token", typeof(object), typeof(CinematicIsland), new PropertyMetadata(null));

	/// <summary>Which glass shows: the dark one over a hero.</summary>
	public static (Visibility Content, Visibility Hero) GlassFor(bool overHero) =>
		overHero ? (Visibility.Collapsed, Visibility.Visible) : (Visibility.Visible, Visibility.Collapsed);

	private static void OnFollowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not TopBarChrome chrome)
		{
			return;
		}

		if (e.NewValue is true && chrome.GetValue(TokenProperty) is null)
		{
			var token = chrome.RegisterPropertyChangedCallback(TopBarChrome.IsOverHeroProperty, (_, _) => Apply(chrome));
			chrome.SetValue(TokenProperty, token);
			chrome.Loaded += (_, _) => Apply(chrome);
			// the template's parts exist only once it is applied: until then, try again as the bar lays out
			var found = false;
			chrome.LayoutUpdated += (_, _) =>
			{
				if (!found)
				{
					found = Apply(chrome);
				}
			};
			Apply(chrome);
		}
		else if (e.NewValue is not true && chrome.GetValue(TokenProperty) is long registered)
		{
			chrome.UnregisterPropertyChangedCallback(TopBarChrome.IsOverHeroProperty, registered);
			chrome.ClearValue(TokenProperty);
		}
	}

	private static bool Apply(TopBarChrome chrome)
	{
		if (!GetFollows(chrome))
		{
			return false;
		}

		var content = chrome.FindDescendant<Grid>(g => g.Name == "ContentGlass");
		var hero = chrome.FindDescendant<Grid>(g => g.Name == "HeroGlass");
		if (content is null || hero is null)
		{
			return false;
		}

		var (c, h) = GlassFor(chrome.IsOverHero);
		content.Visibility = c;
		hero.Visibility = h;
		return true;
	}
}
