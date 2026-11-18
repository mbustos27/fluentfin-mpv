using FluentFin.Helpers;
using FluentFin.Themes;
using FluentFin.Contracts.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.0: a layout theme switch at run time (Settings &gt; Appearance, "Choose a look"). The place is kept
/// (<see cref="ThemeAnchors"/>), the choice is saved and its dictionary swapped (<see cref="ThemeService.Choose"/>, then
/// <see cref="ThemeResources"/>, which restyles the top bar in place), the page shown again in the new view with the same
/// view model (<see cref="NavigationService.ReloadForTheme"/>), the window crossfades for <see cref="CrossfadeTime"/> (none
/// when motion is reduced) and Narrator says "Theme changed to &lt;name&gt;".
/// </summary>
public sealed class ThemeSwitcher(ThemeService themes, INavigationService navigation, ILogger<ThemeSwitcher>? log = null)
{
	/// <summary>The crossfade's length.</summary>
	public static readonly TimeSpan CrossfadeTime = TimeSpan.FromMilliseconds(200);

	/// <summary>The crossfade for the current motion setting: <see cref="CrossfadeTime"/>, or none (<see cref="TimeSpan.Zero"/>).</summary>
	public static TimeSpan CrossfadeFor(bool motionAllowed) => motionAllowed ? CrossfadeTime : TimeSpan.Zero;

	/// <summary>What Narrator says after a switch.</summary>
	public static string Announcement(string displayName) => $"Theme changed to {displayName}";

	/// <summary>The window's crossfade layer (MainWindow's ThemeFade image), set by the window.</summary>
	internal static Image? FadeLayer { get; set; }

	/// <summary>The element the crossfade copies (the window's content), set by the window.</summary>
	internal static UIElement? FadeSource { get; set; }

	/// <summary>Switches to <paramref name="id"/> (saved on this PC). False when the theme shown did not change.</summary>
	public async Task<bool> SwitchAsync(LayoutThemeId id)
	{
		if (ReferenceEquals(ThemeChoice.Resolve(id, themes.Themes), themes.Active))
		{
			themes.Choose(id);
			return false;
		}

		var nav = navigation as NavigationService;
		var frame = nav?.Frame;
		var anchors = frame?.Content is Page shown && shown.GetType() != typeof(Views.VideoPlayerPage) ? ThemeAnchors.Capture(shown) : null;
		var fade = CrossfadeFor(GlassMode.MotionAllowed);
		var faded = fade > TimeSpan.Zero && await CaptureAsync();

		themes.Choose(id);
		var reload = nav?.ReloadForTheme() ?? ThemeReload.None;
		log?.LogInformation("Layout theme {Theme}: {Reload}, crossfade {Crossfade} ms", themes.ActiveId, reload, faded ? (int)fade.TotalMilliseconds : 0);

		if (reload == ThemeReload.Reloaded && frame?.Content is Page fresh && anchors is not null)
		{
			anchors.RestoreWhenLoaded(fresh);
		}

		if (faded)
		{
			FadeOut(fade);
		}

		Announce(frame, Announcement(themes.Active?.DisplayName ?? id.ToString()));
		return true;
	}

	/// <summary>The window as it is, on the fade layer (over everything); false when it could not be copied.</summary>
	private static async Task<bool> CaptureAsync()
	{
		if (FadeLayer is not { } layer || FadeSource is not { ActualSize.X: > 0 } source)
		{
			return false;
		}

		try
		{
			var bitmap = new RenderTargetBitmap();
			await bitmap.RenderAsync(source);
			layer.Source = bitmap;
			layer.Opacity = 1;
			layer.Visibility = Visibility.Visible;
			return true;
		}
		catch (Exception)
		{
			layer.Source = null;
			return false;
		}
	}

	private static void FadeOut(TimeSpan duration)
	{
		if (FadeLayer is not { } layer)
		{
			return;
		}

		var animation = new DoubleAnimation { From = 1, To = 0, Duration = duration, EnableDependentAnimation = false, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
		Storyboard.SetTarget(animation, layer);
		Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
		var story = new Storyboard();
		story.Children.Add(animation);
		story.Completed += (_, _) =>
		{
			layer.Visibility = Visibility.Collapsed;
			layer.Source = null;
		};
		story.Begin();
	}

	private static void Announce(FrameworkElement? element, string text)
	{
		if (element is null)
		{
			return;
		}

		var peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
		peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "NixieThemeChanged");
	}
}
