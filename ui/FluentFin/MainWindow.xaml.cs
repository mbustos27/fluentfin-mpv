using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.ViewModels;

namespace FluentFin;

public sealed partial class MainWindow : WindowEx
{
	public IMainWindowViewModel ViewModel { get; } = App.GetService<IMainWindowViewModel>();

	public MainWindow()
	{
		InitializeComponent();
		ExtendsContentIntoTitleBar = true;
		AppWindow.SetIcon("Assets/jellyfin.ico");

		App.GetKeyedService<INavigationService>(NavigationRegions.InitialSetup).Frame = RootFrame;
	}

	/// <summary>fluentfin-mpv (startup-perf): x:Bind helpers for <see cref="IMainWindowViewModel.StartupStatus"/>.</summary>
	public static bool IsSet(string? text) => !string.IsNullOrWhiteSpace(text);

	public static Microsoft.UI.Xaml.Visibility VisibleWhenSet(string? text) => IsSet(text) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}
