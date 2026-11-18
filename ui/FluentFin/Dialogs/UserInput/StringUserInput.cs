using FluentFin.Core.Contracts.Services;
using FluentFin.Dialogs.ViewModels;
using FluentFin.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FluentFin.Dialogs.UserInput;

public abstract partial class StringUserInput(IContentDialogService dialogService,
									          IServiceProvider serviceProvider) : IUserInput<string>
{
	public virtual string? Message { get; }
	public virtual string? Title { get; }
	public virtual string? PlaceholderText { get; }

	/// <summary>Nixie 1.2.0 (admin, D79): the dialog's primary says what it does ("Rename", "Send"), never "OK".</summary>
	public abstract string ConfirmText { get; }

	public async Task<string?> GetValue()
	{
		var vm = serviceProvider.GetRequiredService<StringPickerViewModel>();
		vm.Message = Message;
		vm.PlaceholderText = PlaceholderText;
		vm.Title = Title;
		vm.ConfirmText = ConfirmText;

		var result = await dialogService.ShowDialog(vm, null!);

		if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
		{
			return vm.Name;
		}

		return null;
	}
}

public sealed class LibraryNewNameInput(IContentDialogService dialogService, IServiceProvider serviceProvider) : StringUserInput(dialogService, serviceProvider)
{
	public override string? Message => "Renaming a library loses all of its metadata.";
	public override string? Title => "Rename library";
	public override string? PlaceholderText => "New name";
	public override string ConfirmText => "Rename";
}

public sealed class SendMessageToSessionInput(IContentDialogService dialogService, IServiceProvider serviceProvider) : StringUserInput(dialogService, serviceProvider)
{
	public override string? Title => "Send a message";
	public override string? PlaceholderText => "Message";
	public override string ConfirmText => "Send";
}
