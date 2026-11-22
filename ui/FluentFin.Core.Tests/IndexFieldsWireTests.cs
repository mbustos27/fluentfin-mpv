using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.6.1 (the 1.6 review's collections queue): the library index's requests, read on the wire.</summary>
[Collection(SessionInfoCollection.Name)]
public sealed class IndexFieldsWireTests
{
	/// <summary>
	/// 1.6.1 (the 1.6 review: the Fields test only read the constant): what the index pages ask the server for, on the wire:
	/// exactly these fields, by the names Jellyfin's API takes (it drops a name it doesn't know and answers 200).
	/// </summary>
	[Fact]
	public async Task TheIndexPages_AskTheServerForExactlyTheirFields_OnTheWire()
	{
		using var server = new FakeJellyfin();
		server.Start();
		server.Respond("/Items", 200, """{"Items":[],"TotalRecordCount":0}""");
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<Settings.ISettings>(), TestHomeCache.Unused());
		await client.Initialize(server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-index-fields-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000001613"), Name = "alice" },
		});
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };

		await client.GetLibraryIndexPage(library, 0, 10, true, CancellationToken.None);
		await client.GetLibraryPeoplePage(library, 0, 10, true, CancellationToken.None);

		static string[] Fields(FakeRequest r) => [.. (r.Query.GetValues("fields") ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)).Order()];
		var pages = server.ApiRequests.Where(r => r.Path.Equals("/Items", StringComparison.OrdinalIgnoreCase)).ToList();
		Assert.Equal(2, pages.Count);
		Assert.Equal(["DateCreated", "Genres", "ProductionLocations", "ProviderIds", "SortName", "Studios", "Tags"], Fields(pages[0]));
		Assert.Equal(["People"], Fields(pages[1]));
		// every name sent is one of the server's own (its ItemFields), as the SDK spells it
		var known = Enum.GetNames<ItemFields>().ToHashSet(StringComparer.Ordinal);
		Assert.All(pages.SelectMany(Fields), name => Assert.Contains(name, known));
	}
}
