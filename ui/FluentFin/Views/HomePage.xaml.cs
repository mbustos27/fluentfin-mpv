using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;


namespace FluentFin.Views;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class HomePage : Page
{
	public HomeViewModel ViewModel { get; } = App.GetService<HomeViewModel>();

	public HomePage()
	{
		InitializeComponent();
	}

	// fluentfin-mpv: a section is on screen while it loads (placeholder cards) and afterwards only if it has items
	public static Microsoft.UI.Xaml.Visibility SectionVisibility(bool isLoading, bool hasItems) =>
		isLoading || hasItems ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

	public static Microsoft.UI.Xaml.Visibility LoadedVisibility(bool isLoading) =>
		isLoading ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

	private void NextUpInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
	{
		ViewModel.NextUpCommand.Execute(null);
    }

	private void ContinueInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
	{
		ViewModel.ContinueCommand.Execute(null);
    }
}
