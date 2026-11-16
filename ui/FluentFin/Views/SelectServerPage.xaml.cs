using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

public sealed partial class SelectServerPage : Page
{
	public SelectServerViewModel ViewModel { get; } = App.GetService<SelectServerViewModel>();

	public SelectServerPage()
	{
		InitializeComponent();

		// fluentfin-mpv: with no saved server, look for one on this network straight away (docs/research/ux-review.md F2)
		Loaded += async (_, _) => await ViewModel.OnShown();
	}

	private void InputFieldKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
	{
		if (sender is not TextBox tb)
		{
			return;
		}

		if (e.Key == Windows.System.VirtualKey.Enter)
		{
			ViewModel.CheckConnectivityAndGoToLoginCommand.Execute(tb.Text);
		}
	}

	private void ServerSelected(ItemsView sender, ItemsViewItemInvokedEventArgs args)
	{
		if (args.InvokedItem is not SavedServer server)
		{
			return;
		}

		ViewModel.CheckConnectivityAndGoToLoginCommand.Execute(server.GetServerUrl());
	}

}
