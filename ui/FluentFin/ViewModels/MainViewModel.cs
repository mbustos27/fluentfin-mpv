using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.ViewModels;

namespace FluentFin.ViewModels;

public partial class MainViewModel(ITitleBarViewModel titleBarViewModel) : ObservableObject, IMainWindowViewModel
{
	public ITitleBarViewModel TitleBarViewModel { get; } = titleBarViewModel;

	/// <inheritdoc/>
	// fluentfin-mpv (startup-perf): the automatic sign-in shown in the window (O4)
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasStartupStatus))]
	public partial string? StartupStatus { get; set; }

	/// <inheritdoc/>
	public bool HasStartupStatus => !string.IsNullOrWhiteSpace(StartupStatus);
}
