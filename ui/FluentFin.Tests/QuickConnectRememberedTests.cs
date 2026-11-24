using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.UI.Core;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// 174-signin (a friend's report: "log in does not remember if you use quick connect", "normal log in saved"): a Quick
/// Connect sign-in with "Remember me" is kept as a password one is, so the next start (a fresh settings service, auth
/// service and start-up sign-in on the same data folder) opens signed in; without "Remember me" nothing is kept; Sign out
/// forgets it. A fake server on localhost answers; no real server is asked.
/// </summary>
[Collection(SavedSignInCollection.Name)] // SessionInfo's saved sign-in is static
public sealed class QuickConnectRememberedTests : IDisposable
{
	private const string Token = "qc-token-for-nixie-0123456789";
	private const string UserId = "aaaaaaaa000000000000000000000174";
	private readonly HttpListener _listener = new();
	private readonly List<(string Method, string Path, string? Token, string Query)> _requests = [];
	private readonly HashSet<string> _revoked = [];
	private volatile int _refusal = 401; // what the server answers a token it no longer takes
	private readonly string _baseUrl;
	private readonly Task _loop;
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-174-tests", Guid.NewGuid().ToString("N"));

	public QuickConnectRememberedTests()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		_baseUrl = $"http://localhost:{port}";
		_listener.Prefixes.Add(_baseUrl + "/");
		_listener.Start();
		_loop = Task.Run(Serve);
		Directory.CreateDirectory(_folder);
		// a settings file of its own: with none, every Settings shares SettingKeys.Servers' one default collection (static)
		File.WriteAllText(Path.Combine(_folder, KnownFolders.SettingsFileName), """{ "Servers": [] }""");
	}

	public void Dispose()
	{
		_listener.Stop();
		_listener.Close();
		_loop.Wait(TimeSpan.FromSeconds(2));
		SessionInfo.CurrentUserCrendentials = null!;
		try
		{
			Directory.Delete(_folder, true);
		}
		catch (IOException)
		{
		}
	}

	private async Task Serve()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception)
			{
				return;
			}

			var path = context.Request.Url!.AbsolutePath;
			var header = context.Request.Headers["Authorization"] ?? context.Request.Headers["X-Emby-Authorization"] ?? "";
			var token = Regex.Match(header, "Token=\"([^\"]*)\"") is { Success: true } m ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
			lock (_requests)
			{
				_requests.Add((context.Request.HttpMethod, path, token, context.Request.Url.Query));
			}

			var good = token == Token && !_revoked.Contains(Token);
			var (status, answer) = path switch
			{
				"/Users/AuthenticateWithQuickConnect" => (200, $$$"""{"AccessToken":"{{{Token}}}","ServerId":"fake-server","User":{"Id":"{{{UserId}}}","Name":"alice","ServerId":"fake-server"}}"""),
				// a password sign-in: the name as the server has it, whatever was typed ("Alice")
				"/Users/AuthenticateByName" => (200, $$$"""{"AccessToken":"pw-token-for-nixie-0123456789","ServerId":"fake-server","User":{"Id":"{{{UserId}}}","Name":"alice","ServerId":"fake-server"}}"""),
				"/Users/Me" when good => (200, $$$"""{"Id":"{{{UserId}}}","Name":"alice","ServerId":"fake-server"}"""),
				"/Sessions" when good => (200, $$$"""[{"Id":"session-174","UserId":"{{{UserId}}}","DeviceId":"{{{InstallDeviceId.Current}}}"}]"""),
				"/Users/Me" or "/Sessions" => (_refusal, ""),
				_ => (200, """{"ServerName":"fake","Version":"10.11.0","Id":"fake-server","StartupWizardCompleted":true}"""),
			};
			var bytes = Encoding.UTF8.GetBytes(answer);
			context.Response.StatusCode = status;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = bytes.Length;
			await context.Response.OutputStream.WriteAsync(bytes);
			context.Response.Close();
		}
	}

	private List<(string Method, string Path, string? Token, string Query)> Requests()
	{
		lock (_requests)
		{
			return [.. _requests];
		}
	}

	/// <summary>One run of the app on the data folder: its settings as read from the file, and its sign-in service.</summary>
	private sealed class Run
	{
		public required FluentFin.Core.Services.LocalSettingsService LocalSettings { get; init; }
		public required FluentFin.Core.Settings.Settings Settings { get; init; }
		public required Mock<IJellyfinClient> Client { get; init; }
		public required Mock<INavigationService> Setup { get; init; }
		public required JellyfinAuthenticationService Auth { get; init; }
	}

	private Run Start()
	{
		var localSettings = new FluentFin.Core.Services.LocalSettingsService(new KnownFolders(_folder));
		var settings = new FluentFin.Core.Settings.Settings(localSettings);
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Initialize(It.IsAny<string>(), It.IsAny<AuthenticationResult>())).Returns(Task.CompletedTask);
		client.Setup(x => x.Logout()).Returns(Task.CompletedTask);
		var titleBar = new Mock<ITitleBarViewModel>();
		titleBar.SetupAllProperties();
		var setup = new Mock<INavigationService>();
		var auth = new JellyfinAuthenticationService(client.Object, titleBar.Object, localSettings, setup.Object, NullLogger<JellyfinAuthenticationService>.Instance);
		return new Run { LocalSettings = localSettings, Settings = settings, Client = client, Setup = setup, Auth = auth };
	}

	/// <summary>The start's automatic sign-in (DefaultActivationHandler's), called directly.</summary>
	private sealed class StartSignIn(INavigationService setup, ISettings settings, IJellyfinAuthenticationService auth)
		: FluentFin.Activation.DefaultActivationHandler(setup, Mock.Of<INavigationService>(), settings, Mock.Of<ILocalSettingsService>(),
			Mock.Of<IPluginManager>(), auth, Mock.Of<IMainWindowViewModel>(), NullLogger<FluentFin.Activation.DefaultActivationHandler>.Instance)
	{
		public Task Go() => HandleInternalAsync(null!);
	}

	/// <summary>The first run: the server page saved the server; Quick Connect's code is approved and signs in.</summary>
	private async Task<(Run Run, SavedServer Server)> SignInWithQuickConnect(bool remember)
	{
		var run = Start();
		run.Settings.ListenToChanges();
		var server = new SavedServer { DisplayName = "home", PublicUrl = _baseUrl, LocalUrl = _baseUrl, Id = "fake-server" };
		run.Settings.Servers.Add(server);
		Assert.True(await run.Auth.Authenticate(server, new QuickConnectResult { Secret = "approved-secret", Authenticated = true }, remember));
		run.Client.Verify(x => x.Initialize(_baseUrl, It.Is<AuthenticationResult>(a => a.AccessToken == Token)), Times.Once);
		return (run, server);
	}

	private static bool WentToTheShell(Run run) =>
		run.Setup.Invocations.Any(i => i.Method.Name == "NavigateTo" && Equals(i.Arguments[0], "FluentFin.ViewModels.ShellViewModel"));

	private static bool WentToTheServerList(Run run) =>
		run.Setup.Invocations.Any(i => i.Method.Name == "NavigateTo" && Equals(i.Arguments[0], "FluentFin.Core.ViewModels.SelectServerViewModel"));

	[Fact]
	public async Task AQuickConnectSignIn_WithRememberMe_IsStillSignedIn_AfterARestart()
	{
		await SignInWithQuickConnect(remember: true);
		var first = Requests().Count;

		// the restart: everything read again from the data folder
		var again = Start();
		await new StartSignIn(again.Setup.Object, again.Settings, again.Auth).Go().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.True(WentToTheShell(again), "the start opened the shell");
		Assert.False(WentToTheServerList(again), "the start showed the server list");
		// it went on with the saved token: asked who it is, and signed in nowhere again (no code, no password)
		again.Client.Verify(x => x.Initialize(_baseUrl, It.Is<AuthenticationResult>(a => a.AccessToken == Token && a.User!.Name == "alice" && a.ServerId == "fake-server")), Times.Once);
		var later = Requests().Skip(first).ToList();
		Assert.Contains(later, r => r.Path == "/Users/Me" && r.Token == Token);
		Assert.DoesNotContain(later, r => r.Path.StartsWith("/Users/Authenticate", StringComparison.Ordinal));
		Assert.Null(SessionInfo.CurrentUserCrendentials); // no password to hand on (Jellyseerr, Sign in again)

		// this device's session is looked up afterwards, not waited for
		await WaitFor(() => Requests().Skip(first).Any(r => r.Path == "/Sessions" && r.Token == Token && r.Query.Contains(InstallDeviceId.Current, StringComparison.Ordinal)));

		// the token is kept protected, never as it is
		Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(_folder, KnownFolders.SettingsFileName)), StringComparison.Ordinal);
		var saved = Assert.Single(Assert.Single(again.Settings.Servers).Users);
		Assert.Equal("alice", saved.Username);
		Assert.Equal("", saved.Password.Unprotect(again.LocalSettings.GetEntropyBytes())); // an empty one, protected (1.7.3)
		Assert.Equal(Token, saved.Token.Unprotect(again.LocalSettings.GetEntropyBytes()));
	}

	/// <summary>
	/// 174-signin (the review's m2): Nixie 1.7.3 and earlier, started on this data folder (a downgrade), read every saved
	/// user's password at the start (DefaultActivationHandler, then Password.Unprotect) and know nothing of the token. An
	/// empty array there is no DPAPI blob: it threw, uncaught, and every start ended at "Startup failed". The Quick Connect
	/// entry's password reads as an empty one: that start signs in with it, the server refuses, the server list shows.
	/// </summary>
	[Fact]
	public async Task ASavedQuickConnectSignIn_ReadsAsAnEmptyPassword_InNixie173()
	{
		await SignInWithQuickConnect(remember: true);

		// 1.7.3's read: the settings file as it is (its SavedUser has no Token), the password unprotected with the entropy
		var older = new FluentFin.Core.Services.LocalSettingsService(new KnownFolders(_folder));
		using var file = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, KnownFolders.SettingsFileName)));
		var user = file.RootElement.GetProperty("Servers")[0].GetProperty("Users")[0];
		var password = Convert.FromBase64String(user.GetProperty("Password").GetString()!);

		Assert.Equal("", password.Unprotect(older.GetEntropyBytes()));
		Assert.Equal("alice", user.GetProperty("Username").GetString());
	}

	/// <summary>
	/// 174-signin (the review's m3): Jellyfin's names ignore case. A password sign-in saves the name as typed ("Alice"),
	/// Quick Connect the server's ("alice"): they were two saved users, and the start signs in by itself only with one.
	/// </summary>
	[Fact]
	public async Task APasswordAndAQuickConnectSignIn_OfOneUser_InAnotherCase_AreOneSavedUser_AndTheStartSignsIn()
	{
		var run = Start();
		run.Settings.ListenToChanges();
		var server = new SavedServer { DisplayName = "home", PublicUrl = _baseUrl, LocalUrl = _baseUrl, Id = "fake-server" };
		run.Settings.Servers.Add(server);
		Assert.Null(await run.Auth.Authenticate(server, "Alice", "s3cret", remember: true));
		Assert.True(await run.Auth.Authenticate(server, new QuickConnectResult { Secret = "approved-secret", Authenticated = true }, remember: true));

		var saved = Assert.Single(server.Users);
		Assert.Equal("alice", saved.Username);
		Assert.NotEmpty(saved.Token);

		var again = Start();
		await new StartSignIn(again.Setup.Object, again.Settings, again.Auth).Go().WaitAsync(TimeSpan.FromSeconds(30));
		Assert.True(WentToTheShell(again), "the start opened the shell");
	}

	[Fact]
	public async Task AQuickConnectSignIn_WithoutRememberMe_IsNotKept()
	{
		var (_, server) = await SignInWithQuickConnect(remember: false);
		Assert.Empty(server.Users);

		var again = Start();
		await new StartSignIn(again.Setup.Object, again.Settings, again.Auth).Go().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.True(WentToTheServerList(again));
		Assert.False(WentToTheShell(again));
		Assert.Empty(Assert.Single(again.Settings.Servers).Users);
	}

	[Fact]
	public async Task SignOut_ForgetsASavedQuickConnectSignIn()
	{
		var (run, server) = await SignInWithQuickConnect(remember: true);
		Assert.Single(server.Users);
		var titleBar = TitleBar(run.Client, server);

		await titleBar.Logout();

		Assert.Empty(server.Users);
		var first = Requests().Count;
		var again = Start();
		await new StartSignIn(again.Setup.Object, again.Settings, again.Auth).Go().WaitAsync(TimeSpan.FromSeconds(30));
		Assert.True(WentToTheServerList(again));
		Assert.False(WentToTheShell(again));
		Assert.Empty(Requests().Skip(first)); // the forgotten token is never sent
	}

	/// <summary>The parameters the server list was opened with.</summary>
	private static List<object?> ServerListParameters(Mock<INavigationService> setup) =>
		[.. setup.Invocations.Where(i => i.Method.Name == "NavigateTo" && Equals(i.Arguments[0], "FluentFin.Core.ViewModels.SelectServerViewModel")).Select(i => i.Arguments[1])];

	/// <summary>The parameters the sign-in page was opened with.</summary>
	private static List<object?> SignInPageParameters(Mock<INavigationService> setup) =>
		[.. setup.Invocations.Where(i => i.Method.Name == "NavigateTo" && Equals(i.Arguments[0], "FluentFin.ViewModels.LoginViewModel")).Select(i => i.Arguments[1])];

	/// <summary>
	/// 174-signin (the review's m1): a saved token the server refuses (401, or 403) is no sign-in: the page says so (the
	/// server list opened without a word) and the token is forgotten (every start tried it again, and a later sign-in without
	/// "Remember me" left it saved). 174-sessions (BS-M3): that page is the server's sign-in page, with the name filled in.
	/// </summary>
	[Fact]
	public async Task ATokenTheServerRefuses_IsForgotten_AndTheSignInPageSaysSo()
	{
		await SignInWithQuickConnect(remember: true);
		_revoked.Add(Token); // signed out on another device, or the device removed on the server
		_refusal = 401;

		var again = Start();
		await new StartSignIn(again.Setup.Object, again.Settings, again.Auth).Go().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.False(WentToTheShell(again));
		Assert.False(WentToTheServerList(again));
		again.Client.Verify(x => x.Initialize(It.IsAny<string>(), It.IsAny<AuthenticationResult>()), Times.Never);
		var notice = Assert.IsType<SetupNotice>(Assert.Single(SignInPageParameters(again.Setup)));
		Assert.Equal("The saved sign-in to home no longer works. Sign in again.", notice.Message);
		Assert.Equal("alice", notice.Username);
		Assert.Same(Assert.Single(again.Settings.Servers), notice.Server);
		Assert.Empty(Assert.Single(again.Settings.Servers).Users);
		// and in the file: the next start asks nothing
		var third = Start();
		Assert.Empty(Assert.Single(third.Settings.Servers).Users);
	}

	/// <summary>
	/// 174-sessions (the review's m1): a 403 is the account not let in now (disabled, remote access off, outside its hours),
	/// not the token: it stays for the next start, and the server list says so.
	/// </summary>
	[Fact]
	public async Task A403_KeepsTheSavedToken_AndTheServerListSaysSo()
	{
		await SignInWithQuickConnect(remember: true);
		_revoked.Add(Token);
		_refusal = 403;

		var again = Start();
		await new StartSignIn(again.Setup.Object, again.Settings, again.Auth).Go().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.False(WentToTheShell(again));
		var notice = Assert.IsType<SetupNotice>(Assert.Single(ServerListParameters(again.Setup)));
		Assert.Equal("home doesn't let this account sign in right now. Try again later; if it keeps happening, the server's owner may need to know.", notice.Message);
		Assert.NotEmpty(Assert.Single(Assert.Single(Start().Settings.Servers).Users).Token);
	}

	/// <summary>
	/// 174-signin (the review's m1): no answer (the network, the server down) keeps the saved token. 174-sessions (BS-M3): and
	/// the server list says why, naming the server (it said nothing).
	/// </summary>
	[Fact]
	public async Task NoAnswer_KeepsTheSavedToken_ForTheNextStart()
	{
		await SignInWithQuickConnect(remember: true);
		_listener.Stop(); // the server's address no longer answers

		var again = Start();
		await new StartSignIn(again.Setup.Object, again.Settings, again.Auth).Go().WaitAsync(TimeSpan.FromSeconds(60));

		Assert.False(WentToTheShell(again));
		var notice = Assert.IsType<SetupNotice>(Assert.Single(ServerListParameters(again.Setup)));
		Assert.StartsWith("Couldn't sign in to home. ", notice.Message);
		Assert.Contains(_baseUrl, notice.Message);
		Assert.False(again.Auth.SavedSignInRefused);
		Assert.NotEmpty(Assert.Single(Assert.Single(Start().Settings.Servers).Users).Token);
	}

	/// <summary>174-signin: the server list and the sign-in page show the line they open with.</summary>
	[Fact]
	public async Task TheServerList_AndTheSignInPage_ShowTheLineTheyOpenWith()
	{
		var settings = new Mock<ISettings>();
		settings.Setup(x => x.Servers).Returns(new System.Collections.ObjectModel.ObservableCollection<SavedServer>());
		var list = new SelectServerViewModel(settings.Object, Mock.Of<INavigationService>(), TestHomeCache.Unused());
		await list.OnNavigatedTo(new SetupNotice("The saved sign-in to home no longer works. Sign in again."));
		Assert.Equal("The saved sign-in to home no longer works. Sign in again.", list.ErrorMessage);
		Assert.True(list.HasError);

		var server = new SavedServer { DisplayName = "home", PublicUrl = _baseUrl, LocalUrl = _baseUrl, Id = "fake-server" };
		var page = new LoginViewModel(Mock.Of<IJellyfinAuthenticationService>(), Mock.Of<INavigationService>(), Mock.Of<ILocalSettingsService>());
		await page.OnNavigatedTo(new SetupNotice(TitleBarViewModel.ForgetFailedNotice, server));
		Assert.Same(server, page.Server);
		Assert.True(page.HasNotice);
		Assert.False(page.HasError); // not a sign-in that failed
		await page.OnNavigatedTo(server);
		Assert.False(page.HasNotice);
	}

	/// <summary>
	/// 174-signin (the review's m4): a settings file that can't be written while the saved token is forgotten doesn't stop
	/// Sign out halfway (no page change, the token alive on the server): the sign-out completes and says it in one line.
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SignOut_Completes_WhenTheSavedSignInCantBeForgotten(bool switchUser)
	{
		var (run, server) = await SignInWithQuickConnect(remember: true);
		server.Users.CollectionChanged += (_, _) => throw new IOException("The disk is full.");
		var setup = new Mock<INavigationService>();
		var titleBar = TitleBar(run.Client, server, setup.Object);

		await (switchUser ? titleBar.SwitchUser() : titleBar.Logout()).WaitAsync(TimeSpan.FromSeconds(10));

		run.Client.Verify(x => x.Logout(), Times.Once); // the token ended on the server
		Assert.False(titleBar.IsSigningOut);
		Assert.Null(titleBar.User);
		var opened = Assert.Single(setup.Invocations, i => i.Method.Name == "NavigateTo");
		Assert.Equal(switchUser ? "FluentFin.ViewModels.LoginViewModel" : "FluentFin.Core.ViewModels.SelectServerViewModel", opened.Arguments[0]);
		var notice = Assert.IsType<SetupNotice>(opened.Arguments[1]);
		Assert.Equal(TitleBarViewModel.ForgetFailedNotice, notice.Message);
		Assert.Equal(switchUser ? server : null, notice.Server);
	}

	[Fact]
	public void TheSignInPage_FillsNoPassword_ForASavedQuickConnectSignIn()
	{
		var vm = new LoginViewModel(Mock.Of<IJellyfinAuthenticationService>(), Mock.Of<INavigationService>(), Mock.Of<ILocalSettingsService>());

		Assert.Equal("", vm.Unprotect([])); // it threw: an empty array is no DPAPI blob
	}

	private static TitleBarViewModel TitleBar(Mock<IJellyfinClient> client, SavedServer server, INavigationService? setup = null)
	{
		var backend = new Mock<IMpvBackend>();
		backend.Setup(x => x.Events).Returns(Observable.Never<BridgeEvent>());
		backend.Setup(x => x.PlaybackInterrupted).Returns(Observable.Never<MpvInterruptedPlayback>());
		var playback = new MpvPlaybackService(backend.Object, client.Object, NullLogger<MpvPlaybackService>.Instance);
		return new TitleBarViewModel(Mock.Of<INavigationService>(), setup ?? Mock.Of<INavigationService>(), Mock.Of<INavigationViewService>(), client.Object, playback)
		{
			LibmpvUnavailable = () => null,
			User = new UserDto { Name = "alice" },
			CurrentServer = server,
		};
	}

	private static async Task WaitFor(Func<bool> condition)
	{
		for (var i = 0; i < 200 && !condition(); i++)
		{
			await Task.Delay(25);
		}

		Assert.True(condition());
	}
}
