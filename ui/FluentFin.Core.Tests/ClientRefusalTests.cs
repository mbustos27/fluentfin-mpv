using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4 (review): the server's refusals reach the user, through the real client against a fake Jellyfin. A server
/// with Quick Connect off answers 403 (it read as "That code didn't work"); a SyncPlay "New group" an account may not start
/// is refused (the refusal was swallowed, so the button did nothing).
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class ClientRefusalTests : IAsyncLifetime
{
	private readonly FakeJellyfin _server = new();
	private JellyfinClient _client = null!;

	public async Task InitializeAsync()
	{
		_server.Start();
		_client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		await _client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.NewGuid(), Name = "alice" },
		});
	}

	public Task DisposeAsync()
	{
		// first: a client left connected kept reconnecting, and could reach the next test's fake server on the same port
		_client.StopWebSocket();
		_server.Dispose();
		return Task.CompletedTask;
	}

	[Theory]
	[InlineData(200, "true", "true", QuickConnectApproval.Approved)]
	[InlineData(200, "false", "true", QuickConnectApproval.NoSuchCode)]
	[InlineData(404, "", "true", QuickConnectApproval.NoSuchCode)]
	// review: Quick Connect off answers 401 (as the harness's server does), not only 403: the server says whether it is on
	[InlineData(401, "", "false", QuickConnectApproval.Disabled)]
	[InlineData(403, "", "false", QuickConnectApproval.Disabled)]
	[InlineData(401, "", "true", QuickConnectApproval.Failed)]
	[InlineData(500, "", "true", QuickConnectApproval.Failed)]
	public async Task ApprovingAQuickConnectCode_SaysWhyItDidNotWork(int status, string body, string enabled, QuickConnectApproval expected)
	{
		_server.Respond("/QuickConnect/Authorize", status, body);
		_server.Respond("/QuickConnect/Enabled", 200, enabled);

		Assert.Equal(expected, await _client.ApproveQuickConnect("123456"));
	}

	[Fact]
	public void TheServersLibraryChange_TellsTheTopBarToReadTheLibrariesAgain()
	{
		// review: the library tabs were read once per sign-in
		var told = 0;
		_client.UserViewsChanged += (_, _) => told++;

		_client.OnLibraryChanged(new Jellyfin.Sdk.Generated.Models.LibraryChangedMessage());

		Assert.Equal(1, told);
	}

	[Theory]
	[InlineData(204, null)]
	[InlineData(403, JellyfinClient.SyncPlayRefused)]
	[InlineData(401, JellyfinClient.SyncPlayRefused)]
	public async Task StartingASyncPlayGroup_SaysTheServersRefusal(int status, string? refusal)
	{
		_server.Respond("/SyncPlay/New", status, "");

		Assert.Equal(refusal, await _client.StartSyncPlayGroup());
	}

	[Fact]
	public async Task StartingASyncPlayGroup_WithNoAnswer_SaysSoInPlainWords()
	{
		_server.Respond("/SyncPlay/New", 500, "");

		Assert.StartsWith("Couldn't start a group on", await _client.StartSyncPlayGroup(), StringComparison.Ordinal);
	}
}
