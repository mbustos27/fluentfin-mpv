using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.3 (UX review, prioritised fix 2): Settings' Remove server and Remove (a saved sign-in) ask first, in a prompt
/// that names the server or the user; nothing goes on a No, or when nobody can be asked.
/// </summary>
public sealed class SettingsRemoveAsksTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-removeasks-test-" + Guid.NewGuid().ToString("N"));
	private readonly HomeCache _cache;
	private readonly SavedServer _server = new() { Id = "6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f", DisplayName = "Living room", PublicUrl = "https://jellyfin.example" };
	private readonly ObservableCollection<SavedServer> _servers;
	private readonly SettingsViewModel _settings;
	private readonly List<ConfirmPrompt> _asked = [];

	static SettingsRemoveAsksTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public SettingsRemoveAsksTests()
	{
		_cache = new HomeCache(_folder, new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		_server.Users.Add(new SavedUser { Username = "alice" });
		_server.Users.Add(new SavedUser { Username = "bob" });
		_servers = [_server];

		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.Servers).Returns(_servers);
		var backend = new Mock<IMpvBackend>();
		backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		_settings = new SettingsViewModel(settings.Object, backend.Object, Mock.Of<IJellyfinClient>(), _cache);
	}

	public void Dispose()
	{
		_cache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private void Answer(bool yes) => _settings.Confirm = prompt =>
	{
		_asked.Add(prompt);
		return Task.FromResult(yes);
	};

	[Fact]
	public async Task RemoveServer_AsksNamingIt_AndANo_KeepsEverything()
	{
		Answer(yes: false);

		await _settings.DeleteServerCommand.ExecuteAsync(_server);

		var prompt = Assert.Single(_asked);
		Assert.Equal("Remove Living room?", prompt.Title);
		Assert.Contains("2 saved sign-ins", prompt.Message);
		Assert.Contains("Nothing changes on the server", prompt.Message);
		Assert.Equal("Remove", prompt.ConfirmText);
		Assert.Equal([_server], _servers);
		Assert.Equal(["alice", "bob"], _server.Users.Select(x => x.Username));
	}

	[Fact]
	public async Task RemoveServer_AYes_RemovesIt()
	{
		Answer(yes: true);

		await _settings.DeleteServerCommand.ExecuteAsync(_server);

		Assert.Single(_asked);
		Assert.Empty(_servers);
	}

	/// <summary>
	/// 174-sessions (BS-m2): Remove server on the server signed in to signs out of it (Nixie stayed signed in to a server it no
	/// longer listed, and the next start showed none), and the prompt says so first. Another server's Remove signs nobody out.
	/// </summary>
	[Theory]
	[InlineData(true, true)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public async Task RemoveServer_TheOneSignedInTo_SignsOutOfItFirst(bool inUse, bool yes)
	{
		var titleBar = new Mock<ITitleBarViewModel>();
		titleBar.SetupGet(x => x.User).Returns(new Jellyfin.Sdk.Generated.Models.UserDto { Name = "alice" });
		// the session's server as the start finds it: the list's own, or (signed in through the server page) one with its id
		titleBar.SetupGet(x => x.CurrentServer).Returns(inUse ? new SavedServer { Id = _server.Id, DisplayName = "Living room" } : new SavedServer { Id = "another" });
		titleBar.Setup(x => x.Logout()).Returns(Task.CompletedTask);
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.Servers).Returns(_servers);
		var backend = new Mock<IMpvBackend>();
		backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		var vm = new SettingsViewModel(settings.Object, backend.Object, Mock.Of<IJellyfinClient>(), _cache, titleBar: titleBar.Object);
		ConfirmPrompt? asked = null;
		vm.Confirm = prompt => { asked = prompt; return Task.FromResult(yes); };

		await vm.DeleteServerCommand.ExecuteAsync(_server);

		Assert.NotNull(asked);
		Assert.Equal(inUse, asked.Message.StartsWith($"{AppIdentity.Name} signs you out of it, then forgets this server and its 2 saved sign-ins", StringComparison.Ordinal));
		titleBar.Verify(x => x.Logout(), inUse && yes ? Times.Once() : Times.Never());
		Assert.Equal(yes ? 0 : 1, _servers.Count);
	}

	[Fact]
	public async Task RemoveUser_AsksNamingTheUserAndTheServer_AndANo_KeepsThem()
	{
		Answer(yes: false);

		Assert.False(await _settings.RemoveSavedUser(_server, _server.Users[0]));

		var prompt = Assert.Single(_asked);
		Assert.Equal("Remove alice from Living room?", prompt.Title);
		Assert.Contains("alice", prompt.Message);
		Assert.Equal(["alice", "bob"], _server.Users.Select(x => x.Username));
	}

	[Fact]
	public async Task RemoveUser_AYes_RemovesOnlyThatOne()
	{
		Answer(yes: true);

		Assert.True(await _settings.RemoveSavedUser(_server, _server.Users[0]));

		Assert.Equal(["bob"], _server.Users.Select(x => x.Username));
	}

	/// <summary>
	/// 174-sessions (the review's m3): a removed saved sign-in takes its name too: the server's sign-in page opened with it,
	/// "Remember me" ticked. Another user's name stays.
	/// </summary>
	[Fact]
	public async Task RemoveUser_ForgetsItsName_ForTheSignInPage()
	{
		Answer(yes: true);
		_server.LastUser = "Alice";

		Assert.True(await _settings.RemoveSavedUser(_server, _server.Users[1])); // bob
		Assert.Equal("Alice", _server.LastUser);
		Assert.True(await _settings.RemoveSavedUser(_server, _server.Users[0])); // alice

		Assert.Equal("", _server.LastUser);
		Assert.Equal("", SavedSignIns.UsernameFor(_server));
		Assert.False(SavedSignIns.WasRemembered(_server, "alice"));
	}

	[Fact]
	public async Task WithNobodyToAsk_NothingIsRemoved()
	{
		_settings.Confirm = null;

		await _settings.DeleteServerCommand.ExecuteAsync(_server);
		Assert.False(await _settings.RemoveSavedUser(_server, _server.Users[0]));

		Assert.Equal([_server], _servers);
		Assert.Equal(2, _server.Users.Count);
	}

	[Fact]
	public void AServerWithoutAName_IsNamedByItsAddress()
	{
		var unnamed = new SavedServer { PublicUrl = "https://media.example" };

		Assert.Equal("Remove https://media.example?", SettingsViewModel.RemoveServerPrompt(unnamed).Title);
		Assert.Equal("Remove this server?", SettingsViewModel.RemoveServerPrompt(new SavedServer()).Title);
		Assert.Contains("the sign-in saved for carol", SettingsViewModel.RemoveServerPrompt(new SavedServer { DisplayName = "x", Users = [new SavedUser { Username = "carol" }] }).Message);
	}

	[Fact]
	public async Task ForgetAllSkipRules_AsksFirst()
	{
		// button review A39: "Forget learned intros" deleted them at once; 1.6 skip: its successor, Forget all, asks too
		var settings = new Mock<ISettings>();
		var backend = new Mock<IMpvBackend>();
		backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		var vm = new SettingsViewModel(settings.Object, backend.Object, Mock.Of<IJellyfinClient>(), _cache, new KnownFolders(_folder));
		ConfirmPrompt? asked = null;
		vm.Confirm = prompt => { asked = prompt; return Task.FromResult(false); };

		await vm.ForgetAllSkipRulesCommand.ExecuteAsync(null);

		Assert.Equal(SettingsViewModel.ForgetAllSkipRulesPrompt, asked);
		Assert.Equal("Forget all skip rules?", asked!.Title);
		// a no forgets nothing: the backend is not even asked
		backend.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<System.Text.Json.Nodes.JsonObject?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public void TheRemoveButtons_AreNamedForWhatTheyRemove()
	{
		Assert.Equal("Remove the sign-in for alice", SettingsViewModel.RemoveUserLabel("alice"));
		Assert.Equal("Remove server Living room", SettingsViewModel.RemoveServerLabel("Living room"));
		Assert.Equal("Remove server", SettingsViewModel.RemoveServerLabel(""));
	}
}
