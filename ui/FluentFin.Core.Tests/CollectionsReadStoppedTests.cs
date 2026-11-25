using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5: a first read of the index that stops part way says so ("Nixie couldn't finish reading…") only once the rows of
/// its pages so far are on the Collections page. The read's end didn't wait for a show of those pages still being made (a cold
/// first build), so the line said "its rows so far stay" over a page with no Made by Nixie row yet.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class CollectionsReadStoppedTests : IDisposable
{
	private static readonly Guid MoviesId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000175");
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-readstopped175-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<BaseItemDto> _films;

	static CollectionsReadStoppedTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public CollectionsReadStoppedTests()
	{
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		CollectionNames.RememberBoxSets([]);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000175"));
		// 2,500 films (more than the first page): a franchise every three
		_films = [.. Enumerable.Range(0, 2500).Select(Film)];
		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync("2500:newest");
		// the first page comes; every page after it fails: the read stops part way
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool total, CancellationToken token) =>
			{
				await Task.Delay(15, token);
				return start == 0
					? new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = total ? _films.Count : null }
					: throw new HttpRequestException("the server went away");
			});
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		// a box set of the server's: the page has a row of its own, and its first visit doesn't wait for Made by Nixie's
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult
		{
			Items = [new BaseItemDto { Id = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000175"), Name = "Box", Type = BaseItemDto_Type.BoxSet, ChildCount = 2 }],
		});
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
	}

	public void Dispose()
	{
		Core.Outside.OutsideListsSnapshot.Shared = Core.Outside.OutsideListsSnapshot.Empty;
		CollectionNames.ForgetBoxSets();
		_homeCache.Dispose();
		TestFolder.Delete(_folder);
	}

	private static BaseItemDto Film(int i) => new()
	{
		Id = Guid.Parse($"{i:D8}-0175-0000-0000-000000000000"),
		Name = $"Film {i:D5}",
		SortName = $"film {i:D5}",
		Type = BaseItemDto_Type.Movie,
		ProductionYear = 1950 + (i % 70),
		CommunityRating = 5 + (i % 5),
		ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = $"{i}", ["TmdbCollection"] = $"{i / 3}" } },
		ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = $"p{i}" } },
		UserData = new UserItemDataDto { Played = false },
	};

	[Fact]
	public async Task AReadThatStopped_SaysSo_OnlyOnceTheRowsOfItsPagesAreShown()
	{
		using var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		using var ui = new UiThread();
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var vm = new CollectionsViewModel(_client.Object, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>()), index,
			new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>()))
		{
			// the show of the read's pages so far (its first page) is held until the read has stopped
			Making = snapshot => snapshot.Reading is not null && held.TrySetResult() ? release.Task : Task.CompletedTask,
		};
		// what the page had when the line came to say the read stopped
		var rowsWhenSaid = new List<string>();
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(CollectionsViewModel.IsReadStopped) && vm.IsReadStopped)
			{
				rowsWhenSaid.Add(string.Join(", ", vm.Rows.Select(r => r.Title)));
			}
		};
		try
		{
			await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
			await held.Task.WaitAsync(TimeSpan.FromSeconds(20));
			await index.Settled.WaitAsync(TimeSpan.FromSeconds(20)); // the read has stopped
			Assert.Null(index.Current);
			await Task.Delay(300);
			await ui.Invoke(() => 0); // its end has run on the page's thread
			Assert.Empty(rowsWhenSaid); // not said while its rows are still being made

			release.SetResult();
			await vm.Reading.WaitAsync(TimeSpan.FromSeconds(20));

			var said = Assert.Single(rowsWhenSaid);
			Assert.Contains("Franchises", said, StringComparison.Ordinal); // ("The server's collections" alone before the fix)
			Assert.Equal((false, true, CollectionsViewModel.ReadStoppedText), await ui.Invoke(() => (vm.IsReadingIndex, vm.IsReadStopped, vm.ReadingText)));
		}
		finally
		{
			release.TrySetResult();
			await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));
			await ui.Run(vm.OnNavigatedFrom).WaitAsync(TimeSpan.FromSeconds(5));
			await ui.Invoke(() => 0).WaitAsync(TimeSpan.FromSeconds(5));
		}
	}
}
