using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// 1.2.0: the one way a page inside the shell shows the whole dusk scene (an empty state: an empty library, no results, the
/// server offline). While it is loaded it holds the full scene behind the page (<see cref="AmbientBackdrop"/>, one hold however
/// often it loads, released when it unloads, so a missed release cannot leave the scene behind other pages), and it draws its
/// content on the strong glass panel (the implicit SceneCard style in Glass.xaml): text straight on the scene's sky bands is not
/// readable (3:1 in places), on this panel it keeps 4.5:1 over the scene's brightest band (NixieButtonTests).
/// </summary>
public sealed partial class SceneCard : ContentControl
{
	public SceneCard()
	{
		DefaultStyleKey = typeof(ContentControl);
		Loaded += (_, _) => AmbientBackdrop.RequestFullScene(this);
		Unloaded += (_, _) => AmbientBackdrop.ReleaseFullScene(this);
	}
}
