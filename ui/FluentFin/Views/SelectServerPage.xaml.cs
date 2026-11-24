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
			SavedServersView.ItemsSource ??= ViewModel.Servers; // 174-signin: back on a page that was shown before (Unloaded below)
			TxtUrl.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
			SayOpeningLine();
			await ViewModel.OnShown();
		};

		// 174-signin: the list lets go of the saved servers when the page goes. The collection is the settings' and outlives
		// the page: a server page left for the sign-in page kept listening, and Remove on the next one threw in that old
		// list's handler (COMException, "Unspecified error") after the file was saved and before this page's list heard of
		// it, so the removed server stayed on the list.
		Unloaded += (_, _) => SavedServersView.ItemsSource = null;
	}

	/// <summary>
	/// 174-sessions (BS-M3): the line the page opens with (why the start didn't sign in) is said to Narrator too: an InfoBar
	/// open from the start announces nothing, and the keyboard is in the address box.
	/// </summary>
	private void SayOpeningLine()
	{
		if (!ViewModel.HasError)
		{
			return;
		}

		var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(ErrorBar) ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(ErrorBar);
		peer?.RaiseNotificationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.Other,
			Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent, ViewModel.ErrorMessage, "NixieServerPageLine");
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

	/// <summary>
	/// 174-signin (a friend's report): a press on a row's More options opens its menu and nothing else. The button takes
	/// the press as its click, but not the tap that comes with it, and the row (ItemContainer) invokes on a tap: the
	/// server's sign-in opened over the menu, so Remove could not be pressed. A double click the same.
	/// </summary>
	private void MoreOptions_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => e.Handled = true;

	private void MoreOptions_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => e.Handled = true;

	private void ServerSelected(ItemsView sender, ItemsViewItemInvokedEventArgs args)
	{
		if (args.InvokedItem is not SavedServer server)
		{
			return;
		}

		ViewModel.CheckConnectivityAndGoToLoginCommand.Execute(server.GetServerUrl());
	}

}
