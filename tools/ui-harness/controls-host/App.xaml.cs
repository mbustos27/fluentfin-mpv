using Microsoft.UI.Xaml;

namespace FluentFin;

/// <summary>
/// The host's application. Named FluentFin.App with a static <see cref="MainWindow"/> because the linked video window
/// (ui\FluentFin\Mpv\MpvVideoHost.cs) follows FluentFin's App.MainWindow.
/// </summary>
public partial class App : Application
{
	public static Window MainWindow { get; private set; } = null!;

	public App()
	{
		InitializeComponent();
		UnhandledException += (_, e) =>
		{
			ControlsHost.Host.Log($"UNHANDLED {e.Exception}");
			e.Handled = true;
		};
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		// the scenario closes the main window and then still checks what is left: the host ends itself
		DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
		MainWindow = new Window { Title = "FluentFin controls host" };
		ControlsHost.Host.Start(MainWindow);
	}
}
