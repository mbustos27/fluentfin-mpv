using CommunityToolkit.WinUI;
using FluentFin.Dialogs;
using FluentFin.ViewModels;
using Microsoft.UI.Xaml.Controls;


namespace FluentFin.Views;

public sealed partial class MediaSegmentsEditorPage : Page
{
	public MediaSegmentsEditorViewModel ViewModel { get; } = App.GetService<MediaSegmentsEditorViewModel>();

	public MediaSegmentsEditorPage()
	{
		InitializeComponent();
		// Nixie 1.2.0 (admin, D90): Delete segment asks here, Cancel the default
		ViewModel.Confirm = DestructiveConfirm.AskInMainWindow;
	}

	private void UpdateTime(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (sender is not Button b)
		{
			return;
		}

		if (b.FindAscendant<StackPanel>()?.Tag is not MediaSegmentViewModel vm)
		{
			return;
		}

		if (b.Tag is "Start")
		{
			vm.StartTicks = ViewModel.CurrentTimeTicks;
		}
		else
		{
			vm.EndTicks = ViewModel.CurrentTimeTicks;
		}
	}
}
