using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.6.1 (the 1.6 review's top bar queue): a failed read of the server's libraries left the top bar empty for the
/// session. A good read after a failed one says so (UserViewsChanged), whoever asked, so the bar reads them again.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class UserViewsRetryTests : IAsyncLifetime
{
	private readonly FakeJellyfin _server = new();
	private JellyfinClient _client = null!;

	public async Task InitializeAsync()
	{
		_server.Start();
		_client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		await _client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-user-views-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000001611"), Name = "alice" },
		});
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task AGoodReadAfterAFailedOne_SaysTheViewsChanged_AndOnlyThen()
	{
		var changed = 0;
		_client.UserViewsChanged += (_, _) => changed++;
		_server.Respond("/UserViews", 500, "");

		Assert.Null(await _client.GetUserViewsOrNull());
		Assert.Equal(0, changed);

		var movies = Guid.NewGuid();
		_server.Respond("/UserViews", 200, $$"""{"Items":[{"Id":"{{movies:N}}","Name":"Movies","Type":"CollectionFolder","CollectionType":"movies"}],"TotalRecordCount":1}""");
		var read = await _client.GetUserViews();

		Assert.Equal([movies], read.Select(x => x.Id!.Value));
		Assert.Equal(1, changed);
		// kept and read again later: no failure before it, nothing to say
		Assert.Single(await _client.GetUserViews());
		Assert.Equal(1, changed);
	}
}
