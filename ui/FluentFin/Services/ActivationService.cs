using FluentFin.Activation;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Views;
using Microsoft.Extensions.Logging;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Services;

public class ActivationService : IActivationService
{
	private readonly ActivationHandler<LaunchActivatedEventArgs> _defaultHandler;
	private readonly IEnumerable<IActivationHandler> _activationHandlers;
	private readonly ILogger<ActivationService> _logger;
	private readonly Action _showWindow;
	private readonly Guid _placementGuid = Guid.Parse("245b5fc3-a858-4106-8dc9-27de8e60e279");
	private static UIElement? s_shell = null;

	public ActivationService(ActivationHandler<LaunchActivatedEventArgs> defaultHandler, IEnumerable<IActivationHandler> activationHandlers, ILogger<ActivationService> logger)
		: this(defaultHandler, activationHandlers, logger, ShowMainWindow)
	{
	}

	/// <summary>Tests: <paramref name="showWindow"/> stands for preparing, maximizing and activating the main window.</summary>
	internal ActivationService(ActivationHandler<LaunchActivatedEventArgs> defaultHandler, IEnumerable<IActivationHandler> activationHandlers, ILogger<ActivationService> logger, Action showWindow)
	{
		_defaultHandler = defaultHandler;
		_activationHandlers = activationHandlers;
		_logger = logger;
		_showWindow = showWindow;
	}

	public async Task ActivateAsync(object activationArgs)
	{
		// Execute tasks before activation.
		await InitializeAsync();

		// fluentfin-mpv (startup-perf, docs/research/perf-ui.md O4): the window first. The activation handlers may sign in
		// to the saved server, which takes several round trips (21 s to give up on an unreachable server); the window says
		// so meanwhile (DefaultActivationHandler) instead of not being there at all.
		_showWindow();
		PerfLog.Startup(_logger, "window shown");

		// Handle activation via ActivationHandlers.
		await HandleActivationAsync(activationArgs);

		// fluentfin-mpv (startup-perf, O13): the Windows player's codec probe runs when that player needs its profile
		// (DeviceProfileFactory), not after every activation
		await StartupAsync();
	}

	private static void ShowMainWindow()
	{
		// Set the MainWindow Content.
		if (App.MainWindow.Content == null)
		{
			s_shell = App.GetService<ShellPage>();
			App.MainWindow.Content = s_shell ?? new Frame();
		}

		// Activate the MainWindow.
		App.MainWindow.Maximize();
		//App.MainWindow.AppWindow.EnablePlacementPersistence(_placementGuid, true, App.MainWindow.AppWindow.Id, Microsoft.UI.Windowing.PlacementPersistenceBehaviorFlags.OpenOverLastOpenedWindow | Microsoft.UI.Windowing.PlacementPersistenceBehaviorFlags.AllowLaunchIntoMaximized);
		App.MainWindow.Activate();
	}

	private async Task HandleActivationAsync(object activationArgs)
	{
		var activationHandler = _activationHandlers.FirstOrDefault(h => h.CanHandle(activationArgs));

		if (activationHandler != null)
		{
			await activationHandler.HandleAsync(activationArgs);
		}

		if (_defaultHandler.CanHandle(activationArgs))
		{
			await _defaultHandler.HandleAsync(activationArgs);
		}
	}

	private async Task InitializeAsync()
	{
		await Task.CompletedTask;
	}

	private async Task StartupAsync()
	{
		await Task.CompletedTask;
	}
}
