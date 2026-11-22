using System.Text.Json;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.6 (review Major 1): "Save as playlist" makes a playlist of the user's own that only they see. Jellyfin 10.11 and
/// 12.4 make a playlist whose request leaves IsPublic out public, so it is sent, false.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class SavePlaylistRequestTests : IAsyncLifetime
{
	private static readonly Guid UserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000162");
	private readonly FakeJellyfin _server = new();
	private JellyfinClient _client = null!;

	public async Task InitializeAsync()
	{
		_server.Start();
		_client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		await _client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-save-playlist-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = UserId, Name = "alice" },
		});
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task ThePlaylist_IsTheUsersOwn_AndPrivate()
	{
		var made = Guid.NewGuid();
		_server.Respond("/Playlists", 200, $$"""{"Id":"{{made:N}}"}""");
		var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

		var result = await _client.CreatePlaylist("Unfinished series", ids);

		Assert.Equal(made, result.Id);
		var request = Assert.Single(_server.ApiRequests, r => r.Method == "POST" && r.Path.Equals("/Playlists", StringComparison.OrdinalIgnoreCase));
		using var body = JsonDocument.Parse(request.Body);
		Assert.True(body.RootElement.TryGetProperty("IsPublic", out var isPublic), "IsPublic left out: the server makes the playlist public");
		Assert.Equal(JsonValueKind.False, isPublic.ValueKind);
		Assert.Equal(UserId, body.RootElement.GetProperty("UserId").GetGuid());
		Assert.Equal(ids, body.RootElement.GetProperty("Ids").EnumerateArray().Select(x => x.GetGuid()));
	}
}
