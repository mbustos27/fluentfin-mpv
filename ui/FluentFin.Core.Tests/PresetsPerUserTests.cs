using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

[Collection(CollectionNamesSerial.Name)]
/// <summary>
/// Nixie 1.6 (review Major 2, the reviewer's probe): a library's Nixie presets are the signed-in user's own: after A signs
/// out and B signs in on the same server, B never gets A's "Highly rated" (made of A's watched state), nor when B simply
/// signs in after A.
/// </summary>
public sealed class PresetsPerUserTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-presets-user-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private Guid _user = Guid.NewGuid();

	public PresetsPerUserTests()
	{
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(() => _user);
	}

	public void Dispose()
	{
		_homeCache.Dispose();
		try { Directory.Delete(_folder, true); } catch { }
	}

	private static BaseItemDto Film(string name, double rating, bool played) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, ProductionYear = 2001, CommunityRating = (float)rating,
		RunTimeTicks = TimeSpan.FromMinutes(120).Ticks, UserData = new UserItemDataDto { Played = played },
	};

	[Fact]
	public async Task UserAsPresets_AreNotServedToUserB_AfterASignsOut()
	{
		var moviesId = Guid.NewGuid();
		var movies = new BaseItemDto { Id = moviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var films = new List<BaseItemDto> { Film("A1", 8.0, false), Film("A2", 8.1, false), Film("A3", 8.2, false), Film("A4", 8.3, false) };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int s, int l, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. films.Skip(s).Take(l)], TotalRecordCount = films.Count });
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		using var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		var store = new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "Smart"), new CoreListLogger<SmartCollectionStore>());
		var presets = new NixieLibraryPresets(index, _client.Object, store);

		await index.GetAsync();
		await presets.Making;
		Assert.Contains(presets.PresetsFor(movies, films), p => p.Kind == SmartCollectionKind.HighlyRated);

		// user A signs out; user B signs in on the same server; B's index is not read (yet, or it fails)
		var first = _user;
		await _homeCache.Forget("http://server", first);
		await Task.Delay(300);
		_user = Guid.NewGuid();
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync((IReadOnlyList<BaseItemDto>?)null);
		Assert.Null(index.Current);

		var forB = presets.PresetsFor(movies, films);
		var forBAsync = await presets.PresetsForAsync(movies, films, CancellationToken.None);
		Assert.True(forB.Count == 0 && forBAsync.Count == 0, $"B got A's presets: {string.Join(", ", forB.Select(p => p.Label))}");
	}

	[Fact]
	public async Task UserAsPresets_AreNotServedToUserB_WhoSignsInAfterA()
	{
		var moviesId = Guid.NewGuid();
		var movies = new BaseItemDto { Id = moviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var films = new List<BaseItemDto> { Film("A1", 8.0, false), Film("A2", 8.1, false), Film("A3", 8.2, false) };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int s, int l, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. films.Skip(s).Take(l)], TotalRecordCount = films.Count });
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		using var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		var store = new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "Smart"), new CoreListLogger<SmartCollectionStore>());
		var presets = new NixieLibraryPresets(index, _client.Object, store);
		await index.GetAsync();
		await presets.Making;
		Assert.NotEmpty(presets.PresetsFor(movies, films));
		Assert.NotNull(presets.LabelOf("nixie:pick:highly-rated"));

		_user = Guid.NewGuid(); // B, on the same PC and server, A still saved
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync((IReadOnlyList<BaseItemDto>?)null);

		Assert.Empty(presets.PresetsFor(movies, films));
		Assert.Empty(await presets.PresetsForAsync(movies, films, CancellationToken.None));
		Assert.Null(presets.LabelOf("nixie:pick:highly-rated"));
	}
}
