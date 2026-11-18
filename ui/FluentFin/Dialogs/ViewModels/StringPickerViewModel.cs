using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ReactiveUI;

namespace FluentFin.Dialogs.ViewModels;

public partial class StringPickerViewModel : ObservableObject
{
	public StringPickerViewModel()
	{
		this.WhenAnyValue(x => x.Message)
			.Select(string.IsNullOrEmpty)
			.Subscribe(isEmpty => HasInfo = !isEmpty);
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanConfirm))]
	public partial string? Name { get; set; }

	/// <summary>Nixie 1.2.0 (admin, D79): what the primary button does ("Rename", "Send").</summary>
	[ObservableProperty]
	public partial string ConfirmText { get; set; } = "OK";

	/// <summary>The primary waits for some text.</summary>
	public bool CanConfirm => !string.IsNullOrWhiteSpace(Name);

	[ObservableProperty]
	public partial string? PlaceholderText { get; set; }

	[ObservableProperty]
	public partial string? Message { get; set; }

	[ObservableProperty]
	public partial string? Title { get; set; }

	[ObservableProperty]
	public partial bool HasInfo { get; set; }
}
