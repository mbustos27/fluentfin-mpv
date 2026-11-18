using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>Cinematic's sign-in backdrop (see CinematicSignInScene.xaml). High contrast shows the window colour alone.</summary>
public sealed partial class CinematicSignInScene : UserControl
{
	public CinematicSignInScene()
	{
		InitializeComponent();
		Loaded += (_, _) => { GlassMode.Changed += OnModeChanged; Apply(); };
		Unloaded += (_, _) => GlassMode.Changed -= OnModeChanged;
	}

	private void OnModeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Apply);

	private void Apply()
	{
		var plain = GlassMode.IsHighContrast;
		foreach (var child in Root.Children)
		{
			child.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
		}

		Root.Background = plain && Application.Current.Resources.TryGetValue("SystemColorWindowColor", out var color) && color is Windows.UI.Color window
			? new SolidColorBrush(window)
			: new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x07, 0x10));
	}
}
