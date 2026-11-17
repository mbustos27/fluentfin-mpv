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
		AppWindow.SetIcon("Assets/Seaglass.ico");

		App.GetKeyedService<INavigationService>(NavigationRegions.InitialSetup).Frame = RootFrame;
	}
}
