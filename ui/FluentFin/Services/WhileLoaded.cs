using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.7.4: a page hears its view model only while it is the page shown. Back and Forward hand a kept view model to a new
/// page (NavigationService, PageKeep), as a theme switch does: a handler the page left had added in its constructor kept that
/// page (and its pictures) alive as long as the view model, one more for every Back, and had it answer changes it no longer
/// showed. The page listens from its constructor (what its view model says while it arrives counts) to its Unloaded, and again
/// from each Loaded.
/// </summary>
public static class WhileLoaded
{
	/// <summary><paramref name="attach"/> now and on each Loaded (after <paramref name="detach"/>, so never twice); <paramref name="detach"/> on Unloaded.</summary>
	public static void Hold(FrameworkElement page, Action attach, Action detach, Action? onLoaded = null)
	{
		page.Loaded += (_, _) =>
		{
			detach();
			attach();
			onLoaded?.Invoke();
		};
		page.Unloaded += (_, _) => detach();
		attach();
	}

	/// <summary>
	/// <paramref name="handler"/> hears <paramref name="source"/> until the page's Unloaded, and again from each Loaded;
	/// <paramref name="onLoaded"/> (what the handler would show, as it is now) runs on each Loaded, for what changed meanwhile.
	/// </summary>
	public static void Listen(FrameworkElement page, INotifyPropertyChanged source, PropertyChangedEventHandler handler, Action? onLoaded = null) =>
		Hold(page, () => source.PropertyChanged += handler, () => source.PropertyChanged -= handler, onLoaded);
}
