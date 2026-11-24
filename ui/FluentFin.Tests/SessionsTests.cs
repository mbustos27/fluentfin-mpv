using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
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
/// 174-sessions (the desktop basics sweep of 1.7.3, BS-M1 to BS-M4 and BS-m1 to BS-m3): what a sign-in leaves on this PC and
/// what the next start does with it. Each test runs the app's own pieces (the settings file in a data folder of its own, the
/// sign-in service, the start's sign-in, the title bar's Sign out, Switch user and Sign in again) against a fake Jellyfin on
/// localhost with two users; a "restart" reads everything again from the data folder. No real server is asked.
/// </summary>
[Collection(SavedSignInCollection.Name)] // SessionInfo's saved sign-in is static
public sealed class SessionsTests : IDisposable
{
	private const string ServerId = "fake-server";
	private static readonly Dictionary<string, (string Password, string Id)> Accounts = new(StringComparer.OrdinalIgnoreCase)
	{
		["alice"] = ("s3cret", "aaaaaaaa000000000000000000000174"),
		["bob"] = ("b0b-pw", "bbbbbbbb000000000000000000000174"),
	};

	private readonly HttpListener _listener = new();
	private readonly List<(string User, int Status)> _signIns = [];
	private readonly TaskCompletionSource _hangUntil = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private volatile int _answer;    // 0: as the accounts say; else this status for every sign-in
	private volatile bool _hang;     // a sign-in is never answered (until the test ends)
	private readonly string _baseUrl;
	private readonly Task _loop;
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-174-sessions", Guid.NewGuid().ToString("N"));

	public SessionsTests()
	{
		_baseUrl = $"http://127.0.0.1:{FreePort()}";
		_listener.Prefixes.Add(_baseUrl + "/");
		_listener.Start();
		_loop = Task.Run(Serve);
		Directory.CreateDirectory(_folder);
		// a settings file of its own: with none, every Settings shares SettingKeys.Servers' one default collection (static)
		File.WriteAllText(Path.Combine(_folder, KnownFolders.SettingsFileName), """{ "Servers": [] }""");
	}

	public void Dispose()
	{
		_hangUntil.TrySetResult();
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

	private static int FreePort()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
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

			_ = Task.Run(() => Answer(context));
		}
	}

	private async Task Answer(HttpListenerContext context)
	{
		try
		{
			var (status, answer) = (200, "{}");
			if (context.Request.Url!.AbsolutePath.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase))
			{
				using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
				using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
				var name = body.RootElement.GetProperty("Username").GetString() ?? "";
				var password = body.RootElement.TryGetProperty("Pw", out var pw) ? pw.GetString() ?? "" : "";
				if (_hang)
				{
					lock (_signIns)
					{
						_signIns.Add((name, 0));
					}

					await _hangUntil.Task;
					context.Response.Abort();
					return;
				}

				status = _answer != 0 ? _answer : Accounts.TryGetValue(name, out var account) && account.Password == password ? 200 : 401;
				lock (_signIns)
				{
					_signIns.Add((name, status));
				}

				if (status == 200)
				{
					var id = Accounts[name].Id;
					answer = $$$"""{"AccessToken":"token-{{{name.ToLowerInvariant()}}}","ServerId":"{{{ServerId}}}","User":{"Id":"{{{id}}}","Name":"{{{name.ToLowerInvariant()}}}","ServerId":"{{{ServerId}}}"}}""";
				}
				else
				{
					answer = "";
				}
			}

			var bytes = Encoding.UTF8.GetBytes(answer);
			context.Response.StatusCode = status;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = bytes.Length;
			await context.Response.OutputStream.WriteAsync(bytes);
			context.Response.Close();
		}
		catch (Exception)
		{
			// the test ended, or the client gave up
		}
	}

	private List<(string User, int Status)> SignIns()
	{
		lock (_signIns)
		{
			return [.. _signIns];
		}
	}

	/// <summary>One run of the app on the data folder: its settings as read from the file, and its sign-in service.</summary>
	private sealed class Run
	{
		public required FluentFin.Core.Services.LocalSettingsService LocalSettings { get; init; }
		public required FluentFin.Core.Settings.Settings Settings { get; init; }
		public required Mock<IJellyfinClient> Client { get; init; }
		public required Mock<ITitleBarViewModel> TitleBar { get; init; }
		public required Mock<INavigationService> Setup { get; init; }
		public required JellyfinAuthenticationService Auth { get; init; }

		public SavedServer Server(string id = ServerId) => Settings.Servers.Single(x => x.Id == id);
	}

	private Run Start()
	{
		SessionInfo.CurrentUserCrendentials = null!; // a new process remembers nothing
		var localSettings = new FluentFin.Core.Services.LocalSettingsService(new KnownFolders(_folder));
		var settings = new FluentFin.Core.Settings.Settings(localSettings);
		settings.ListenToChanges(); // as the start does (DefaultActivationHandler)
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Initialize(It.IsAny<string>(), It.IsAny<AuthenticationResult>())).Returns(Task.CompletedTask);
		client.Setup(x => x.Logout()).Returns(Task.CompletedTask);
		var titleBar = new Mock<ITitleBarViewModel>();
		titleBar.SetupAllProperties();
		var setup = new Mock<INavigationService>();
		var auth = new JellyfinAuthenticationService(client.Object, titleBar.Object, localSettings, setup.Object,
			NullLogger<JellyfinAuthenticationService>.Instance, settings);
		return new Run { LocalSettings = localSettings, Settings = settings, Client = client, TitleBar = titleBar, Setup = setup, Auth = auth };
	}

	/// <summary>The start's automatic sign-in (DefaultActivationHandler's), called directly: a launch's arguments can't be made outside WinUI.</summary>
	private sealed class StartSignIn(Run run)
		: FluentFin.Activation.DefaultActivationHandler(run.Setup.Object, Mock.Of<INavigationService>(), run.Settings, Mock.Of<ILocalSettingsService>(),
			Mock.Of<IPluginManager>(), run.Auth, Mock.Of<IMainWindowViewModel>(), NullLogger<FluentFin.Activation.DefaultActivationHandler>.Instance)
	{
		public Task Go() => HandleInternalAsync(null!);
	}

	/// <summary>A restart: everything read again from the data folder, then the start's own sign-in.</summary>
	private async Task<Run> Restart(TimeSpan? bound = null)
	{
		var run = Start();
		var start = new StartSignIn(run);
		if (bound is { } b)
		{
			start.SignInTimeout = b;
		}

		await start.Go().WaitAsync(TimeSpan.FromSeconds(30));
		return run;
	}

	private SavedServer AddServer(Run run, string id = ServerId, string name = "home", string? url = null)
	{
		var server = new SavedServer { DisplayName = name, PublicUrl = url ?? _baseUrl, LocalUrl = url ?? _baseUrl, Id = id };
		run.Settings.Servers.Add(server);
		return server;
	}

	private static List<(string Page, object? Parameter)> Pages(Run run) =>
		[.. run.Setup.Invocations.Where(i => i.Method.Name == "NavigateTo").Select(i => ((string)i.Arguments[0]!, i.Arguments[1]))];

	private const string Shell = "FluentFin.ViewModels.ShellViewModel";
	private const string ServerList = "FluentFin.Core.ViewModels.SelectServerViewModel";
	private const string SignInPage = "FluentFin.ViewModels.LoginViewModel";

	private static (string Page, object? Parameter) OnlyPage(Run run) => Assert.Single(Pages(run));

	/// <summary>The title bar as the app has it, signed in to <paramref name="server"/> as <paramref name="user"/>.</summary>
	private static TitleBarViewModel TitleBar(Run run, SavedServer server, string user)
	{
		var backend = new Mock<IMpvBackend>();
		backend.Setup(x => x.Events).Returns(Observable.Never<BridgeEvent>());
		backend.Setup(x => x.PlaybackInterrupted).Returns(Observable.Never<MpvInterruptedPlayback>());
		var playback = new MpvPlaybackService(backend.Object, run.Client.Object, NullLogger<MpvPlaybackService>.Instance);
		return new TitleBarViewModel(Mock.Of<INavigationService>(), run.Setup.Object, Mock.Of<INavigationViewService>(), run.Client.Object, playback)
		{
			LibmpvUnavailable = () => null,
			User = new UserDto { Name = user },
			CurrentServer = server,
		};
	}

	// ---- BS-M1: Sign out doesn't stick

	[Fact]
	public async Task SignOut_ThenARestart_IsSignedOut_AndTheSignInPageKeepsTheName()
	{
		var first = Start();
		Assert.Null(await first.Auth.Authenticate(AddServer(first), "alice", "s3cret", remember: true));

		var second = await Restart();
		Assert.Equal(Shell, OnlyPage(second).Page); // remembered: signed in by itself
		await TitleBar(second, second.Server(), "alice").Logout();
		Assert.Equal(ServerList, Pages(second).Last().Page);
		Assert.Null(SessionInfo.CurrentUserCrendentials); // nothing of it in memory either

		var asked = SignIns().Count;
		var third = await Restart();
		var (page, parameter) = OnlyPage(third);
		Assert.Equal(ServerList, page);
		Assert.Null(parameter);                  // a sign-out is no failure
		Assert.Equal(asked, SignIns().Count);    // the forgotten password is never sent
		Assert.Empty(third.Server().Users);
		Assert.Equal("alice", third.Server().LastUser);

		// the server's row opens its sign-in page with the name, "Remember me" ticked, no password
		var signIn = new LoginViewModel(third.Auth, third.Setup.Object, third.LocalSettings);
		await signIn.OnNavigatedTo(third.Server());
		Assert.Equal("alice", signIn.Username);
		Assert.Equal("", signIn.Password);
		Assert.True(signIn.KeepMeLoggedIn);
	}

	/// <summary>A settings file that can't be written doesn't stop Sign out halfway, and its line says what the next start may do.</summary>
	[Fact]
	public async Task SignOut_Completes_WhenTheSavedPasswordCantBeForgotten()
	{
		var first = Start();
		var server = AddServer(first);
		Assert.Null(await first.Auth.Authenticate(server, "alice", "s3cret", remember: true));
		server.Users.CollectionChanged += (_, _) => throw new IOException("The disk is full.");

		await TitleBar(first, server, "alice").Logout().WaitAsync(TimeSpan.FromSeconds(10));

		first.Client.Verify(x => x.Logout(), Times.Once); // the session ended on the server
		var (page, parameter) = Pages(first).Last();
		Assert.Equal(ServerList, page);
		Assert.Equal(TitleBarViewModel.ForgetPasswordFailedNotice, Assert.IsType<SetupNotice>(parameter).Message);
	}

	/// <summary>Switch user isn't Sign out: the saved password stays, and the next start signs in with it.</summary>
	[Fact]
	public async Task SwitchUser_KeepsTheSavedPassword()
	{
		var first = Start();
		Assert.Null(await first.Auth.Authenticate(AddServer(first), "alice", "s3cret", remember: true));
		await TitleBar(first, first.Server(), "alice").SwitchUser();
		var (page, parameter) = Pages(first).Last();
		Assert.Equal(SignInPage, page);

		// the page is for someone else: no name, "Remember me" as it comes, the saved ones in its list
		var signIn = new LoginViewModel(first.Auth, first.Setup.Object, first.LocalSettings);
		await signIn.OnNavigatedTo(parameter!);
		Assert.Equal(("", false, false), (signIn.Username, signIn.KeepMeLoggedIn, signIn.HasNotice));
		Assert.Same(first.Server(), signIn.Server);

		var again = await Restart();
		Assert.Equal(Shell, OnlyPage(again).Page);
		Assert.Equal("alice", SignIns().Last().User);
	}

	// ---- BS-M2: automatic sign-in only with one server and one saved user

	[Fact]
	public async Task TwoUsers_SwitchUser_ThenARestart_SignsInAsTheLastOne()
	{
		var first = Start();
		var server = AddServer(first);
		Assert.Null(await first.Auth.Authenticate(server, "alice", "s3cret", remember: true));
		await TitleBar(first, server, "alice").SwitchUser();
		Assert.Null(await first.Auth.Authenticate(server, "bob", "b0b-pw", remember: true));
		Assert.Equal(2, server.Users.Count);

		for (var i = 0; i < 2; i++)
		{
			var asked = SignIns().Count;
			var again = await Restart();
			Assert.Equal(Shell, OnlyPage(again).Page);
			Assert.Equal(("bob", 200), Assert.Single(SignIns().Skip(asked)));
			Assert.Equal("bob", SessionInfo.CurrentUserCrendentials?.Username);
			Assert.Equal(2, again.Server().Users.Count); // alice is still saved
		}
	}

	[Fact]
	public async Task TwoServers_ARestart_SignsInToTheOneSignedInToLast()
	{
		var first = Start();
		var one = AddServer(first, "server-one", "One");
		var two = AddServer(first, "server-two", "Two");
		Assert.Null(await first.Auth.Authenticate(one, "alice", "s3cret", remember: true));
		Assert.Null(await first.Auth.Authenticate(two, "bob", "b0b-pw", remember: true));

		var asked = SignIns().Count;
		var again = await Restart();
		Assert.Equal(Shell, OnlyPage(again).Page);
		Assert.Equal(("bob", 200), Assert.Single(SignIns().Skip(asked)));
		Assert.Equal("server-two", again.TitleBar.Object.CurrentServer?.Id);

		// and back: One signed in to last
		Assert.Null(await again.Auth.Authenticate(again.Server("server-one"), "alice", "s3cret", remember: true));
		asked = SignIns().Count;
		var third = await Restart();
		Assert.Equal(Shell, OnlyPage(third).Page);
		Assert.Equal(("alice", 200), Assert.Single(SignIns().Skip(asked)));
		Assert.Equal("server-one", third.TitleBar.Object.CurrentServer?.Id);
	}

	/// <summary>A data folder from 1.7.3 (no last sign-in on any server): the old rule, one server with one saved user.</summary>
	[Fact]
	public async Task AFolderFrom173_KeepsTheOldRule()
	{
		var first = Start();
		var server = AddServer(first);
		SavedSignIns.Remember(server, new SavedUser { Username = "alice", Password = "s3cret".Protect(first.LocalSettings.GetEntropyBytes()) });
		Assert.Equal(Shell, OnlyPage(await Restart()).Page);

		var second = Start();
		var both = second.Server();
		both.LastSignIn = null;
		both.LastUser = "";
		SavedSignIns.Remember(both, new SavedUser { Username = "bob", Password = "b0b-pw".Protect(second.LocalSettings.GetEntropyBytes()) });
		var asked = SignIns().Count;
		Assert.Equal(ServerList, OnlyPage(await Restart()).Page); // two saved users and no last one: the server list, as before
		Assert.Equal(asked, SignIns().Count);
	}

	// ---- BS-M3: a failed sign-in at start says nothing

	[Fact]
	public async Task ARefusedPort_AtStart_TheServerListSaysWhy_AndThePasswordIsKept()
	{
		var first = Start();
		var closed = $"http://127.0.0.1:{FreePort()}";
		Assert.Null(await first.Auth.Authenticate(AddServer(first), "alice", "s3cret", remember: true));
		var server = first.Server();
		server.LocalUrl = server.PublicUrl = closed; // the address no longer answers: nothing listens there
		first.Settings.SaveServerDetails();

		var again = await Restart();
		var (page, parameter) = OnlyPage(again);
		Assert.Equal(ServerList, page);
		var line = Assert.IsType<SetupNotice>(parameter).Message;
		Assert.Equal($"Couldn't sign in to home. Can't reach {closed}. Check that the address and port are right and that this PC can reach it (VPN, firewall), then try again.", line);
		Assert.NotEmpty(Assert.Single(again.Server().Users).Password); // not the server's refusal: tried again at the next start
	}

	[Fact]
	public async Task NoAnswer_AtStart_IsGivenUpOn_AndTheServerListSaysWhy()
	{
		var first = Start();
		Assert.Null(await first.Auth.Authenticate(AddServer(first), "alice", "s3cret", remember: true));
		_hang = true;

		var clock = Stopwatch.StartNew();
		var again = await Restart(TimeSpan.FromSeconds(1.5));
		clock.Stop();

		Assert.InRange(clock.Elapsed.TotalSeconds, 1.4, 8); // it waited 22 s (Windows' own connection timeout), or for ever
		var (page, parameter) = OnlyPage(again);
		Assert.Equal(ServerList, page);
		Assert.Equal($"Couldn't sign in to home. {_baseUrl} didn't answer in time. Check the address and port, and that this PC can reach the server (VPN, firewall), then try again.",
			Assert.IsType<SetupNotice>(parameter).Message);
		again.Client.Verify(x => x.Initialize(It.IsAny<string>(), It.IsAny<AuthenticationResult>()), Times.Never);
		Assert.Null(again.TitleBar.Object.User);
		Assert.NotEmpty(Assert.Single(again.Server().Users).Password);
	}

	[Fact]
	public void TheStartWaits10Seconds()
	{
		var run = Start();
		Assert.Equal(TimeSpan.FromSeconds(10), new StartSignIn(run).SignInTimeout);
	}

	// ---- BS-M3 and BS-M4: a refused saved password

	/// <summary>
	/// Jellyfin disables an account after 3 failed sign-ins (5 for an administrator), and only the server's administrator
	/// can turn it on again: a saved password the server refuses is sent once, then forgotten, its name kept; the first start
	/// opens the server's sign-in page with the name and the reason.
	/// </summary>
	[Fact]
	public async Task ARefusedSavedPassword_IsSentOnce_InThreeStarts()
	{
		var first = Start();
		var server = AddServer(first);
		Assert.Null(await first.Auth.Authenticate(server, "alice", "s3cret", remember: true));
		// changed elsewhere: the saved one is stale
		SavedSignIns.Remember(server, new SavedUser { Username = "alice", Password = "an-old-password".Protect(first.LocalSettings.GetEntropyBytes()) });

		var asked = SignIns().Count;
		for (var start = 1; start <= 3; start++)
		{
			var run = await Restart();
			var (page, parameter) = OnlyPage(run);
			if (start == 1)
			{
				Assert.Equal(SignInPage, page);
				var notice = Assert.IsType<SetupNotice>(parameter);
				Assert.Equal("alice", notice.Username);
				Assert.Same(run.Server(), notice.Server);
				Assert.Equal("The saved sign-in to home no longer works. Sign in again.", notice.Message);

				// the page shows the line and the name, with the keyboard for the password
				var signIn = new LoginViewModel(run.Auth, run.Setup.Object, run.LocalSettings);
				await signIn.OnNavigatedTo(parameter!);
				Assert.Equal("alice", signIn.Username);
				Assert.Equal(notice.Message, signIn.Notice);
				Assert.True(signIn.HasNotice);
			}
			else
			{
				Assert.Equal((ServerList, (object?)null), (page, parameter));
			}

			Assert.Empty(run.Server().Users);
			Assert.Equal("alice", run.Server().LastUser);
		}

		Assert.Equal([("alice", 401)], SignIns().Skip(asked)); // one refused sign-in in three starts
	}

	/// <summary>
	/// The review's m1: Jellyfin answers 403 (the account disabled, remote access off, outside its allowed hours) only once the
	/// password is right, and those don't count toward its lockout: the saved password stays, and the server list says the
	/// server doesn't let the account in now (it was forgotten as a wrong one).
	/// </summary>
	[Fact]
	public async Task A403_KeepsTheSavedPassword_AndSaysTheServerDoesntLetTheAccountIn()
	{
		var first = Start();
		Assert.Null(await first.Auth.Authenticate(AddServer(first), "alice", "s3cret", remember: true));
		_answer = 403;

		var asked = SignIns().Count;
		var run = await Restart();
		var (page, parameter) = OnlyPage(run);
		Assert.Equal(ServerList, page);
		Assert.Equal("home doesn't let this account sign in right now. Try again later; if it keeps happening, the server's owner may need to know.",
			Assert.IsType<SetupNotice>(parameter).Message);
		Assert.False(run.Auth.SavedSignInRefused);
		Assert.Equal("alice", Assert.Single(run.Server().Users).Username);
		Assert.Equal([("alice", 403)], SignIns().Skip(asked));

		_answer = 0; // let in again (its hours, remote access back): the next start signs in with the kept password
		Assert.Equal(Shell, OnlyPage(await Restart()).Page);
	}

	[Fact]
	public async Task SignInAgain_WithARefusedPassword_TriesOnce()
	{
		var first = Start();
		var server = AddServer(first);
		Assert.Null(await first.Auth.Authenticate(server, "alice", "s3cret", remember: true));
		var titleBar = TitleBar(first, server, "alice");
		_answer = 401; // the password changed elsewhere, and this sign-in ended

		var asked = SignIns().Count;
		Assert.False(await titleBar.SignInAgain(first.Auth));
		var (page, parameter) = Pages(first).Last();
		Assert.Equal(SignInPage, page);
		var notice = Assert.IsType<SetupNotice>(parameter);
		Assert.Equal(("The saved sign-in to home no longer works. Sign in again.", "alice"), (notice.Message, notice.Username));
		Assert.Same(server, notice.Server);
		Assert.Empty(server.Users);
		Assert.Null(SessionInfo.CurrentUserCrendentials);

		// another page's Sign in again, then a restart: nothing is sent again
		titleBar.User = new UserDto { Name = "alice" };
		Assert.False(await titleBar.SignInAgain(first.Auth));
		Assert.Equal(ServerList, OnlyPage(await Restart()).Page);
		Assert.Single(SignIns().Skip(asked));
	}

	// ---- BS-m1: "Remember me" off doesn't forget an earlier remembered sign-in

	/// <summary>
	/// BS-m1: "Remember me" unticked on the sign-in page for a remembered name forgets that sign-in (it stayed, and the next
	/// start signed in with it). The page ticks the box for a remembered name, so unticking it is the user's choice.
	/// </summary>
	[Fact]
	public async Task RememberMeUnticked_ForARememberedName_ForgetsIt()
	{
		var first = Start();
		var server = AddServer(first);
		Assert.Null(await first.Auth.Authenticate(server, "alice", "s3cret", remember: true));
		await TitleBar(first, server, "alice").SwitchUser();

		var signIn = new LoginViewModel(first.Auth, first.Setup.Object, first.LocalSettings);
		await signIn.OnNavigatedTo(Pages(first).Last().Parameter!);
		signIn.Username = "Alice";
		Assert.True(signIn.KeepMeLoggedIn);    // a remembered name ticks it
		signIn.KeepMeLoggedIn = false;         // the user unticks it for her
		signIn.Password = "s3cret";
		await signIn.Login();

		Assert.Empty(server.Users);
		var asked = SignIns().Count;
		var again = await Restart();
		Assert.Equal(ServerList, OnlyPage(again).Page);
		Assert.Equal(asked, SignIns().Count);
		Assert.Equal("", again.Server().LastUser); // not remembered: not even the name
	}

	/// <summary>
	/// The review's Major: a saved user picked on Switch user's page (empty, "Remember me" as it comes) keeps their saved
	/// password, and the next start signs in as them. It was forgotten: the box was unticked, and the next start signed in as
	/// the other saved user.
	/// </summary>
	[Fact]
	public async Task PickingASavedUser_OnSwitchUsersPage_KeepsTheirSignIn()
	{
		var first = Start();
		var server = AddServer(first);
		Assert.Null(await first.Auth.Authenticate(server, "alice", "s3cret", remember: true));
		Assert.Null(await first.Auth.Authenticate(server, "bob", "b0b-pw", remember: true));
		await TitleBar(first, server, "bob").SwitchUser();

		var signIn = new LoginViewModel(first.Auth, first.Setup.Object, first.LocalSettings);
		await signIn.OnNavigatedTo(Pages(first).Last().Parameter!);
		Assert.Equal(("", false), (signIn.Username, signIn.KeepMeLoggedIn));
		// picked from the list: the page puts in her name and her saved password (LoginPage.AutoSuggestBox_SuggestionChosen)
		signIn.Username = "alice";
		signIn.Password = signIn.Unprotect(server.Users.Single(x => x.Username == "alice").Password);
		await signIn.Login();

		Assert.Equal(["alice", "bob"], server.Users.Select(x => x.Username).Order());
		var asked = SignIns().Count;
		var again = await Restart();
		Assert.Equal(Shell, OnlyPage(again).Page);
		Assert.Equal(("alice", 200), Assert.Single(SignIns().Skip(asked)));
	}

	/// <summary>The review's Major: "Remember me" off means "don't keep this one", never "forget the one kept before".</summary>
	[Fact]
	public async Task ASignInWithoutRememberMe_LeavesTheSavedOnesAsTheyAre()
	{
		var first = Start();
		var server = AddServer(first);
		Assert.Null(await first.Auth.Authenticate(server, "alice", "s3cret", remember: true));

		Assert.Null(await first.Auth.Authenticate(server, "alice", "s3cret", remember: false));

		Assert.Equal("alice", Assert.Single(Start().Server().Users).Username);
	}

	/// <summary>
	/// The review's m2: the start never signs in as someone other than the last one who signed in. A sign-in without
	/// "Remember me" (bob, on this server or another) is the last one: the next start shows the server page, sending nothing
	/// (it signed in as alice, remembered before).
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ASignInWithoutRememberMe_ThenARestart_SignsInAsNobody(bool anotherServer)
	{
		var first = Start();
		var one = AddServer(first, "server-one", "One");
		var two = anotherServer ? AddServer(first, "server-two", "Two") : one;
		Assert.Null(await first.Auth.Authenticate(one, "alice", "s3cret", remember: true));
		Assert.Null(await first.Auth.Authenticate(two, "bob", "b0b-pw", remember: false));

		var asked = SignIns().Count;
		var again = await Restart();
		Assert.Equal((ServerList, (object?)null), OnlyPage(again));
		Assert.Equal(asked, SignIns().Count);
		Assert.Equal("alice", Assert.Single(again.Server("server-one").Users).Username); // still saved, for a sign-in by hand
	}

	// ---- BS-m2: a server removed while signed in to it is saved again by the next sign-in

	[Fact]
	public async Task ASignIn_ToAServerNoLongerSaved_SavesItAgain()
	{
		var first = Start();
		var server = new SavedServer { DisplayName = "home", PublicUrl = _baseUrl, LocalUrl = _baseUrl, Id = ServerId }; // not in the list
		Assert.Null(await first.Auth.Authenticate(server, "alice", "s3cret", remember: true));
		Assert.Same(server, Assert.Single(first.Settings.Servers));

		var again = await Restart();
		Assert.Equal(Shell, OnlyPage(again).Page);
		Assert.Equal("alice", Assert.Single(again.Server().Users).Username);
	}

	// ---- BS-m3: the sign-in page's username is empty even with one saved user

	[Fact]
	public async Task TheSignInPage_OpensWithTheName()
	{
		var run = Start();
		async Task<LoginViewModel> Open(object parameter, string typed = "")
		{
			var vm = new LoginViewModel(run.Auth, run.Setup.Object, run.LocalSettings) { Username = typed };
			await vm.OnNavigatedTo(parameter);
			return vm;
		}

		var one = new SavedServer { DisplayName = "home", PublicUrl = _baseUrl, Id = "a", Users = [new SavedUser { Username = "bob", Password = [1] }] };
		var vm = await Open(one);
		Assert.Equal(("bob", true), (vm.Username, vm.KeepMeLoggedIn)); // its one saved user (it opened empty)
		Assert.Equal("", vm.Password);                                  // the password is typed, or picked from the list

		one.LastUser = "alice";                                           // signed in last, then signed out
		Assert.Equal(("alice", true), ((await Open(one)).Username, (await Open(one)).KeepMeLoggedIn));
		Assert.Equal("carol", (await Open(new SetupNotice("x", one, "carol"))).Username);
		Assert.Equal("dave", (await Open(one, typed: "dave")).Username); // never over a name typed already

		var none = new SavedServer { DisplayName = "empty", PublicUrl = _baseUrl, Id = "b" };
		var empty = await Open(none);
		Assert.Equal(("", false), (empty.Username, empty.KeepMeLoggedIn));
		var two = new SavedServer { DisplayName = "two", PublicUrl = _baseUrl, Id = "c", Users = [new SavedUser { Username = "a", Password = [1] }, new SavedUser { Username = "b", Password = [1] }] };
		Assert.Equal("", (await Open(two)).Username);                    // two saved users, none last: the list offers both
	}
}
