using System.Text.Json;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.6.1 (the 1.6 review's collections queue): the box sets and playlists were one request of 2,000, the first by
/// name; a server with more lost the rest on the Collections page, in Home's rows and in the tidy names' rule. Every page
/// is read now. (Serial: the read is remembered for the tidy names, a static list.)
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class SetsPagingTests : IAsyncLifetime
{
	private readonly ConcurrentFakeJellyfin _server = new();
	private JellyfinClient _client = null!;

	public async Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("/UserViews", 200, """{"Items":[],"TotalRecordCount":0}""");
		_client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		await _client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-sets-paging-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000001612"), Name = "alice" },
		});
	}

	public Task DisposeAsync()
	{
		CollectionNames.RememberBoxSets([]);
		_server.Dispose();
		return Task.CompletedTask;
	}

	private static readonly List<(Guid Id, string Name)> Sets = [.. Enumerable.Range(0, 4_100).Select(i => (Guid.NewGuid(), $"Set {i:D5}"))];

	/// <summary>The server's pages: StartIndex and Limit as Jellyfin reads them; <paramref name="failFrom"/> answers 500 from there.</summary>
	private void ServeSets(int failFrom = int.MaxValue) => _server.RespondWith("/Items", request =>
	{
		var start = int.Parse(request.Query["StartIndex"] ?? "0");
		var limit = int.Parse(request.Query["Limit"] ?? "100000");
		if (start >= failFrom)
		{
			return (500, "");
		}

		var page = Sets.Skip(start).Take(limit).Select(s => new { Id = s.Id.ToString("N"), s.Name, Type = "BoxSet" });
		return (200, JsonSerializer.Serialize(new { Items = page, TotalRecordCount = Sets.Count, StartIndex = start }));
	});

	[Fact]
	public async Task MoreThan2000BoxSets_AreReadPageAfterPage_InOrder()
	{
		ServeSets();

		var read = await _client.GetBoxSets();

		Assert.Equal(Sets.Select(s => s.Id), read!.Items!.Select(x => x.Id!.Value));
		Assert.Equal(4_100, read.TotalRecordCount);
		var asked = _server.ApiRequests.Where(r => r.Path == "/Items").Select(r => (Start: r.Query["StartIndex"], Limit: r.Query["Limit"])).ToList();
		Assert.Equal([("0", "2000"), ("2000", "2000"), ("4000", "2000")], asked);
	}

	[Fact]
	public async Task APageThatFails_KeepsWhatWasRead_AndAFirstPageThatFails_IsNoList()
	{
		ServeSets(failFrom: 2000);
		var read = await _client.GetPlaylists();
		Assert.Equal(2000, read!.Items!.Count);

		ServeSets(failFrom: 0);
		Assert.Null(await _client.GetBoxSets());
	}
}
