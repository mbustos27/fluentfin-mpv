using Microsoft.UI.Xaml;

namespace FluentFin;

/// <summary>The harness's application: one window, one scenario (<see cref="FluentFinControlsHost.Host"/>), then it ends itself.</summary>
public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		UnhandledException += (_, e) =>
		{
			FluentFinControlsHost.Host.Log($"UNHANDLED {e.Exception}");
			e.Handled = true;
		};
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
		FluentFinControlsHost.Host.Start(new Window { Title = "FluentFin player controls host" });
	}
}
