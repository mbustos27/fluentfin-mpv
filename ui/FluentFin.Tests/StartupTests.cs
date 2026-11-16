using System.Collections.ObjectModel;
using FluentFin.Activation;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.MediaPlayers;
using FluentFin.Services;
using FluentFin.Tests.Mpv;
using FluentFin.UI.Core;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// fluentfin-mpv (startup-perf): the window before the sign-in (docs/research/perf-ui.md O4), the sign-in shown while it
/// runs, Flyleaf's engine with its first player (O5), the Windows player's codec probe only for that player (O13), and the
/// mpv backend pre-warmed when the shell is shown and shut down when it is left (docs/BRIDGE.md §3, §4).
/// </summary>
public sealed class StartupTests
{
	/// <summary>An activation handler that takes as long as the test says (an automatic sign-in to a slow server).</summary>
	private sealed class SlowHandler(Task done, List<string> steps) : IActivationHandler
	{
		public bool CanHandle(object args) => true;

		public async Task HandleAsync(object args)
		{
			steps.Add("activation handler started");
			await done;
			steps.Add("activation handler done");
		}
	}

	/// <summary>The default handler never handles an argument that is not a launch (only a launch is one).</summary>
	private sealed class NoDefault : ActivationHandler<LaunchActivatedEventArgs>
	{
		protected override Task HandleInternalAsync(LaunchActivatedEventArgs args) => throw new InvalidOperationException("not a launch");
	}

	[Fact]
	public async Task TheWindowIsShown_BeforeTheSignInFinishes()
	{
		var steps = new List<string>();
		var signIn = new TaskCompletionSource();
		var service = new ActivationService(new NoDefault(), [new SlowHandler(signIn.Task, steps)], NullLogger<ActivationService>.Instance, () => steps.Add("window shown"));

		var activation = service.ActivateAsync(new object());
		await Task.Delay(50);

		Assert.Equal(["window shown", "activation handler started"], steps);
		Assert.False(activation.IsCompleted);

		signIn.SetResult();
		await activation.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(["window shown", "activation handler started", "activation handler done"], steps);
	}

	/// <summary>Calls the handler's sign-in directly: a launch's arguments cannot be made outside WinUI.</summary>
	private sealed class Handler(INavigationService setup, INavigationService main, ISettings settings, IJellyfinAuthenticationService auth, IMainWindowViewModel window)
		: DefaultActivationHandler(setup, main, settings, Mock.Of<ILocalSettingsService>(), Mock.Of<IPluginManager>(), auth, window, NullLogger<DefaultActivationHandler>.Instance)
	{
		public Task SignIn() => HandleInternalAsync(null!);
	}

	private static (Handler Handler, Mock<INavigationService> Setup, Mock<IMainWindowViewModel> Window, TaskCompletionSource<bool> SignIn) SavedLogin(string serverName)
	{
		var server = new SavedServer { DisplayName = serverName, Users = [new SavedUser { Username = "alice" }] };
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.Servers).Returns(new ObservableCollection<SavedServer> { server });
		var signIn = new TaskCompletionSource<bool>();
		var auth = new Mock<IJellyfinAuthenticationService>();
		auth.Setup(x => x.Authenticate(server, server.Users[0])).Returns(signIn.Task);
		var setup = new Mock<INavigationService>();
		var window = new Mock<IMainWindowViewModel>();
		window.SetupProperty(x => x.StartupStatus);
		return (new Handler(setup.Object, Mock.Of<INavigationService>(), settings.Object, auth.Object, window.Object), setup, window, signIn);
	}

	[Theory]
	[InlineData(true, "FluentFin.ViewModels.ShellViewModel")]
	[InlineData(false, "FluentFin.Core.ViewModels.SelectServerViewModel")]
	public async Task TheAutomaticSignIn_SaysSoInTheWindow_UntilItEnds(bool succeeds, string next)
	{
		var (handler, setup, window, signIn) = SavedLogin("Home");

		var running = handler.SignIn();
		await Task.Delay(20);

		Assert.Equal("Signing in to Home…", window.Object.StartupStatus);
		setup.Verify(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()), Times.Never);

		signIn.SetResult(succeeds);
		await running.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Null(window.Object.StartupStatus);
		setup.Verify(x => x.NavigateTo(next, It.IsAny<object?>(), It.IsAny<bool>()), Times.Once);
	}

	[Fact]
	public async Task ASignInThatThrows_StillClearsTheStatus()
	{
		var (handler, _, window, signIn) = SavedLogin("");

		var running = handler.SignIn();
		await Task.Delay(20);
		Assert.Equal("Signing in to your server…", window.Object.StartupStatus);
		signIn.SetException(new HttpRequestException("unreachable"));

		await Assert.ThrowsAsync<HttpRequestException>(() => running);
		Assert.Null(window.Object.StartupStatus);
	}

	/// <summary>
	/// The window's startup panel and its ring show from <see cref="IMainWindowViewModel.HasStartupStatus"/>, which goes
	/// false, and says so, when the sign-in clears the text to null (through a function of the text the ring stayed up over
	/// the video for good: FluentFin.Core.Tests' BindingXamlTests).
	/// </summary>
	[Fact]
	public void TheStartupPanelsBool_FollowsTheText_DownToNull()
	{
		var window = new MainViewModel(Mock.Of<ITitleBarViewModel>());
		var changed = new List<string?>();
		window.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		Assert.False(window.HasStartupStatus);

		window.StartupStatus = "Signing in to Home…";
		Assert.True(window.HasStartupStatus);
		Assert.Contains(nameof(IMainWindowViewModel.HasStartupStatus), changed);

		changed.Clear();
		window.StartupStatus = null;
		Assert.False(window.HasStartupStatus);
		Assert.Contains(nameof(IMainWindowViewModel.HasStartupStatus), changed);

		window.StartupStatus = "  ";
		Assert.False(window.HasStartupStatus);
	}
}

/// <summary>fluentfin-mpv (startup-perf, O5): Flyleaf's engine starts with the first Flyleaf player, once.</summary>
[Collection(nameof(FlyleafEngineTests))]
public sealed class FlyleafEngineTests : IDisposable
{
	private readonly Action _real = FlyleafEngine.Start;
	private readonly Func<IReadOnlyList<(string Name, string Path)>> _realClash = FlyleafEngine.FFmpegLoadedElsewhere;

	public FlyleafEngineTests()
	{
		FlyleafEngine.Reset();
		// this process may have loaded the vendored libmpv's FFmpeg (LibmpvProbeTests): only the guard's own tests clash
		FlyleafEngine.FFmpegLoadedElsewhere = () => [];
	}

	public void Dispose()
	{
		FlyleafEngine.Start = _real;
		FlyleafEngine.FFmpegLoadedElsewhere = _realClash;
		FlyleafEngine.Reset();
	}

	/// <summary>
	/// fluentfin-mpv: Flyleaf does not start while another FFmpeg with its DLLs' names is loaded (FluentFin's own player
	/// controls ran first): Windows would give Flyleaf that FFmpeg. It says to restart FluentFin, and is tried again later.
	/// </summary>
	[Fact]
	public void EnsureStarted_RefusesWhileAnotherFFmpegOfItsNamesIsLoaded()
	{
		var starts = 0;
		FlyleafEngine.Start = () => starts++;
		FlyleafEngine.FFmpegLoadedElsewhere = () => [("avcodec-63.dll", @"C:\App\libmpv\avcodec-63.dll")];

		var ex = Assert.Throws<InvalidOperationException>(FlyleafEngine.EnsureStarted);

		Assert.Contains("avcodec-63.dll", ex.Message);
		Assert.Contains(@"C:\App\libmpv", ex.Message);
		Assert.Contains("restart FluentFin to use this player", ex.Message);
		Assert.Equal(0, starts);
		Assert.False(FlyleafEngine.IsStarted);

		FlyleafEngine.FFmpegLoadedElsewhere = () => [];
		FlyleafEngine.EnsureStarted();
		Assert.Equal(1, starts);
	}

	/// <summary>The real check: names of Flyleaf's FFMpeg folder, loaded from elsewhere (none in a process that loaded no FFmpeg).</summary>
	[Fact]
	public void TheRealClashCheck_LooksAtFlyleafsFFmpegNames()
	{
		var names = FluentFin.Mpv.NativeModules.FFmpegNames(Path.Combine(AppContext.BaseDirectory, "FFMpeg"));
		var clashes = _realClash();

		Assert.All(clashes, c => Assert.Contains(c.Name, names));
		Assert.All(clashes, c => Assert.NotEqual(Path.Combine(AppContext.BaseDirectory, "FFMpeg"), Path.GetDirectoryName(c.Path), StringComparer.OrdinalIgnoreCase));
	}

	[Fact]
	public void EnsureStarted_StartsTheEngineOnce()
	{
		var starts = 0;
		FlyleafEngine.Start = () => Interlocked.Increment(ref starts);

		Parallel.For(0, 16, _ => FlyleafEngine.EnsureStarted());
		FlyleafEngine.EnsureStarted();

		Assert.Equal(1, starts);
		Assert.True(FlyleafEngine.IsStarted);
	}

	[Fact]
	public void AStartThatThrows_IsTriedAgain()
	{
		var starts = 0;
		FlyleafEngine.Start = () =>
		{
			if (++starts == 1)
			{
				throw new DllNotFoundException("avcodec");
			}
		};

		Assert.Throws<DllNotFoundException>(FlyleafEngine.EnsureStarted);
		Assert.False(FlyleafEngine.IsStarted);
		FlyleafEngine.EnsureStarted();
		Assert.Equal(2, starts);
	}

	/// <summary>
	/// The launch must not start it (App.OnLaunched), and the one place a Flyleaf player is made must start it before the
	/// player (MediaPlayerHost.CreateFlyleaf): read from the sources, since neither can run outside WinUI.
	/// </summary>
	[Fact]
	public void TheLaunchDoesNotStartIt_AndTheFlyleafPlayerDoes()
	{
		var app = Source("FluentFin", "App.xaml.cs");
		Assert.DoesNotContain("FlyleafLib.Engine.Start", app);
		Assert.DoesNotContain("StartFlyleaf()", app);
		Assert.DoesNotContain("FlyleafEngine.EnsureStarted", app);

		var host = Source("FluentFin", "Controls", "MediaPlayerHost.xaml.cs");
		var create = host[host.IndexOf("private FlyleafHost CreateFlyleaf()", StringComparison.Ordinal)..];
		create = create[..create.IndexOf("return host;", StringComparison.Ordinal)];
		var ensure = create.IndexOf("FlyleafEngine.EnsureStarted();", StringComparison.Ordinal);
		Assert.True(ensure >= 0, "CreateFlyleaf starts the engine");
		Assert.True(ensure < create.IndexOf("new FlyleafHost()", StringComparison.Ordinal), "before the player is made");

		// and nothing else starts Flyleaf's engine
		var elsewhere = Directory.EnumerateFiles(Path.Combine(RepoRoot, "FluentFin"), "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
			.Where(f => File.ReadAllText(f).Contains("FlyleafLib.Engine.Start", StringComparison.Ordinal))
			.Select(Path.GetFileName);
		Assert.Equal(["FlyleafEngine.cs"], elsewhere);
	}

	private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot, .. parts]));

	internal static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}
}

/// <summary>fluentfin-mpv (startup-perf, O13): the Windows player's codec probe runs only for that player, once.</summary>
[Collection(nameof(DeviceProfileFactoryTests))]
public sealed class DeviceProfileFactoryTests : IDisposable
{
	private readonly Func<Task> _real = DeviceProfileFactory.WindowsProbe;
	private int _probes;

	public DeviceProfileFactoryTests()
	{
		DeviceProfileFactory.Reset();
		DeviceProfileFactory.WindowsProbe = () =>
		{
			Interlocked.Increment(ref _probes);
			return Task.CompletedTask;
		};
	}

	public void Dispose()
	{
		DeviceProfileFactory.WindowsProbe = _real;
		DeviceProfileFactory.Reset();
	}

	private static DeviceProfileFactory For(MediaPlayerType player)
	{
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(player);
		return new DeviceProfileFactory(settings.Object);
	}

	[Theory]
	[InlineData(MediaPlayerType.Mpv)]
	[InlineData(MediaPlayerType.Flyleaf)]
	[InlineData(MediaPlayerType.Vlc)]
	public async Task OtherPlayers_NeverProbe(MediaPlayerType player)
	{
		await For(player).PrepareAsync();
		Assert.Equal(0, _probes);
		Assert.NotNull(For(player).GetDeviceProfile());
	}

	[Fact]
	public async Task TheWindowsPlayer_ProbesOnce()
	{
		var factory = For(MediaPlayerType.WindowsMediaPlayer);
		await factory.PrepareAsync();
		await factory.PrepareAsync();
		await For(MediaPlayerType.WindowsMediaPlayer).PrepareAsync();

		Assert.Equal(1, _probes);
	}

	[Fact]
	public async Task AProbeThatFailed_RunsAgain()
	{
		DeviceProfileFactory.WindowsProbe = () => Interlocked.Increment(ref _probes) == 1 ? Task.FromException(new InvalidOperationException("no audio")) : Task.CompletedTask;
		var factory = For(MediaPlayerType.WindowsMediaPlayer);

		await Assert.ThrowsAsync<InvalidOperationException>(factory.PrepareAsync);
		await factory.PrepareAsync();

		Assert.Equal(2, _probes);
	}
}

/// <summary>
/// fluentfin-mpv (startup-perf review): the window is shown before an automatic sign-in finishes, so the title bar shows the
/// user (search, and the account menu with Log out and Switch user) only once FluentFin's session is made: a sign-out in
/// that gap revoked the token the sign-in then opened the shell with.
/// </summary>
public sealed class SignInTitleBarTests
{
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<ITitleBarViewModel> _titleBar = new();
	private readonly JellyfinAuthenticationService _service;
	private readonly Jellyfin.Sdk.Generated.Models.AuthenticationResult _auth = new()
	{
		AccessToken = "fake-token-0123456789",
		ServerId = "fake-server",
		User = new Jellyfin.Sdk.Generated.Models.UserDto { Id = Guid.NewGuid(), Name = "alice" },
	};

	public SignInTitleBarTests()
	{
		_titleBar.SetupProperty(x => x.User);
		_service = new JellyfinAuthenticationService(_client.Object, _titleBar.Object, Mock.Of<ILocalSettingsService>(), Mock.Of<INavigationService>(),
			NullLogger<JellyfinAuthenticationService>.Instance);
	}

	[Fact]
	public async Task TheUser_ShowsInTheTitleBar_OnlyOnceTheSessionIsMade()
	{
		var session = new TaskCompletionSource();
		_client.Setup(x => x.Initialize("http://jf:8096", _auth)).Returns(session.Task);

		var signIn = _service.FinishSignIn("http://jf:8096", _auth);
		await Task.Delay(20);

		Assert.Null(_titleBar.Object.User);
		session.SetResult();
		await signIn.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Same(_auth.User, _titleBar.Object.User);
	}

	[Fact]
	public async Task ASessionThatFails_LeavesNoUserInTheTitleBar()
	{
		_client.Setup(x => x.Initialize(It.IsAny<string>(), It.IsAny<Jellyfin.Sdk.Generated.Models.AuthenticationResult>()))
			.ThrowsAsync(new HttpRequestException("unreachable"));

		await Assert.ThrowsAsync<HttpRequestException>(() => _service.FinishSignIn("http://jf:8096", _auth));

		Assert.Null(_titleBar.Object.User);
	}
}

/// <summary>fluentfin-mpv (startup-perf): the shell is where signing in ends and signing out begins (docs/BRIDGE.md §3).</summary>
public sealed class ShellPrewarmTests
{
	[Fact]
	public async Task EnteringTheShell_PrewarmsTheMpvBackend_AndLeavingIt_ShutsItDown()
	{
		var backend = new RecordingBackend();
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Mpv);
		var identity = new BridgeIdentity("http://jf:8096", "srv", "Home", "aaaa", "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");
		var prewarm = new MpvPrewarm(backend, settings.Object, NullLogger<MpvPrewarm>.Instance) { Delay = TimeSpan.Zero, Identity = () => identity };
		var navigation = new Mock<INavigationService>();
		var shell = new ShellViewModel(navigation.Object, Mock.Of<INavigationViewService>(), prewarm, NullLogger<ShellViewModel>.Instance);

		await shell.OnNavigatedTo(null!);
		await prewarm.Current.WaitAsync(TimeSpan.FromSeconds(10));

		navigation.Verify(x => x.NavigateTo("FluentFin.Core.ViewModels.HomeViewModel", It.IsAny<object?>(), It.IsAny<bool>()), Times.Once);
		Assert.Equal([identity], backend.Prewarmed);

		await shell.OnNavigatedFrom();
		await prewarm.Current.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(1, backend.Shutdowns);
	}
}
