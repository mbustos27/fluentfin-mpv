using Microsoft.UI.Xaml;

namespace CompositionSpike;

/// <summary>The spike's application: one window, one scenario (<see cref="Host"/>), then it ends itself.</summary>
public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		UnhandledException += (_, e) =>
		{
			Host.Log($"UNHANDLED {e.Exception}");
			e.Handled = true;
		};
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
		Host.Start(new Window { Title = "FluentFin composition spike" });
	}
}
