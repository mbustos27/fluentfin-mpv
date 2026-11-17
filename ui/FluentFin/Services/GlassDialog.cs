using Microsoft.UI.Xaml;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: how wide a ContentDialog's glass panel may grow (Styles\TextBlock.xaml). The dialog pages (edit metadata, identify,
/// manage library...) set <see cref="PagePanelMaxWidth"/> through their keyed style; a dialog made in code (a sign-in error, a
/// yes/no question) keeps WinUI's own <see cref="DefaultPanelMaxWidth"/>, so its lines stay short enough to read.
/// </summary>
public static class GlassDialog
{
	/// <summary>WinUI's ContentDialogMaxWidth.</summary>
	public const double DefaultPanelMaxWidth = 548;

	/// <summary>The dialog pages with forms and pictures (DefaultContentDialogStyleWithTitleBackground).</summary>
	public const double PagePanelMaxWidth = 1000;

	public static readonly DependencyProperty PanelMaxWidthProperty = DependencyProperty.RegisterAttached(
		"PanelMaxWidth", typeof(double), typeof(GlassDialog), new PropertyMetadata(DefaultPanelMaxWidth));

	public static double GetPanelMaxWidth(DependencyObject element) => (double)element.GetValue(PanelMaxWidthProperty);

	public static void SetPanelMaxWidth(DependencyObject element, double value) => element.SetValue(PanelMaxWidthProperty, value);
}
