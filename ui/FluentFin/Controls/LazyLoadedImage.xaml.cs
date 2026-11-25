using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;


namespace FluentFin.Controls;

#nullable disable

public sealed partial class LazyLoadedImage : UserControl
{
	public LazyLoadedImage()
	{
		InitializeComponent();

		// 1.7.5 (fx3): a fade that ends in the window's teardown sets nothing there
		ImageFadeIn.Completed += Services.UiDispatch.Frame("a picture's fade in", () =>
		{
			EnableBlurHash = false;
			BlurHashImageSource = null;
		});
	}

	[GeneratedDependencyProperty]
	public partial ImageSource ImageSource { get; set; }

	[GeneratedDependencyProperty]
	public partial bool EnableBlurHash { get; set; }

	[GeneratedDependencyProperty]
	public partial WriteableBitmap BlurHashImageSource { get; set; }

	[GeneratedDependencyProperty(DefaultValue = Stretch.UniformToFill)]
	public partial Stretch Stretch { get; set; }

	private void ImageOpened(object sender, RoutedEventArgs e)
	{
		if (App.IsClosing)
		{
			return; // 1.7.5 (fx3): nothing in the window's teardown (a picture that arrives there)
		}

		ImageFadeIn.Begin();
	}
}

#nullable restore