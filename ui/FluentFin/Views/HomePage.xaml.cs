using FluentFin.Core.Services;
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
		if (FluentFin.Services.TextInput.IsTextInput(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)))
		{
			return; // typing: N and C are letters then
		}

		args.Handled = true;
		ViewModel.NextUpCommand.Execute(null);
    }

	private void ContinueInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FluentFin.Services.TextInput.IsTextInput(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)))
		{
			return;
		}

		args.Handled = true;
		ViewModel.ContinueCommand.Execute(null);
    }

	// Seaglass scrolling: "See all" on a row. Recently Added opens its library; Continue Watching and Next Up, which have no
	// library of their own, open their items as a grid.
	private void ContinueSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Continue Watching", [.. ViewModel.ContinueItems]));

	private void NextUpSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Next Up", [.. ViewModel.NextUpItems]));

	private void RecentSeeAll(object sender, EventArgs e)
	{
		if (sender is FluentFin.Controls.ShelfRow { Tag: { } library })
		{
			App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<LibraryViewModel>(library);
		}
	}
}
