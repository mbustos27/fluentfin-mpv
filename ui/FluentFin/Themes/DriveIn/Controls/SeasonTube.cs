using FluentFin.Core;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: one season on the SHOWTIMES board's picker, a nixie tube with its number: lit for the season shown (with the
/// neon underline the top bar's tabs have), resting for the others (still 4.5:1). A season with no number (Specials) shows
/// its name, "Extras". The button around it carries the name ("Season 2, current season"); this is what the eye reads.
/// </summary>
public sealed partial class SeasonTube : StackPanel
{
	public static readonly DependencyProperty PillProperty =
		DependencyProperty.Register(nameof(Pill), typeof(object), typeof(SeasonTube), new PropertyMetadata(null, (d, _) => ((SeasonTube)d).Apply()));

	public SeasonPill? Pill { get => GetValue(PillProperty) as SeasonPill; set => SetValue(PillProperty, value); }

	/// <summary>The tube's height.</summary>
	public const double TubeHeight = 52;

	private readonly NixieReadout _tube = new() { DigitHeight = TubeHeight, HorizontalAlignment = HorizontalAlignment.Center };
	private readonly TextBlock _label = new()
	{
		VerticalAlignment = VerticalAlignment.Center,
		Height = TubeHeight,
		Padding = new Thickness(6, 15, 6, 0),
		FontSize = 15,
		FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
	};
	private readonly Border _underline = new() { Height = 3, Width = 26, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Center };

	public SeasonTube()
	{
		Spacing = 6;
		IsHitTestVisible = false;
		AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
		Children.Add(_tube);
		Children.Add(_label);
		Children.Add(_underline);
		ActualThemeChanged += (_, _) => Apply();
	}

	private void Apply()
	{
		var pill = Pill;
		var number = DriveInItems.SeasonTube(pill?.Dto);
		// Nixie 1.5 (season speed): lit or not before its number, so the tube's cells are made once (a tube not picked was
		// made lit, then again unlit, for each season shown)
		_tube.IsLit = pill?.IsCurrent == true;
		_tube.Text = number ?? "";
		_tube.Visibility = number is null ? Visibility.Collapsed : Visibility.Visible;
		_label.Text = number is null ? DriveInItems.SeasonLabel(pill?.Dto) : "";
		_label.Visibility = number is null ? Visibility.Visible : Visibility.Collapsed;
		_label.Foreground = (Brush)Application.Current.Resources[pill?.IsCurrent == true ? "DriveInPageTextBrush" : "DriveInPageTextSecondaryBrush"];
		_underline.Background = (Brush)Application.Current.Resources["NixieAccentBrush"];
		_underline.Opacity = pill?.IsCurrent == true ? 1 : 0;
	}
}
