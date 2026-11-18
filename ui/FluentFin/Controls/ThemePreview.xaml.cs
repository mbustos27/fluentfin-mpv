using FluentFin.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>Nixie 1.3.0: a layout theme's sketch (ThemePreview.xaml), the one for <see cref="Theme"/>.</summary>
public sealed partial class ThemePreview : UserControl
{
	public ThemePreview()
	{
		InitializeComponent();
		Show();
	}

	public static readonly DependencyProperty ThemeProperty =
		DependencyProperty.Register(nameof(Theme), typeof(LayoutThemeId), typeof(ThemePreview), new PropertyMetadata(LayoutThemeId.Classic, (d, _) => ((ThemePreview)d).Show()));

	public LayoutThemeId Theme
	{
		get => (LayoutThemeId)GetValue(ThemeProperty);
		set => SetValue(ThemeProperty, value);
	}

	/// <summary>The sketch's name in ThemePreview.xaml for a theme.</summary>
	public static string SketchFor(LayoutThemeId id) => $"{id}Sketch";

	private void Show()
	{
		foreach (var sketch in new[] { ClassicSketch, DriveInSketch, CinematicSketch, EditorialSketch })
		{
			sketch.Visibility = sketch.Name == SketchFor(Theme) ? Visibility.Visible : Visibility.Collapsed;
		}
	}
}
