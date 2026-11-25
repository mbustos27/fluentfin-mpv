using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: Settings > Delete User and Delete Server take the saved home page of each login they remove (its watch
/// history, on disk unencrypted) with them, and leave the other logins' pages alone.
/// </summary>
public sealed class SavedLoginRemovalTests : IDisposable
{
	private const string ServerUrl = "https://jellyfin.example";
	private const string ServerId = "6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f";
	private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
	private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-savedlogin-test-" + Guid.NewGuid().ToString("N"));
	private readonly HomeCache _cache;
	private readonly SavedServer _server = new() { Id = ServerId, DisplayName = "Home server", PublicUrl = ServerUrl };
	private readonly SavedServer _otherServer = new() { Id = "7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a", DisplayName = "Other", PublicUrl = "https://other.example" };
	private readonly SettingsViewModel _settings;

	static SavedLoginRemovalTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public SavedLoginRemovalTests()
	{
		_cache = new HomeCache(_folder, new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		_server.Users.Add(new SavedUser { Username = "alice" });
		_server.Users.Add(new SavedUser { Username = "bob" });
		_otherServer.Users.Add(new SavedUser { Username = "alice" });

		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.Servers).Returns(new ObservableCollection<SavedServer>([_server, _otherServer]));
		var backend = new Mock<IMpvBackend>();
		backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		_settings = new SettingsViewModel(settings.Object, backend.Object, Mock.Of<IJellyfinClient>(), _cache);
		// Seaglass 1.1.3: removing asks first; these tests answer yes (SettingsRemoveAsksTests has the other answers)
		_settings.Confirm = _ => Task.FromResult(true);
	}

	public void Dispose()
	{
		_cache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	/// <summary>Signs in as each of these and saves their home page, as a session would.</summary>
	private async Task SavePages(params (string Url, string ServerId, Guid User, string Name)[] logins)
	{
		foreach (var (url, serverId, user, name) in logins)
		{
			_cache.SignedIn(url, user, serverId, name);
			await _cache.Save(url, user, new HomeSnapshot([new BaseItemDto { Id = Guid.NewGuid(), Name = $"{name}'s show", Type = BaseItemDto_Type.Episode }], [], [], []),
				(await _cache.Get(url, user)).ChangesSeen);
		}
	}

	private async Task<bool> HasPage(string url, Guid user)
	{
		using var restarted = new HomeCache(_folder, new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		return (await restarted.Get(url, user)).Snapshot is not null;
	}

	[Fact]
	public async Task DeleteUser_DeletesThatLoginsSavedHomePage()
	{
		await SavePages((ServerUrl, ServerId, Alice, "alice"), (ServerUrl, ServerId, Bob, "bob"), ("https://other.example", _otherServer.Id, Alice, "alice"));

		await _settings.RemoveSavedUser(_server, _server.Users[0]);

		Assert.Equal(["bob"], _server.Users.Select(x => x.Username));
		Assert.False(await HasPage(ServerUrl, Alice));
		Assert.True(await HasPage(ServerUrl, Bob));
		Assert.True(await HasPage("https://other.example", Alice)); // the same name on another server is another login
	}

	[Fact]
	public async Task DeleteServer_DeletesTheSavedHomePageOfEachOfItsLogins()
	{
		await SavePages((ServerUrl, ServerId, Alice, "alice"), (ServerUrl, ServerId, Bob, "bob"), ("https://other.example", _otherServer.Id, Alice, "alice"));

		await _settings.DeleteServerCommand.ExecuteAsync(_server);

		Assert.Equal([_otherServer], _settings.Servers);
		Assert.False(await HasPage(ServerUrl, Alice));
		Assert.False(await HasPage(ServerUrl, Bob));
		Assert.True(await HasPage("https://other.example", Alice));
	}
	/// <summary>
	/// 1.7.5 (the bug hunt, reproduced): Switch user keeps a remembered Quick Connect session alive on the server; Remove server
	/// forgot it without ending it, and then nothing could. Each kept session is ended first (EndKeptSessions), while the
	/// server and its saved sign-ins are still there, then they are forgotten.
	/// </summary>
	[Fact]
	public async Task RemoveServer_EndsTheSessionsKeptForItsSavedSignIns_First()
	{
		var (settings, sessions, servers) = WithSessions();
		var stillThere = false;
		sessions.Setup(x => x.EndKeptSessions(_server, It.IsAny<IEnumerable<SavedUser>>()))
			.Callback(() => stillThere = servers.Contains(_server) && _server.Users.Count == 2).Returns(Task.CompletedTask);

		await settings.DeleteServerCommand.ExecuteAsync(_server);

		sessions.Verify(x => x.EndKeptSessions(_server, It.Is<IEnumerable<SavedUser>>(u => u.Select(y => y.Username).SequenceEqual(new[] { "alice", "bob" }))), Times.Once);
		Assert.True(stillThere, "the kept sessions were ended after the server was forgotten");
		Assert.DoesNotContain(_server, servers);
	}

	/// <summary>1.7.5 (the bug hunt): Remove on one saved sign-in ends its kept session first, then forgets it.</summary>
	[Fact]
	public async Task RemoveSavedUser_EndsItsKeptSession_First()
	{
		var (settings, sessions, _) = WithSessions();
		var alice = _server.Users[0];
		var stillThere = false;
		sessions.Setup(x => x.EndKeptSessions(_server, It.IsAny<IEnumerable<SavedUser>>()))
			.Callback(() => stillThere = _server.Users.Contains(alice)).Returns(Task.CompletedTask);

		Assert.True(await settings.RemoveSavedUser(_server, alice));

		sessions.Verify(x => x.EndKeptSessions(_server, It.Is<IEnumerable<SavedUser>>(u => u.Single() == alice)), Times.Once);
		Assert.True(stillThere, "the kept session was ended after the sign-in was forgotten");
		Assert.DoesNotContain(alice, _server.Users);
	}

	private (SettingsViewModel Settings, Mock<IJellyfinAuthenticationService> Sessions, ObservableCollection<SavedServer> Servers) WithSessions()
	{
		var servers = new ObservableCollection<SavedServer>([_server, _otherServer]);
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.Servers).Returns(servers);
		var backend = new Mock<IMpvBackend>();
		backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		var sessions = new Mock<IJellyfinAuthenticationService>();
		var vm = new SettingsViewModel(settings.Object, backend.Object, Mock.Of<IJellyfinClient>(), _cache, sessions: sessions.Object) { Confirm = _ => Task.FromResult(true) };
		return (vm, sessions, servers);
	}
}