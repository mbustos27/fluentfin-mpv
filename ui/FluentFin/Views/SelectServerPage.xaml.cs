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
		// 1.1.4 (states): the harness's contrast-theme preview (SEAGLASS_THEME=HighContrast[:scheme]; nothing
		// otherwise). Here because this is the first page of every harness run; App.OnLaunched, after SeaglassTheme.Attach,
		// is where it belongs (another stream's file).
		Services.HighContrastPreview.ApplyFromEnvironment();
		InitializeComponent();

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

	private void ServerSelected(ItemsView sender, ItemsViewItemInvokedEventArgs args)
	{
		if (args.InvokedItem is not SavedServer server)
		{
			return;
		}

		ViewModel.CheckConnectivityAndGoToLoginCommand.Execute(server.GetServerUrl());
	}

}
