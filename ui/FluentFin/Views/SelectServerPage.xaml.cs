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
		// 1.1.4 (states): the keyboard starts in the address box (the saved servers are one Shift+Tab away)
		// asks before a saved server goes (Cancel is the default)
		ViewModel.Confirm = prompt => Dialogs.ConfirmDialog.AskAsync(XamlRoot, prompt);
		Loaded += async (_, _) =>
		{
			TxtUrl.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
			await ViewModel.OnShown();
		};
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

	/// <summary>1.1.4 (states): a saved server's address under its name (the address this PC uses first).</summary>
	public static string AddressOf(string localUrl, string publicUrl) =>
		!string.IsNullOrWhiteSpace(localUrl) ? localUrl.Trim() : publicUrl?.Trim() ?? "";

	/// <summary>The More options button of a saved server's row, named after it ("More options for Living room").</summary>
	public static string MoreOptionsName(string displayName) =>
		string.IsNullOrWhiteSpace(displayName) ? "More options" : $"More options for {displayName.Trim()}";

	private async void RemoveServer_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (sender is Microsoft.UI.Xaml.FrameworkElement { Tag: SavedServer server })
		{
			await ViewModel.RemoveServerCommand.ExecuteAsync(server);
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
