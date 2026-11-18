using FluentFin.Dialogs.ViewModels;
using ReactiveUI;

namespace FluentFin.Dialogs.Views;

public sealed partial class EditSubtitlesDialog : IViewFor<EditSubtitlesViewModel>
{
	private EditSubtitlesViewModel? _viewModel;

	public EditSubtitlesViewModel? ViewModel
	{
		get => _viewModel;
		set
		{
			_viewModel = value;
			if (value is not null)
			{
				// Nixie 1.2.0 (admin, D62): Delete subtitle asks in a flyout at the button (a second dialog cannot open over this one)
				value.Confirm = prompt => ConfirmFlyout.AskAsync(ConfirmFlyout.AnchorIn(this), prompt);
			}
		}
	}

	object? IViewFor.ViewModel { get => ViewModel; set => ViewModel = (EditSubtitlesViewModel?)value; }

	public EditSubtitlesDialog()
	{
		ViewModel = App.GetService<EditSubtitlesViewModel>();
		InitializeComponent();
	}

	public static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

	public static string DeleteName(string? subtitle) => string.IsNullOrWhiteSpace(subtitle) ? "Delete subtitle" : $"Delete subtitle {subtitle}";

	public static string DownloadName(string? name, string? language, string? provider)
	{
		var details = string.Join(", ", new[] { language, provider }.Where(x => !string.IsNullOrWhiteSpace(x)));
		return $"Download {name}{(details.Length > 0 ? $" ({details})" : "")}";
	}
}
