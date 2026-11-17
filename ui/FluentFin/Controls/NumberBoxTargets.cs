using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass 1.1.4 (UX review, prioritised fix 7): a NumberBox's inline spin buttons are WinUI's 32 x 24 px (their template's
/// MinWidth 32 and a 4 px margin inside a 32 px box), at the WCAG 2.5.8 floor. <c>NumberBoxTargets.MinTargetSize</c> makes each
/// at least that size: the box grows to fit them (its row is as tall as the buttons and their margin). The keys stay WinUI's:
/// Up and Down step by SmallChange, PageUp and PageDown by LargeChange, typing and Enter set the value, Escape undoes it.
/// The spin buttons are not Tab stops (WinUI's design): the keyboard steps from the box itself.
/// </summary>
public static class NumberBoxTargets
{
	/// <summary>WinUI's names for the inline spin buttons in the NumberBox template.</summary>
	public static readonly string[] SpinButtonNames = ["UpSpinButton", "DownSpinButton"];

	public static readonly DependencyProperty MinTargetSizeProperty = DependencyProperty.RegisterAttached(
		"MinTargetSize", typeof(double), typeof(NumberBoxTargets), new PropertyMetadata(0d, OnMinTargetSizeChanged));

	public static double GetMinTargetSize(DependencyObject element) => (double)element.GetValue(MinTargetSizeProperty);

	public static void SetMinTargetSize(DependencyObject element, double value) => element.SetValue(MinTargetSizeProperty, value);

	private static void OnMinTargetSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not NumberBox box)
		{
			return;
		}

		box.Loaded -= OnLoaded;
		box.Loaded += OnLoaded;
		if (box.IsLoaded)
		{
			Apply(box);
		}
	}

	private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((NumberBox)sender);

	/// <summary>Each inline spin button at least <see cref="GetMinTargetSize"/> wide and high (once its template is there).</summary>
	private static void Apply(NumberBox box)
	{
		var size = GetMinTargetSize(box);
		if (size <= 0)
		{
			return;
		}

		foreach (var button in box.FindDescendants().OfType<RepeatButton>().Where(x => SpinButtonNames.Contains(x.Name)))
		{
			button.MinWidth = Math.Max(button.MinWidth, size);
			button.MinHeight = Math.Max(button.MinHeight, size);
		}
	}
}
