using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.ViewModels;

namespace FluentFin.ViewModels;

public partial class MainViewModel(ITitleBarViewModel titleBarViewModel) : ObservableObject, IMainWindowViewModel
{
	public ITitleBarViewModel TitleBarViewModel { get; } = titleBarViewModel;

	/// <inheritdoc/>
	// fluentfin-mpv (startup-perf): the automatic sign-in shown in the window (O4)
	[ObservableProperty]
	public partial string? StartupStatus { get; set; }
}
