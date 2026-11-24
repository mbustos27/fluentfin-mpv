using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
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

	/// <summary>
	/// 174-sessions (BS-M3): how long the start's sign-in may take before it is given up on and the server list says why. An
	/// address that doesn't answer showed "Signing in to …" for 22 s (Windows' own connection timeout), then nothing.
	/// </summary>
	internal TimeSpan SignInTimeout { get; set; } = TimeSpan.FromSeconds(10);

	protected async override Task HandleInternalAsync(LaunchActivatedEventArgs args)
	{
		_pluginManager.LoadOptions(_localSettingsService);

		// 174-sessions (BS-M1, BS-M2): the sign-in made last, when it is still saved (it was the one server's one saved user, so
		// a second user or server stopped it, and Sign out's saved password signed in again at the next start)
		if (SavedSignIns.ForStart(_settings.Servers) is ({ } server, { } user))
		{
			// fluentfin-mpv (startup-perf, O4): the window is already shown (ActivationService); it says what it waits for
			var name = LoginViewModel.ServerNameOf(server);
			_mainWindow.StartupStatus = $"Signing in to {(string.IsNullOrWhiteSpace(server.DisplayName) ? "the server" : server.DisplayName)}…"; // 1.7.3 (rule 11): it said "your server"
			bool result;
			string? problem = null;
			using var bound = new CancellationTokenSource(SignInTimeout);
			try
			{
				result = await _jellyfinAuthenticationService.Authenticate(server, user, bound.Token);
				problem = _jellyfinAuthenticationService.SavedSignInProblem;
			}
			catch (Exception ex)
			{
				// 174-sessions: no sign-in, and the server list says so (it ended at "Startup failed")
				_logger.LogError(ex, "The sign-in at start failed");
				result = false;
				problem = $"Couldn't sign in to {(string.IsNullOrEmpty(name) ? "the server" : name)}. Try again; if it keeps happening, the app's log has the details.";
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
			else if (_jellyfinAuthenticationService.SavedSignInRefused)
			{
				// 174-sessions (BS-M3, BS-M4): refused (its saved password or token forgotten): the server's sign-in page, with the
				// name and the line, so the password is asked once
				_navigationService.NavigateTo<LoginViewModel>(new SetupNotice(problem ?? JellyfinAuthenticationService.NoLongerWorks(server), server, user.Username));
			}
			else
			{
				// 174-signin: a saved sign-in the server refused is said on the server list (it opened without a word); 174-sessions
				// (BS-M3): so is every other reason (no answer, an error), kept for the next start
				_navigationService.NavigateTo<SelectServerViewModel>(problem is { } said ? new SetupNotice(said) : null);
			}
		}
		else
		{
			_navigationService.NavigateTo<SelectServerViewModel>();
		}
	}
}
