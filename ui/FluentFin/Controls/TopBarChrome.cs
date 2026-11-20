using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.3.0: the shared top bar's chrome, the one part of the bar a layout theme restyles (its TopBarStyle, a Style for
/// this control). The chrome is drawn BEHIND the bar (TitleBarControl's TitleBar: Back, the wordmark, the tabs, search, the
/// account), never around it: a theme switch restyles the chrome alone, so the bar's parts are never moved, rebuilt or
/// reloaded, and keep their state (the search text, an open account menu, the window's title-bar regions) and their
/// automation names. A theme's TopBarStyle sets:
/// <list type="bullet">
/// <item>Template: what is drawn behind the bar, with the states of the group "BarStates": "OverContent" (the bar over a page
/// that starts below it: 1.2.0's glass) and "OverHero" (over a page whose artwork runs under the bar: 1.2.0's dark scrim;
/// the bar's text is then in the dark theme);</item>
/// <item>Padding: how far the bar's parts sit in from the chrome's edges (a floating island insets them).</item>
/// </list>
/// The default style (Styles/TopBarChrome.xaml) is 1.2.0's bar, which Classic keeps.
/// Nixie 1.4.0 (the library UX review, E-m8): a theme's ornament on the bar, the template part named <see cref="OrnamentPart"/>
/// (Editorial's sunset rule), is not drawn while the player is open: it ran across the top of the video.
/// </summary>
public sealed partial class TopBarChrome : Control
{
	/// <summary>The template part a theme draws as decoration along the bar (hidden over the player).</summary>
	public const string OrnamentPart = "SunsetRule";

	private UIElement? _ornament;
	private PlayerChrome? _player;

	public TopBarChrome()
	{
		DefaultStyleKey = typeof(TopBarChrome);
		IsTabStop = false;
		Loaded += (_, _) => FollowPlayer(true);
		Unloaded += (_, _) => FollowPlayer(false);
	}

	public static readonly DependencyProperty IsOverHeroProperty =
		DependencyProperty.Register(nameof(IsOverHero), typeof(bool), typeof(TopBarChrome), new PropertyMetadata(false, (d, _) => ((TopBarChrome)d).ShowState(true)));

	/// <summary>Whether the page under the bar runs its artwork under it (TopBar.ExtendsUnder).</summary>
	public bool IsOverHero
	{
		get => (bool)GetValue(IsOverHeroProperty);
		set => SetValue(IsOverHeroProperty, value);
	}

	public static readonly DependencyProperty IsOverPlayerProperty =
		DependencyProperty.Register(nameof(IsOverPlayer), typeof(bool), typeof(TopBarChrome), new PropertyMetadata(false, (d, _) => ((TopBarChrome)d).ShowOrnament()));

	/// <summary>1.4.0: whether the player is open under the bar (PlayerChrome.IsPlayerOpen): the ornament is not drawn then.</summary>
	public bool IsOverPlayer
	{
		get => (bool)GetValue(IsOverPlayerProperty);
		set => SetValue(IsOverPlayerProperty, value);
	}

	/// <summary>The state for <see cref="IsOverHero"/>.</summary>
	public static string StateFor(bool overHero) => overHero ? "OverHero" : "OverContent";

	// 1.4.0 contrast
	public static readonly DependencyProperty HeroInLightProperty =
		DependencyProperty.Register(nameof(HeroInLight), typeof(bool), typeof(TopBarChrome), new PropertyMetadata(true, (d, _) => ((TopBarChrome)d).HeroInLightChanged?.Invoke(d, EventArgs.Empty)));

	/// <summary>
	/// Nixie 1.4.0 (the library review's DI-B1, plan item 3): whether the bar takes its hero look (its OverHero state, the bar's
	/// parts in the dark theme) over a hero in the light theme too: true by default (1.2.0's dark scrim with light text, in
	/// both themes). A theme's TopBarStyle sets it false when its bar over a hero is its bar over content by day: Drive-in's
	/// noon bar is the same light glass with dark text over the libraries, Home and an item's art as over the Libraries page
	/// (it was the dusk bar's mauve over the noon sky, with the dark theme's text: 1.99 to 3.18:1).
	/// </summary>
	public bool HeroInLight
	{
		get => (bool)GetValue(HeroInLightProperty);
		set => SetValue(HeroInLightProperty, value);
	}

	/// <summary>Raised when <see cref="HeroInLight"/> changes (a theme switch restyled the chrome): the bar decides its look again.</summary>
	public event EventHandler? HeroInLightChanged;

	/// <summary>
	/// Whether the bar shows its hero look (the OverHero state, its parts in the dark theme): over a page whose art runs under it
	/// (<paramref name="underBar"/>), unless the app is light (<paramref name="appIsLight"/>) and the theme's bar keeps its
	/// look over content there (<paramref name="heroInLight"/> false).
	/// </summary>
	public static bool ShowsHero(bool underBar, bool heroInLight, bool appIsLight) => underBar && (heroInLight || !appIsLight);
	// end 1.4.0 contrast

	/// <summary>Whether the theme's ornament is drawn: never over the player.</summary>
	public static bool ShowsOrnament(bool overPlayer) => !overPlayer;

	protected override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		_ornament = GetTemplateChild(OrnamentPart) as UIElement;
		ShowState(false);
		ShowOrnament();
	}

	private void ShowState(bool useTransitions) => VisualStateManager.GoToState(this, StateFor(IsOverHero), useTransitions);

	private void ShowOrnament()
	{
		if (_ornament is not null)
		{
			_ornament.Visibility = ShowsOrnament(IsOverPlayer) ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private void FollowPlayer(bool follow)
	{
		if (_player is not null)
		{
			_player.PropertyChanged -= OnPlayerChanged;
			_player = null;
		}

		if (!follow || Application.Current is not App)
		{
			return;
		}

		_player = App.GetService<PlayerChrome>();
		_player.PropertyChanged += OnPlayerChanged;
		IsOverPlayer = _player.IsPlayerOpen;
	}

	private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(PlayerChrome.IsPlayerOpen) && _player is not null)
		{
			IsOverPlayer = _player.IsPlayerOpen;
		}
	}
}
