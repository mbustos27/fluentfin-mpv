using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.UI.Core;
using FluentFin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace FluentFin.Activation;

public class DefaultActivationHandler : ActivationHandler<LaunchActivatedEventArgs>
{
	private readonly INavigationService _navigationService;
	private readonly INavigationService _mainNavigationService;
	private readonly ISettings _settings;
	private readonly IJellyfinAuthenticationService _jellyfinAuthenticationService;
	private readonly ILocalSettingsService _localSettingsService;
	private readonly IPluginManager _pluginManager;
	private readonly IMainWindowViewModel _mainWindow;
	private readonly ILogger<DefaultActivationHandler> _logger;

	public DefaultActivationHandler([FromKeyedServices(NavigationRegions.InitialSetup)] INavigationService navigationService,
									INavigationService mainNavigationService,
									ISettings settings,
									ILocalSettingsService localSettingsService,
									IPluginManager pluginManager,
									IJellyfinAuthenticationService jellyfinAuthenticationService,
									IMainWindowViewModel mainWindow,
									ILogger<DefaultActivationHandler> logger)
	{
		_navigationService = navigationService;
		_mainNavigationService = mainNavigationService;
		_settings = settings;
		_jellyfinAuthenticationService = jellyfinAuthenticationService;
		_localSettingsService = localSettingsService;
		_pluginManager = pluginManager;
		_mainWindow = mainWindow;
		_logger = logger;
		_settings.ListenToChanges();
	}

	protected override bool CanHandleInternal(LaunchActivatedEventArgs args)
	{
		// None of the ActivationHandlers has handled the activation.
		return _navigationService.Frame?.Content == null;
	}

	protected async override Task HandleInternalAsync(LaunchActivatedEventArgs args)
	{
		_pluginManager.LoadOptions(_localSettingsService);

		if (_settings.Servers.Count == 1 && _settings.Servers[0].Users.Count == 1)
		{
			// fluentfin-mpv (startup-perf, O4): the window is already shown (ActivationService); it says what it waits for
			var server = _settings.Servers[0];
			_mainWindow.StartupStatus = $"Signing in to {(string.IsNullOrWhiteSpace(server.DisplayName) ? "the server" : server.DisplayName)}…"; // 1.7.3 (rule 11): it said "your server"
			bool result;
			try
			{
				result = await _jellyfinAuthenticationService.Authenticate(server, server.Users[0]);
			}
			finally
			{
				_mainWindow.StartupStatus = null;
			}

			PerfLog.Startup(_logger, result ? "signed in" : "sign-in failed");
			if (result)
			{
				_navigationService.NavigateTo<ShellViewModel>();

				// fluentfin-mpv: a library id from the jump list, also next to --data-dir <folder>
				var cmdArgs = KnownFolders.WithoutDataDir(Environment.GetCommandLineArgs().Skip(1));
				if(cmdArgs.Length == 1 && Guid.TryParse(cmdArgs[0], out var libraryId))
				{
					_mainNavigationService.NavigateTo<LibraryViewModel>(libraryId);
				}
			}
			else
			{
				_navigationService.NavigateTo<SelectServerViewModel>();
			}
		}
		else
		{
			_navigationService.NavigateTo<SelectServerViewModel>();
		}
	}
}
