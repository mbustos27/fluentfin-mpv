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
		_server.Dispose();
		return Task.CompletedTask;
	}

	[Theory]
	[InlineData(200, "true", QuickConnectApproval.Approved)]
	[InlineData(200, "false", QuickConnectApproval.NoSuchCode)]
	[InlineData(404, "", QuickConnectApproval.NoSuchCode)]
	[InlineData(403, "", QuickConnectApproval.Disabled)]
	[InlineData(500, "", QuickConnectApproval.Failed)]
	public async Task ApprovingAQuickConnectCode_SaysWhyItDidNotWork(int status, string body, QuickConnectApproval expected)
	{
		_server.Respond("/QuickConnect/Authorize", status, body);

		Assert.Equal(expected, await _client.ApproveQuickConnect("123456"));
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
