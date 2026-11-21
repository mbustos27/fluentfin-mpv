using System.Collections.ObjectModel;
using System.Security.Cryptography;
using DeviceId;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie (1.2.0): what a saved sign-in from Seaglass (or FluentFin MPV) becomes. The app saves no access token: a saved
/// sign-in is the user name and the password, protected with DPAPI and the entropy kept in LocalSettings.json
/// (<see cref="SavedUser"/>), and every start signs in again with them (JellyfinAuthenticationService.Authenticate). So
/// what must survive the rename is (1) the password, which still decrypts after the data folder copy because
/// LocalSettings.json, the entropy with it, is copied, and (2) the device: the sign-in and the mpv backend send this PC's
/// Windows device id, which no name or folder changes, so the server sees the same device sign in again as Nixie and gets
/// a new token (a server's per-user data, such as Continue Watching, is untouched). The sign-in request itself:
/// FluentFin.Tests' SignInClientTests.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class ClientNameSignInTests : IAsyncLifetime
{
	private static readonly Guid UserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
	private readonly string _local = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));
	private readonly FakeJellyfin _server = new();

	public Task InitializeAsync()
	{
		Directory.CreateDirectory(_local);
		_server.Start();
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		Directory.Delete(_local, true);
		return Task.CompletedTask;
	}

	[Fact]
	public void ASavedSeaglassSignIn_StillDecrypts_AfterTheDataFolderCopy()
	{
		// Seaglass 1.1.x saved a sign-in: the server, the user name, the password protected with its entropy
		var seaglass = new LocalSettingsService(new KnownFolders(Path.Combine(_local, KnownFolders.SeaglassFolderName)));
		var servers = new ObservableCollection<SavedServer>
		{
			new() { DisplayName = "home", PublicUrl = "http://jf:8096", Id = "fake-server", Users = [new SavedUser { Username = "alice", Password = "s3cret".Protect(seaglass.GetEntropyBytes()) }] },
		};
		seaglass.SaveSetting(SettingKeys.Servers, servers);

		var note = KnownFolders.CopyLegacyFolder(_local, out var failed);
		Assert.False(failed, note);

		// Nixie's first start reads the copy: the same server and user, and the password decrypts with the copied entropy
		var nixie = new LocalSettingsService(new KnownFolders(Path.Combine(_local, KnownFolders.FolderName)));
		var server = Assert.Single(nixie.ReadSetting(SettingKeys.Servers));
		var user = Assert.Single(server.Users);
		Assert.Equal("http://jf:8096", server.PublicUrl);
		Assert.Equal("alice", user.Username);
		Assert.Equal(seaglass.GetEntropyBytes(), nixie.GetEntropyBytes());
		Assert.Equal("s3cret", user.Password.Unprotect(nixie.GetEntropyBytes()));

		// without that entropy (a Nixie folder that did not get LocalSettings.json) the saved password is lost
		Assert.ThrowsAny<CryptographicException>(() => user.Password.Unprotect(RandomNumberGenerator.GetBytes(20)));
	}

	[Fact]
	public async Task AfterTheSignIn_TheBackendConnectsAsNixie_OnTheSameDevice()
	{
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Mpv);
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(),
			Mock.Of<IDeviceProfileFactory>(), settings.Object, TestHomeCache.Unused());

		// the session a sign-in (with the saved user name and password) just returned: a new token, made for Nixie
		const string newToken = "token-the-server-gave-nixie";
		await client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = newToken,
			ServerId = "fake-server",
			User = new UserDto { Id = UserId, Name = "alice" },
		});

		// FluentFin's own calls carry that token as it was returned
		var calls = _server.ApiRequests.ToList();
		Assert.Contains(calls, x => x.Path.Equals("/Sessions/Capabilities/Full", StringComparison.OrdinalIgnoreCase));
		foreach (var call in calls)
		{
			Assert.NotNull(call.Authorization);
			Assert.Equal(newToken, Fields(call.Authorization)["Token"]);
			Assert.DoesNotContain("Seaglass", call.Authorization);
		}

		// the backend's connect: Nixie, on this copy's device id (1.5.1 fixes, BH-150-M2: no longer the PC's, which Seaglass and
		// Nixie up to 1.5.0 sent from every copy on the PC), the one FluentFin's own sign-in sent (its calls carry only the token)
		var identity = MpvIdentity.FromSession(settings.Object);
		Assert.NotNull(identity);
		Assert.Equal("Nixie", identity.ClientName);
		Assert.Equal(AppVersion.Current, identity.ClientVersion);
		Assert.Equal(InstallDeviceId.Current, identity.DeviceId);
		Assert.NotEqual(new DeviceIdBuilder().OnWindows(windows => windows.AddWindowsDeviceId()).ToString(), identity.DeviceId);
		Assert.Equal(Environment.MachineName, identity.DeviceName);
		Assert.Equal(newToken, identity.AccessToken);
	}

	/// <summary>The fields of a <c>MediaBrowser Client="...", Device="...", ...</c> header.</summary>
	internal static Dictionary<string, string> Fields(string header) =>
		System.Text.RegularExpressions.Regex.Matches(header, "(\\w+)=\"([^\"]*)\"")
			.ToDictionary(m => m.Groups[1].Value, m => Uri.UnescapeDataString(m.Groups[2].Value));
}
