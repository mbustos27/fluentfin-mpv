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

	/// <summary>
	/// Nixie 1.7.3: the app is closing (its window hidden, then closed): what follows a page or the window going away (the
	/// shell's Unloaded, the caption buttons) has nothing to do. Set once, on the UI thread, when the close begins.
	/// </summary>
	public static bool IsClosing { get; private set; }

	// 1.7.3 (verify-package: a close from the player page took 3 to 20 s): when the close began, for the log's durations
	private static readonly System.Diagnostics.Stopwatch s_closing = new();
	// 1.8.1: this window's device slot, claimed at the start (an instance field: a static one touched in the constructor ran
	// App's static initializer early, whose MainWindow needs the host)
	private Task<(int? Slot, long Ms)>? _deviceSlot;

	public static T GetService<T>()
		where T : class
	{
		// Nixie 1.3.0: a theme switch hands the current page's view model to its new view (ViewModelHandoff)
		if (ViewModelHandoff.TryTake(typeof(T), out var handed) && handed is T same)
		{
			return same;
		}

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
		// 1.5.1 fixes (BH-150-M2): the device id Jellyfin sees is this copy's (its data folder's), not the PC's; 1.6.1: kept in
		// that folder (a new version folder or drive letter is the same device), one per running window of the copy
		Core.InstallDeviceId.UseDataFolder(knownFolders.ApplicationData, UpdateNoticeService.IsMsiInstall());
		// 1.8.1 (the review of BH-175-m4): this window's slot claimed now, off the UI thread: started while the copy's last window
		// sends a film's last reports, it waits for that window's slot (a few seconds at most) here, not in the sign-in
		_deviceSlot = Task.Run(() =>
		{
			var watch = System.Diagnostics.Stopwatch.StartNew();
			_ = Core.InstallDeviceId.Current;
			return (Core.InstallDeviceId.WindowSlot, watch.ElapsedMilliseconds);
		});

		Host = Microsoft.Extensions.Hosting.Host
		.CreateDefaultBuilder()
		.UseContentRoot(AppContext.BaseDirectory)
		.ConfigureServices((context, services) =>
		{
			Log.Logger = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.File(new RedactingLogFormatter(), knownFolders.LogFile, Serilog.Events.LogEventLevel.Debug, rollingInterval: RollingInterval.Day) // Nixie 1.2.0: no token in the log
				.CreateLogger();
			if (migrationNote is not null)
			{
				Log.Information("{Note}", migrationNote);
			}
			services.AddLogging(NixieLogging.OnlyNixiesLog); // 1.7.3 fix round's re-review: not the host's Event Log provider too (unmasked)

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
			// Nixie 1.3.0: the layout themes (Themes\<Id>\), found in this assembly; NIXIE_LAYOUT overrides the saved one for a run
			services.AddSingleton(sp => new ThemeService(sp.GetRequiredService<ILocalSettingsService>(), LayoutThemes.Discover(typeof(App).Assembly), Core.AppIdentity.ReadEnvironment("LAYOUT")));
			services.AddSingleton<ThemeSwitcher>(); // Nixie 1.3.0: a theme switch at run time (Settings, "Choose a look")
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
			services.AddSingleton<RecentPlays>(); // sg-cw: what the mpv player just played, on Home and the item pages at once
			// 1.6 collections: the library index Nixie's collections are built from, and what the user keeps of them (on this PC)
			services.AddSingleton<Core.SmartCollections.LibraryIndex>();
			services.AddSingleton<Core.SmartCollections.SmartCollectionStore>();
			services.AddSingleton<Core.SmartCollections.NixieLibraryPresets>();
			// the colpage stream's seams (its LibraryPresets.cs): the presets strip's Nixie entries, and Save as collection
			services.AddSingleton<Core.SmartCollections.NixieLibraryPresetSource>();
			services.AddSingleton<ILibraryPresetSource>(sp => sp.GetRequiredService<Core.SmartCollections.NixieLibraryPresetSource>());
			services.AddSingleton<ISavedFilters>(sp => sp.GetRequiredService<Core.SmartCollections.NixieLibraryPresetSource>());
			services.AddTransient<CollectionsViewModel>();
			// end 1.6 collections
			// 1.7 keyless: the outside sources (Settings > Look up lists online), their cache, and the harness's fake in harness runs
			services.AddSingleton(sp => OutsideHubFactory.Create(sp.GetRequiredService<KnownFolders>(), sp.GetRequiredService<ILocalSettingsService>(),
				sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Outside")));
			services.AddSingleton(sp => new Core.Outside.OutsideLists(sp.GetRequiredService<Core.Outside.OutsideHub>(), sp.GetRequiredService<ILocalSettingsService>(), publish: true));
			// end 1.7 keyless
			// 1.7 trending: Trakt (the Client ID this build carries; the harness's own in a harness run) and TMDb trending (the
			// user's token, DPAPI), and the collections matched from their lists
			services.AddSingleton<Core.Outside.TmdbToken>();
			services.AddSingleton(sp =>
			{
				var hub = sp.GetRequiredService<Core.Outside.OutsideHub>();
				return new Core.Outside.TrendingSources(hub, sp.GetRequiredService<Core.Outside.TmdbToken>(), Core.Outside.TraktClientId.Current(harness: hub.Routing.IsHarness),
					tmdbAppToken: Core.Outside.TmdbAppToken.Current(harness: hub.Routing.IsHarness));
			});
			services.AddSingleton(sp => new Core.Outside.TrendingCollections(sp.GetRequiredService<Core.Outside.TrendingSources>(), sp.GetRequiredService<Core.SmartCollections.LibraryIndex>(),
				sp.GetRequiredService<HomeCache>()));
			// end 1.7 trending

			// Views and ViewModels
			services.AddTransient<ShellViewModel>();
			services.AddSingleton<IMainWindowViewModel, MainViewModel>();
			services.AddSingleton<ITitleBarViewModel, TitleBarViewModel>();
			services.AddTransient<HomeViewModel>();
			services.AddTransient<LibraryViewModel>();
			// 1.6 colpage: the harness's stand-in presets (NIXIE_PRESETS, dev builds under the harness only; HarnessPresetSource)
			if (HarnessPresetSource.FromEnvironment() is { } harnessPresets)
			{
				services.AddSingleton<ILibraryPresetSource>(harnessPresets);
			}
			// end 1.6 colpage
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
			services.AddTransient<PersonItemsViewModel>(); // 1.5 search: "With <name>", what a person result opens
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
			services.AddTransient<CollectionViewModel>(); // Nixie 1.4.0
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
			services.AddSingleton<Core.Admin.ServerPower>(); // Nixie 1.2.0 (admin): a restart outlives the dashboard page
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
		Services.CrashTrail.Log("the process's unhandled exception", e.ExceptionObject as Exception); // 1.7.5 (fx3): with the last UI action
	}

	private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
	{
		GetService<ILogger<App>>().LogError(e.Exception, "Unhandled exception");
		// 1.7.5 (fx3; 1.7.3's Drive-in crash): XAML's own message (a stowed error's text), the HRESULT and the last UI action
		Services.CrashTrail.Log("XAML's unhandled exception", e.Exception, e.Message);
		e.Handled = true;
	}

	protected async override void OnLaunched(LaunchActivatedEventArgs args)
	{
		try
		{
			PerfLog.Startup(GetService<ILogger<App>>(), "launched");
			// 1.8.1 (the review of BH-175-m4): which device the server sees this window as (0: the copy's own), and how long the
			// claim took (a window started during the copy's last close waits for its slot)
			var slotLog = GetService<ILogger<App>>();
			_ = _deviceSlot?.ContinueWith(t => slotLog.LogInformation("[device] this window is the copy's device slot {Slot}, claimed in {Ms} ms",
				t.Result.Slot?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none", t.Result.Ms), CancellationToken.None,
				TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
			// Nixie 1.4.0 (bug hunt M3): what threw on the UI thread is in the log before a crash can end the app
			Services.FirstChanceTrail.Start();
			Services.CrashTrail.ListenForUnhandledErrors(GetService<ILogger<App>>()); // 1.7.5 (fx3)
			// 1.5 season: pictures 6 at a time, not WinINet's 2 (a season's stills came 0.4 to 0.9 s after its rows)
			Services.ImageDownloads.AllowMoreAtOnce(GetService<ILogger<App>>());
			// Nixie 1.4.0 (bug hunt m4): every theme's library page gives its load's focus by the app's one arrival rule
			Core.ViewModels.LibraryViewModel.LoadFocusYields = Services.ArrivalFocus.LibraryYields;
			// 1.5.1 fixes (BH-150-M2): every page's Sign in again, once the server no longer takes this sign-in
			Core.SignInState.SignInAgain = () => ((ViewModels.TitleBarViewModel)GetService<ITitleBarViewModel>()).SignInAgain(GetService<IJellyfinAuthenticationService>());
			await Host.StartAsync();

			Locator.SetServiceProvider(Host.Services);
			// 1.6 collections: the Collection page (colpage's view model) reads the index and the saved collections through these
			Core.ViewModels.SmartCollectionServices.Index = GetService<Core.SmartCollections.LibraryIndex>();
			Core.ViewModels.SmartCollectionServices.Store = GetService<Core.SmartCollections.SmartCollectionStore>();
			Core.ViewModels.SmartCollectionServices.Settings = GetService<ILocalSettingsService>();
			GetService<Core.SmartCollections.NixieLibraryPresets>(); // listening to the index from the start: the presets are ready when a library opens
			Core.SmartCollections.SmartCollectionCatalog.Log = GetService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger(typeof(Core.SmartCollections.SmartCollectionCatalog)); // 1.7.2: a kind that couldn't be made
			// 1.7 keyless: the outside lists (each source's cache; what is stale is fetched, for the sources turned on). When they
			// change, Home builds its Made by Nixie rows again
			Core.Outside.OutsideLists.App = GetService<Core.Outside.OutsideLists>();
			Core.Outside.OutsideLists.App.Failed += ex => GetService<ILogger<App>>().LogWarning(ex, "[outside] the lists could not be refreshed");
			Core.Outside.OutsideListsSnapshot.SharedChanged += _ => GetService<HomeCache>().MarkStale();
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(TimeSpan.FromSeconds(10));
					await Core.Outside.OutsideLists.App.RefreshAsync();
				}
				catch (Exception ex)
				{
					GetService<ILogger<App>>().LogWarning(ex, "[outside] the lists could not be refreshed");
				}
			});
			// end 1.7 keyless
			Core.Outside.TrendingServices.Collections = GetService<Core.Outside.TrendingCollections>(); // 1.7 trending: Home and Collections read it

			// Nixie 1.4.0 (the bug hunt's m5): an item Nixie has no page for says so, instead of nothing happening
			Commands.CannotOpen += (_, e) => _ = GetService<IContentDialogService>().ShowMessage(e.Title, e.Message);

			base.OnLaunched(args);

			MainWindow.AppWindow.Closing += AppWindow_Closing;

			// a hung mpv window would freeze our input too (the queues of the two processes are attached)
			GetService<MpvVideoHost>().Unresponsive += reason => GetService<IMpvBackend>().Kill(reason);

			// sg-cw: listening before the first play (it follows the backend's events)
			GetService<RecentPlays>();

			// Seaglass: the saved Glass choice, and the glass brushes following GlassMode
			GlassSetting.Apply(GetService<ISettings>());
			// 1.6 collections: Settings > Collections > "Tidy collection names" (on by default)
			Core.SmartCollections.CollectionSettings.Apply(GetService<ILocalSettingsService>());
			// Nixie 1.3.0: the layout theme's dictionary in App.xaml's slot, before the first page (the sign-in's backdrop is the theme's)
			ThemeResources.Attach(GetService<ThemeService>(), GetService<ILogger<App>>());
			// the harness's light-theme screenshots (NIXIE_THEME=Light|Dark, or SEAGLASS_THEME); unset, the app follows Windows
			if (Enum.TryParse<ElementTheme>(Core.AppIdentity.ReadEnvironment("THEME"), true, out var forcedTheme) && forcedTheme != ElementTheme.Default)
			{
				GlassTheme.SetAppTheme(MainWindow, forcedTheme); // 1.2.0: the caption buttons too (the layout audit's #17)
			}

			GlassTheme.Attach((FrameworkElement)MainWindow.Content);
			// 1.7.5 (fx3): the last things done in the window, said with the crash trail's lines (kinds only, no titles)
			Services.UiTrail.Attach(MainWindow.Content);
			GetService<INavigationService>().Navigated += (_, e) => Services.UiTrail.NotePage(e.SourcePageType);
			NixieTheme.Attach();
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
		IsClosing = true;
		s_closing.Start();
		var log = GetService<ILogger<App>>();
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
			await CloseSequence.RunAsync(GetService<IJellyfinClient>(), GetService<MpvPlaybackService>(), log);
			log.LogInformation("[close] the reports and the backend: done after {Ms} ms", s_closing.ElapsedMilliseconds);
			// fluentfin-mpv player controls: the in-process mpv after the backend that used it (bounded)
			GetService<MpvCompositionVideo>().Dispose();
			await Host.StopAsync();
			log.LogInformation("[close] the app's services: stopped after {Ms} ms", s_closing.ElapsedMilliseconds);
		}
		finally
		{
			// 1.7.5 (the review's Minor): nothing of the watching may keep the window from closing (a throw here left a hidden
			// process): each part on its own, the close itself whatever they did
			Services.CloseWatch? watch = null;
			try
			{
				WatchExit(log);
				// 1.7.5 (g4): one close waited 25 s in here with nothing said; now it says each step and, while it lasts, the last
				watch = WatchWindowClose(log);
			}
			catch (Exception ex)
			{
				log.LogWarning(ex, "[close] the close's watch could not be set up; the window closes all the same");
			}

			try
			{
				MainWindow.Close();
			}
			finally
			{
				watch?.Dispose();
			}

			log.LogInformation("[close] the window: closed after {Ms} ms", s_closing.ElapsedMilliseconds);
		}
	}

	/// <summary>
	/// 1.7.5 (g4): the window's close, watched (<see cref="Services.CloseWatch"/>): the page it closes on, the window's Closed
	/// handlers done (this one is added last, so it runs after the others) and its content unloaded.
	/// </summary>
	private static Services.CloseWatch WatchWindowClose(Microsoft.Extensions.Logging.ILogger log)
	{
		// 1.7.5 (the review's Minor): the page's name is read inside a try (the frame may be going already)
		string page;
		try
		{
			page = (GetService<INavigationService>() as NavigationService)?.Frame?.Content?.GetType().Name ?? "no page";
		}
		catch (Exception ex)
		{
			page = $"a page that could not be read ({ex.GetType().Name})";
		}

		var watch = new Services.CloseWatch(log, s_closing, $"closing the window on {page}");
		try
		{
			MainWindow.Closed += (_, _) => watch.Step = "the window's Closed handlers: done";
			if (MainWindow.Content is FrameworkElement content)
			{
				content.Unloaded += (_, _) => watch.Step = "the window's content: unloaded";
			}
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
		{
			// the window is going already: the watch's own lines still say how long it took
		}

		return watch;
	}

	/// <summary>
	/// Nixie 1.7.3 (rule 10): the close's last parts in the log, as durations from the moment it began: the UI thread's queue
	/// shutting down and done (the window's pages taken away), and the process's exit (after every foreground thread).
	/// </summary>
	private static void WatchExit(Microsoft.Extensions.Logging.ILogger log)
	{
		void Said(string what) => log.LogInformation("[close] {What} after {Ms} ms", what, s_closing.ElapsedMilliseconds);
		try
		{
			var queue = MainWindow.DispatcherQueue;
			queue.ShutdownStarting += (_, _) => Said("the UI thread's queue: shutting down");
			queue.ShutdownCompleted += (_, _) => Said("the UI thread's queue: done");
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
		{
			// the queue has gone already: the exit's line below still comes
		}

		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			Said("the process: exiting");
			Log.CloseAndFlush();
		};
	}
}
