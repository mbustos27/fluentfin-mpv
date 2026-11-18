using FluentFin.Dialogs.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using ReactiveUI;

namespace FluentFin.Dialogs.Views;

public sealed partial class EditImagesDialog : IViewFor<EditImagesViewModel>
{
	private EditImagesViewModel? _viewModel;

	public EditImagesViewModel? ViewModel
	{
		get => _viewModel;
		set
		{
			_viewModel = value;
			if (value is not null)
			{
				// Nixie 1.2.0 (admin, D53): Delete image asks in a flyout at the button (a second dialog cannot open over this one)
				value.Confirm = prompt => ConfirmFlyout.AskAsync(ConfirmFlyout.AnchorIn(this), prompt);
			}
		}
	}

	object? IViewFor.ViewModel { get => ViewModel; set => ViewModel = (EditImagesViewModel?)value; }

	public EditImagesDialog()
	{
		ViewModel = App.GetService<EditImagesViewModel>();
		InitializeComponent();
	}

	public static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

	public static string SearchName(ImageInfo_ImageType? type) => $"Search for a replacement {Kind(type)} image";

	public static string DeleteName(ImageInfo_ImageType? type) => $"Delete {Kind(type)} image";

	private static string Kind(ImageInfo_ImageType? type) => type?.ToString().ToLowerInvariant() ?? "this";
}
