using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: the sign-in page's backdrop (SignInBackdropTemplate): the lot at dusk filling the window, the outdoor screen in
/// the middle where the shared sign-in form sits, the sun to the right, two rows of cars. The window's own colour in high
/// contrast. Decorative.
/// </summary>
public sealed partial class SignInLot : Grid
{
	private readonly DriveInScene _back = new() { Layer = SceneLayer.Back };
	private readonly DriveInScene _front = new() { Layer = SceneLayer.Front };
	private readonly Border _screen;

	public SignInLot()
	{
		IsHitTestVisible = false;
		AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
		_screen = new Border
		{
			Padding = new Thickness(7),
			CornerRadius = new CornerRadius(8),
			BorderThickness = new Thickness(1),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Child = new Border
			{
				CornerRadius = new CornerRadius(4),
				BorderThickness = new Thickness(2),
				Background = new SolidColorBrush(DriveInScene.C("#F0170509")),
			},
		};
		Children.Add(_back);
		Children.Add(_screen);
		Children.Add(_front);
		SizeChanged += (_, e) => Apply(e.NewSize.Width, e.NewSize.Height);
		Loaded += (_, _) => Apply(ActualWidth, ActualHeight);
		ActualThemeChanged += (_, _) => Apply(ActualWidth, ActualHeight);
	}

	private void Apply(double width, double height)
	{
		if (width <= 0 || height <= 0)
		{
			return;
		}

		var layout = DriveInLayout.SignIn(width, height);
		_back.Layout = layout;
		_front.Layout = layout;
		var high = BulbFrame.HighContrast();
		Background = high ? (Brush)Application.Current.Resources["DriveInGroundBrush"] : null;
		_screen.Visibility = high ? Visibility.Collapsed : Visibility.Visible;
		_screen.Margin = new Thickness(layout.Screen.X, layout.Screen.Y, 0, 0);
		_screen.Width = layout.Screen.Width;
		_screen.Height = layout.Screen.Height;
		_screen.Background = (Brush)Application.Current.Resources["DriveInScreenFrameBrush"];
		_screen.BorderBrush = (Brush)Application.Current.Resources["DriveInScreenFrameEdgeBrush"];
		((Border)_screen.Child).BorderBrush = (Brush)Application.Current.Resources["DriveInScreenEdgeBrush"];
	}
}
