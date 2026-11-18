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

		// 1.1.4 (states): asks before a saved server goes (Cancel is the default)
		ViewModel.Confirm = prompt => Dialogs.ConfirmDialog.AskAsync(XamlRoot, prompt);

		// fluentfin-mpv: with no saved server, look for one on this network straight away (docs/research/ux-review.md F2)
		// 1.1.4 (states): the keyboard starts in the address box (the saved servers are one Shift+Tab away)
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

	/// <summary>1.2.0 (the button review, S9 and S11): a row's Remove, named after the server ("Remove Living room").</summary>
	public static string RemoveName(string displayName) =>
		string.IsNullOrWhiteSpace(displayName) ? "Remove" : $"Remove {displayName.Trim()}";

	/// <summary>The More options button of a saved server's row, named after it ("More options for Living room").</summary>
	public static string MoreOptionsName(string displayName) =>
		string.IsNullOrWhiteSpace(displayName) ? "More options" : $"More options for {displayName.Trim()}";

	/// <summary>
	/// Remove on a row (its More options menu, or its context menu). The row that had the keyboard is gone with the server:
	/// the keyboard goes to the row now in its place (or the last one), else to the address box.
	/// </summary>
	private async void RemoveServer_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (sender is not Microsoft.UI.Xaml.FrameworkElement { Tag: SavedServer server })
		{
			return;
		}

		var index = ViewModel.Servers.IndexOf(server);
		await ViewModel.RemoveServerCommand.ExecuteAsync(server);
		if (ViewModel.Servers.Contains(server))
		{
			return; // Cancel: the dialog gives the keyboard back to the More options button
		}

		var next = SelectServerViewModel.FocusAfterRemove(index, ViewModel.Servers.Count);
		// after the list has laid itself out again without the row
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (next is int row && SavedServersView.ScrollView?.Content is ItemsRepeater repeater && repeater.TryGetElement(row) is Microsoft.UI.Xaml.Controls.Control container)
			{
				container.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
			}
			else
			{
				TxtUrl.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
			}
		});
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
