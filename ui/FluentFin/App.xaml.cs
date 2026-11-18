using System.Reactive.Subjects;
using System.Reflection;
using FluentFin.Activation;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.WebSockets;
using FluentFin.Dialogs;
using FluentFin.Dialogs.UserInput;
using FluentFin.Dialogs.ViewModels;
using FluentFin.Dialogs.Views;
using FluentFin.Helpers;
using FluentFin.Core.Mpv;
using FluentFin.Mpv;
using FluentFin.Plugins.Playback_Reporting;
using FluentFin.Services;
using FluentFin.UI.Core;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.ViewModels;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReactiveUI.Builder;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Serilog;
using Windows.ApplicationModel;

namespace FluentFin;

public partial class App : Application
{
	public IHost Host { get; private set; } = null!;

	private bool _isClosing;

	public static T GetService<T>()
		where T : class
	{
		if ((Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
		{
			throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
		}

		return service;
	}

	public static T GetKeyedService<T>(object key)
	where T : class
	{
		if ((Current as App)!.Host.Services.GetKeyedService<T>(key) is not T service)
		{
			throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
		}

		return service;
	}

	public static WindowEx MainWindow { get; } = new MainWindow();

	public static GlobalCommands Commands { get; } = GetService<GlobalCommands>();

	public static DialogCommands Dialogs { get; } = GetService<DialogCommands>();


	public App()
	{
		// Seaglass: a crash here comes before the log exists and WinUI ends the process without a word; leave a note in
		// %TEMP%\Nixie-startup-crash.txt (StartupCrashFile) so it can be told what happened
		try
		{
			Construct();
		}
		catch (Exception ex)
		{
			WriteStartupCrash(ex);
			throw;
		}
	}

	/// <summary>Nixie: the title of the error box a failed start shows.</summary>
	internal const string CouldNotStart = Core.AppIdentity.Name + " could not start";

	/// <summary>Nixie: the start-up crash note's name in %TEMP% (it was Seaglass-startup-crash.txt).</summary>
	internal static string StartupCrashFile => $"{Core.AppIdentity.Name}-startup-crash.txt";

	internal static void WriteStartupCrash(Exception ex)
	{
		try
		{
			File.WriteAllText(Path.Combine(Path.GetTempPath(), StartupCrashFile), $"{DateTime.Now:O}{Environment.NewLine}{ex}");
		}
		catch (Exception)
		{
			// nowhere to say it
		}
	}

	private void Construct()
	{
		// ReactiveUI 23+ must be initialized explicitly before any Rx/ReactiveUI type is touched
		RxAppBuilder.CreateReactiveUIBuilder().WithWinUI().BuildApp();
		InitializeComponent();
		// Seaglass: NIXIE_THEME (SEAGLASS_THEME before 1.2.0; harness screenshots) for the popups too; settable only here, in the constructor
		if (GlassTheme.ForcedApplicationTheme(Core.AppIdentity.ReadEnvironment("THEME")) is { } forced)
		{
			RequestedTheme = forced;
		}

		// fluentfin-mpv: --data-dir <folder> / FLUENTFIN_MPV_DATA_DIR put everything this run writes there (dev and test runs)
		// Nixie: the data folder was %LOCALAPPDATA%\Seaglass (and before that %LOCALAPPDATA%\FluentFinMpv); the newest one is
		// copied (never moved) to %LOCALAPPDATA%\Nixie on the first start. A failed copy leaves no Nixie folder: this run uses the
		// old folder in place, the copy is tried again at the next start, and the user is told once the window is up (_legacyCopyFailed)
		var legacyCopy = Core.KnownFolders.CopyLegacyForStartup([.. Environment.GetCommandLineArgs().Skip(1)], Environment.GetEnvironmentVariable);
		var migrationNote = legacyCopy.Note;
		_legacyCopyFailed = legacyCopy.Failed ? legacyCopy.LegacyApp ?? "the old version" : null;
		var knownFolders = StartupFolders(legacyCopy.Failed ? legacyCopy.LegacyRoot : null);

		Host = Microsoft.Extensions.Hosting.Host
		.CreateDefaultBuilder()
		.UseContentRoot(AppContext.BaseDirectory)
		.ConfigureServices((context, services) =>
		{
			Log.Logger = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.File(knownFolders.LogFile, Serilog.Events.LogEventLevel.Debug, rollingInterval: RollingInterval.Day)
				.CreateLogger();
			if (migrationNote is not null)
			{
				Log.Information("{Note}", migrationNote);
			}
			services.AddLogging(builder => builder.AddSerilog());

			// Default Activation Handler
			services.AddTransient<ActivationHandler<LaunchActivatedEventArgs>, DefaultActivationHandler>();

			// Other Activation Handlers
			services.AddTransient<IActivationHandler, MpvSelfTestActivationHandler>();

			// mpv player: the jellyfin-mpv-shim backend as a child process, drawing into a window of ours (docs/BRIDGE.md)
			services.AddSingleton<MpvVideoHost>();
			services.AddSingleton<IMpvBackendLauncher, WindowsMpvBackendLauncher>();
			// fluentfin-mpv player controls: or into an mpv inside FluentFin, attached to by the backend (Settings, Player controls)
			services.AddSingleton(sp => new MpvCompositionVideo(sp.GetRequiredService<ILogger<MpvCompositionVideo>>(),
				(sp.GetRequiredService<IMpvBackendLauncher>() as WindowsMpvBackendLauncher)?.ConfigDirectory));
			services.AddSingleton<IMpvVideoSurface, MpvVideoSurfaceChoice>();
			services.AddSingleton<IMpvBackend, MpvBackendHost>();
			services.AddSingleton<MpvPlaybackService>();
			services.AddSingleton<MpvPrewarm>();
			// fluentfin-mpv: the nav pane and search hide while a player page is open (docs/research/ux-review.md V9)
			services.AddSingleton(_ => new PlayerChrome(action => MainWindow.DispatcherQueue.TryEnqueue(() => action())));

			// Services
			services.AddSingleton<IActivationService, ActivationService>();
			services.AddSingleton<IPageService, PageService>();
			services.AddSingleton<INavigationViewService, NavigationViewService>();
			// 1.2.0 (the top bar): the shell's sections are tabs; the plugins' sections and the pins go there (no rail)
			services.AddSingleton<TopNavigation>();
			services.AddSingleton<INavigationViewServiceCore>(sp => sp.GetRequiredService<TopNavigation>());
			services.AddSingleton<INavigationService, NavigationService>();
			services.AddSingleton((Func<IServiceProvider, INavigationServiceCore>)(sp => sp.GetRequiredService<INavigationService>()));
			services.AddTransient<IContentDialogService, ContentDialogService>();
			services.AddTransient<IContentDialogServiceCore>(sp => sp.GetRequiredService<IContentDialogService>());
			services.AddSingleton<IDeviceProfileFactory, DeviceProfileFactory>();
			// fluentfin-mpv: a run with its own data folder leaves the taskbar's jump list (shell state outside it) alone
			if (knownFolders.IsCustom)
			{
				services.AddSingleton<IJumpListService, NoJumpListService>();
			}
			else
			{
				services.AddSingleton<IJumpListService, JumpListService>();
			}

			services.AddNavigationViewNavigation(NavigationRegions.Settings);
			services.AddFrameNavigation(NavigationRegions.UserEditor);
			services.AddFrameNavigation(NavigationRegions.InitialSetup);
			services.AddNavigationViewNavigation(NavigationRegions.PlaybackReporting);

			// Core Services
			services.AddTransient<LoginViewModel>();
			services.AddSingleton<ILocalSettingsService, LocalSettingsService>();
			services.AddSingleton<IJellyfinAuthenticationService, JellyfinAuthenticationService>();
			services.AddSingleton<IJellyfinClient, JellyfinClient>();
			services.AddSingleton<ISettings, Settings>();
			services.AddSingleton(knownFolders);
			services.AddSingleton<ITaskBarProgress, TaskBarProgress>();
			services.AddSingleton<Subject<IOutboundWebSocketMessage>>();
			services.AddSingleton<IObservable<IOutboundWebSocketMessage>>(sp => sp.GetRequiredService<Subject<IOutboundWebSocketMessage>>());
			services.AddSingleton<IObserver<IOutboundWebSocketMessage>>(sp => sp.GetRequiredService<Subject<IOutboundWebSocketMessage>>());


			// Commands
			services.AddSingleton<GlobalCommands>();
			services.AddSingleton<DialogCommands>();

			services.AddSingleton<HomeCache>(); // fluentfin-mpv: the last home page, shown at once (HomeViewModel)
			services.AddSingleton<SeriesEpisodes>(); // 1.2.0: a series' episodes for Play's label, kept (series and season pages)

			// Views and ViewModels
			services.AddTransient<ShellViewModel>();
			services.AddSingleton<IMainWindowViewModel, MainViewModel>();
			services.AddSingleton<ITitleBarViewModel, TitleBarViewModel>();
			services.AddTransient<HomeViewModel>();
			services.AddTransient<LibraryViewModel>();
			services.AddTransient<ItemGridViewModel>();
			services.AddTransient<VideoPlayerViewModel>();
			services.AddTransient<MovieViewModel>();
			services.AddTransient<EpisodeViewModel>();
			services.AddTransient<SeriesViewModel>();
			services.AddTransient<SeasonViewModel>();
			services.AddTransient<JellyfinSettingsViewModel>();
			services.AddTransient<SettingsViewModel>();
			services.AddTransient<TrickplayViewModel>();
			services.AddTransient<SelectServerViewModel>();
			services.AddTransient<MediaSegmentsEditorViewModel>();

			// dashboard view models
			services.AddTransient<DashboardViewModel>();
			services.AddTransient<SearchResultsViewModel>(); // Seaglass 1.1.4: the search results page
			services.AddTransient<GeneralSettingsViewModel>();
			services.AddTransient<UsersViewModel>();
			services.AddTransient<UserEditorViewModel>();
			services.AddTransient<UserProfileEditorViewModel>();
			services.AddTransient<UserAccessEditorViewModel>();
			services.AddTransient<UserParentalControlEditorViewModel>();
			services.AddTransient<UserPasswordEditorViewModel>();
			services.AddTransient<LibrariesSettingsViewModel>();
			services.AddTransient<LibrariesMetadataViewModel>();
			services.AddTransient<LibrariesNfoSettingsViewModel>();
			services.AddTransient<LibrariesDisplayViewModel>();
			services.AddTransient<PlaybackResumeViewModel>();
			services.AddTransient<PlaybackTrickplayViewModel>();
			services.AddTransient<PlaybackTranscodingViewModel>();
			services.AddTransient<ActivitiesViewModel>();
			services.AddTransient<LibrariesLandingPageViewModel>();
			services.AddTransient<ScheduledTasksViewModel>();

			// Dialogs
			services.AddDialog<EditMetadataViewModel, EditMetadataDialog>();
			services.AddDialog<EditImagesViewModel, EditImagesDialog>();
			services.AddDialog<EditSubtitlesViewModel, EditSubtitlesDialog>();
			services.AddDialog<IdentifyViewModel, IdentifyDialog>();
			services.AddDialog<MediaInfoViewModel, MediaInfoDialog>();
			services.AddDialog<RefreshMetadataViewModel, RefreshMetadataDialog>();
			services.AddDialog<AccessSchedulePickerViewModel, AccessSchedulePickerDialog>();
			services.AddDialog<AddUserViewModel, AddUserDialog>();
			services.AddDialog<ManageLibraryViewModel, ManageLibraryDialog>();
			services.AddDialog<StringPickerViewModel, StringPickerDialog>();
			services.AddDialog<QuickConnectViewModel, QuickConnectDialog>();
			services.AddDialog<SessionPickerViewModel, SessionPickerDialog>();
			services.AddDialog<SyncPlayGroupPickerViewModel, SyncPlayGroupPickerDialog>();

			// Pickers
			services.AddTransient<IUserInput<AccessSchedule>, AccessScheduleUserInput>();
			services.AddKeyedTransient<IUserInput<string>, LibraryNewNameInput>(UserInputs.LibraryNewName);
			services.AddKeyedTransient<IUserInput<string>, SendMessageToSessionInput>(UserInputs.MessageToSession);
			services.AddTransient<IServerFolderPicker, ServerFolderInput>();

			services.AddTransient<ShellPage>();

			services.AddTransient<IPluginManager, PluginManager>();
			Assembly[] assemblies = [typeof(PlaybackReportingPlugin).Assembly];
			foreach (var assembly in assemblies)
			{
				var plugins = assembly.GetTypes()
					.Where(t => t.IsAssignableTo(typeof(IPlugin)) && !t.IsAbstract);

				foreach (var type in plugins)
				{
					var plugin = (IPlugin)Activator.CreateInstance(type)!;
					services.AddSingleton(plugin);
					plugin.ConfigureServices(services);
				}
			}

			// Configuration
			// fluentfin-mpv: no WindowsUpdateService (deleted). It downloaded upstream FluentFin's latest installer from GitHub on
			// start and offered to run it, which would replace this build (and its mpv player) with upstream's. The app's own,
			// opt-in notice (Settings > Check for updates) only ever names this repository's releases: Core/Updates.
			services.AddSingleton<UpdateNoticeService>();
			services.AddHostedService(sp => sp.GetRequiredService<UpdateNoticeService>());
			services.AddHostedService<WebSocketMessageHandler>();
		}).
		Build();

		UnhandledException += App_UnhandledException;
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
	}

	// fluentfin-mpv: a --data-dir with no folder or a mistyped one stops the start (the default folder would take a test run's
	// data), and so does a data folder that cannot be made, with a message instead of a crash before any window
	// an instance field: setting a static one in the constructor ran App's static initializer (which creates MainWindow, which asks
	// for services) before the host existed, and every start crashed (1.1.0 verify)
	// Nixie: the app whose data folder could not be copied ("Seaglass", "FluentFin MPV"), null when nothing failed
	private string? _legacyCopyFailed;

	/// <summary>Nixie: the message shown once the window is up when the old data folder could not be copied (title, text).</summary>
	internal static (string Title, string Text) LegacyCopyFailedMessage(string oldApp) =>
		($"{Core.AppIdentity.Name} could not copy your data",
		 $"{Core.AppIdentity.Name} couldn't copy the data from {oldApp}, so it is using that folder in place for now. Nothing is lost; it will try the copy again the next time it starts.");

	private static Core.KnownFolders StartupFolders(string? useInstead)
	{
		if (Core.KnownFolders.TryForStartup([.. Environment.GetCommandLineArgs().Skip(1)], Environment.GetEnvironmentVariable, out var error,
			useInstead is null ? null : () => useInstead) is { } folders)
		{
			return folders;
		}

		NativeMethods.ShowError(CouldNotStart, error ?? "The data folder could not be used.");
		Environment.Exit(2);
		throw new InvalidOperationException(error);
	}

	private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
	{
		NativeMethods.AllowSleep();
		GetService<ILogger<App>>().LogError($"{e.ExceptionObject}");
	}

	private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
	{
		GetService<ILogger<App>>().LogError(e.Exception, "Unhandled exception");
		e.Handled = true;
	}

	protected async override void OnLaunched(LaunchActivatedEventArgs args)
	{
		try
		{
			PerfLog.Startup(GetService<ILogger<App>>(), "launched");
			await Host.StartAsync();

			Locator.SetServiceProvider(Host.Services);

			base.OnLaunched(args);

			MainWindow.AppWindow.Closing += AppWindow_Closing;

			// a hung mpv window would freeze our input too (the queues of the two processes are attached)
			GetService<MpvVideoHost>().Unresponsive += reason => GetService<IMpvBackend>().Kill(reason);

			// Seaglass: the saved Glass choice, and the glass brushes following GlassMode
			GlassSetting.Apply(GetService<ISettings>());
			// the harness's light-theme screenshots (NIXIE_THEME=Light|Dark, or SEAGLASS_THEME); unset, the app follows Windows
			if (Enum.TryParse<ElementTheme>(Core.AppIdentity.ReadEnvironment("THEME"), true, out var forcedTheme) && forcedTheme != ElementTheme.Default)
			{
				((FrameworkElement)MainWindow.Content).RequestedTheme = forcedTheme;
			}

			GlassTheme.Attach((FrameworkElement)MainWindow.Content);
			SeaglassTheme.Attach();
			TitleTint.Attach();

			// fluentfin-mpv (startup-perf, O5): no Flyleaf engine here; it starts with the first Flyleaf player (FlyleafEngine)
			await GetService<IActivationService>().ActivateAsync(args);

			if (_legacyCopyFailed is { } oldApp)
			{
				var (title, text) = LegacyCopyFailedMessage(oldApp);
				_ = GetService<IContentDialogService>().ShowMessage(title, text);
			}
		}
		catch (Exception ex)
		{
			// App_UnhandledException marks errors handled, so a failed launch would otherwise leave a process with no window
			GetService<ILogger<App>>().LogCritical(ex, "Startup failed");
			NativeMethods.ShowError(CouldNotStart, ex.Message);
			Exit();
		}
	}

	private static bool IsPackaged()
	{
		try
		{
			_ = Package.Current;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
	{
		if (_isClosing)
		{
			return;
		}

		// the process exits as soon as the window closes, so finish reporting first and close again afterwards
		args.Cancel = true;
		_isClosing = true;
		try
		{
			// fluentfin-mpv: gone for the user at once; the mpv backend may need ~10 s for its final report (docs/BRIDGE.md §3)
			sender.Hide();
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
		{
		}

		try
		{
			// fluentfin-mpv (startup-perf): no pre-warm of the mpv backend from now on; one in flight gives way to the close
			GetService<MpvPrewarm>().Cancel($"{Core.AppIdentity.Name} is closing");

			// fluentfin-mpv: upstream's stop report (bounded: a hung server must not keep a hidden FluentFin alive) and, side by
			// side, the mpv backend's shutdown and any crash report FluentFin is sending for it. Before the window goes, so the
			// backend's mpv window is gone before ours is destroyed.
			await CloseSequence.RunAsync(GetService<IJellyfinClient>(), GetService<MpvPlaybackService>(), GetService<ILogger<App>>());
			// fluentfin-mpv player controls: the in-process mpv after the backend that used it (bounded)
			GetService<MpvCompositionVideo>().Dispose();
			await Host.StopAsync();
		}
		finally
		{
			MainWindow.Close();
		}
	}
}
