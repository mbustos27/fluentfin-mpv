using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.9 (stream text): the summaries' requests, read on the wire.</summary>
[Collection(SessionInfoCollection.Name)]
public sealed class TextPassWireTests
{
	/// <summary>
	/// A library's summaries and the titles' by id ask exactly Overview and Taglines (by the names Jellyfin's API takes: it
	/// drops a name it doesn't know and answers 200), lean (no images, no user data), and only the library's kind.
	/// </summary>
	[Fact]
	public async Task TheSummariesRequests_AskOnlyOverviewAndTaglines_Lean_OnTheWire()
	{
		using var server = new FakeJellyfin();
		server.Start();
		server.Respond("/Items", 200, """{"Items":[],"TotalRecordCount":0}""");
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<Settings.ISettings>(), TestHomeCache.Unused());
		await client.Initialize(server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-text-pass-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000001900"), Name = "alice" },
		});
		var shows = new BaseItemDto { Id = Guid.NewGuid(), Name = "Shows", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Tvshows };
		Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];

		await client.GetLibraryTextPage(shows, 1000, 1000, false, CancellationToken.None);
		Assert.NotNull(await client.GetTextByIds(ids, CancellationToken.None));
		Assert.Empty((await client.GetTextByIds([], CancellationToken.None))!); // none asked: no request

		static string[] Values(FakeRequest r, string name) => [.. (r.Query.GetValues(name) ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)).Order()];
		var asked = server.ApiRequests.Where(r => r.Path.Equals("/Items", StringComparison.OrdinalIgnoreCase)).ToList();
		Assert.Equal(2, asked.Count);
		Assert.All(asked, r =>
		{
			Assert.Equal(["Overview", "Taglines"], Values(r, "fields"));
			Assert.Equal(["false"], Values(r, "enableImages"));
			Assert.Equal(["false"], Values(r, "enableUserData"));
			Assert.All(Values(r, "fields"), name => Assert.True(Enum.TryParse<ItemFields>(name, out _), name));
		});
		Assert.Equal(["Series"], Values(asked[0], "includeItemTypes"));
		Assert.Equal([shows.Id!.Value.ToString("N")], Values(asked[0], "parentId").Select(x => Guid.Parse(x).ToString("N")));
		Assert.Equal(["1000"], Values(asked[0], "startIndex"));
		Assert.Equal(ids.Select(x => x.ToString("N")).Order(), Values(asked[1], "ids").Select(x => Guid.Parse(x).ToString("N")).Order());
		Assert.Empty(Values(asked[1], "parentId"));
	}
}
