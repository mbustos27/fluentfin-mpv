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
/// Nixie (1.2.0): a saved login from Seaglass (or FluentFin MPV) still signs in, and the server sees the new name. The
/// fake server records the calls. FluentFin's signed-in requests carry only the saved access token (the Jellyfin SDK's
/// header, <c>MediaBrowser Token="..."</c>): the token is the server's key to the session and the device, so the rename
/// changes nothing there. The name reaches the server with the mpv backend's own requests: <c>connect</c> gives it the
/// client name Nixie and the same device id (the Windows device's, not derived from any name), so the server finds the
/// device it already knows and only updates its app name. A new sign-in (the server page, Quick Connect, a password) says
/// Client="Nixie" too: FluentFin.Tests' SignInClientTests.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class ClientNameSignInTests : IAsyncLifetime
{
	// a token the server gave Seaglass 1.1.x at its sign-in, kept in the saved login
	private const string SavedToken = "seaglass-era-token-0123456789";
	private static readonly Guid UserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

	private readonly FakeJellyfin _server = new();

	public Task InitializeAsync()
	{
		_server.Start();
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task ASavedSeaglassLogin_SignsIn_WithItsToken_AndTheBackendConnectsAsNixie_OnTheSameDevice()
	{
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Mpv);
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(),
			Mock.Of<IDeviceProfileFactory>(), settings.Object, TestHomeCache.Unused());

		// the automatic sign-in: the saved server, user and token, no password
		await client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = SavedToken,
			ServerId = "fake-server",
			User = new UserDto { Id = UserId, Name = "alice" },
		});

		// the start-up calls (capabilities, plugins, views) went out with the saved token, as it was saved
		var calls = _server.ApiRequests.ToList();
		Assert.Contains(calls, x => x.Path.Equals("/Sessions/Capabilities/Full", StringComparison.OrdinalIgnoreCase));
		foreach (var call in calls)
		{
			Assert.NotNull(call.Authorization);
			Assert.StartsWith("MediaBrowser ", call.Authorization);
			Assert.Equal(SavedToken, Fields(call.Authorization)["Token"]);
			Assert.DoesNotContain("Seaglass", call.Authorization);
			if (Fields(call.Authorization).TryGetValue("Client", out var named))
			{
				Assert.Equal("Nixie", named);
			}
		}

		// what the backend is given in connect, and sends the server with the token: Nixie, on the device the server knows
		var expectedDevice = new DeviceIdBuilder().OnWindows(windows => windows.AddWindowsDeviceId()).ToString();
		var identity = MpvIdentity.FromSession(settings.Object);
		Assert.NotNull(identity);
		Assert.Equal("Nixie", identity.ClientName);
		Assert.Equal(AppVersion.Current, identity.ClientVersion);
		Assert.Equal(expectedDevice, identity.DeviceId);
		Assert.Equal(Environment.MachineName, identity.DeviceName);
		Assert.Equal(SavedToken, identity.AccessToken);
	}

	/// <summary>The fields of a <c>MediaBrowser Client="...", Device="...", ...</c> header.</summary>
	internal static Dictionary<string, string> Fields(string header) =>
		System.Text.RegularExpressions.Regex.Matches(header, "(\\w+)=\"([^\"]*)\"")
			.ToDictionary(m => m.Groups[1].Value, m => Uri.UnescapeDataString(m.Groups[2].Value));
}
