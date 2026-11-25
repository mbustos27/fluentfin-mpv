using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (the review of Save as playlist's owner check): <see cref="JellyfinClient.IsOwnPlaylist"/> through the real
/// client against a fake Jellyfin answering as 10.11 and 12.4 do (live-175-owner-and-place.ps1). A playlist only shared with
/// the user read as theirs: <c>GET /Playlists/{id}/Users/{userId}</c> answers an editing share as it answers the owner.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class OwnPlaylistTests : IAsyncLifetime
{
	private static readonly Guid Me = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
	private static readonly Guid Playlist = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
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
			User = new UserDto { Id = Me, Name = "alice" },
		});
	}

	public Task DisposeAsync()
	{
		_client.StopWebSocket();
		_server.Dispose();
		return Task.CompletedTask;
	}

	private void Answer(int permission, bool canEdit, string shares)
	{
		// the SDK's paths, whichever way it writes the ids
		foreach (var format in new[] { "N", "D" })
		{
			_server.Respond($"/Playlists/{Playlist.ToString(format)}/Users/{Me.ToString(format)}", permission, permission == 200 ? $$"""{"UserId":"{{Me:N}}","CanEdit":{{(canEdit ? "true" : "false")}}}""" : "");
			_server.Respond($"/Playlists/{Playlist.ToString(format)}", 200, $$"""{"OpenAccess":false,"Shares":[{{shares}}],"ItemIds":[]}""");
		}
	}

	[Fact]
	public async Task TheOwnersPlaylist_IsTheirs()
	{
		Answer(200, canEdit: true, shares: "");

		Assert.True(await _client.IsOwnPlaylist(Playlist));
	}

	[Fact]
	public async Task APlaylistSharedWithTheUser_IsNotTheirs_EvenOneTheyMayEdit()
	{
		Answer(200, canEdit: true, shares: $$"""{"UserId":"{{Me:N}}","CanEdit":true}""");
		Assert.False(await _client.IsOwnPlaylist(Playlist));

		Answer(200, canEdit: false, shares: $$"""{"UserId":"{{Me:N}}","CanEdit":false}""");
		Assert.False(await _client.IsOwnPlaylist(Playlist));
	}

	[Fact]
	public async Task AnotherUsersPublicPlaylist_IsNotTheirs_AndNoAnswerIsNotKnown()
	{
		Answer(404, canEdit: false, shares: "");
		Assert.False(await _client.IsOwnPlaylist(Playlist));

		Answer(500, canEdit: false, shares: "");
		Assert.Null(await _client.IsOwnPlaylist(Playlist));
	}
}
