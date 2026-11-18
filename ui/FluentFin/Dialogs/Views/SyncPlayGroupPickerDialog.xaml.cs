using FluentFin.Dialogs.ViewModels;
using ReactiveUI;

namespace FluentFin.Dialogs.Views;

public sealed partial class SyncPlayGroupPickerDialog : IViewFor<SyncPlayGroupPickerViewModel>
{
	public SyncPlayGroupPickerViewModel? ViewModel { get; set; } = App.GetService<SyncPlayGroupPickerViewModel>();

	object? IViewFor.ViewModel { get => ViewModel; set => ViewModel = (SyncPlayGroupPickerViewModel?)value; }

	public SyncPlayGroupPickerDialog()
	{
		InitializeComponent();

		// Seaglass 1.1.4 (review): "New group" closes the dialog only when the group started; a refusal stays, said in it
		SecondaryButtonClick += async (_, e) =>
		{
			e.Cancel = true;
			if (ViewModel is { } vm && await vm.CreateGroup())
			{
				vm.CanClose = true;
				Hide();
			}
		};
	}
}
