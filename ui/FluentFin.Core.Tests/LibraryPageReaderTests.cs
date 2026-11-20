using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.4.1: the library page reader's ends, and what the review found around it (in memory).</summary>
public sealed class LibraryPageReaderTests
{
	private static readonly BaseItemDto Library = new() { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };

	private static List<BaseItemDto> Titles(int count) => [.. Enumerable.Range(0, count).Select(i => new BaseItemDto { Id = Guid.NewGuid(), Name = $"T{i:D4}" })];

	[Fact]
	public async Task TitlesAddedAfterTheFirstPagesCount_AreReadToo_TheEndIsAShortPage()
	{
		var titles = Titles(150);
		var client = new Mock<IJellyfinClient>();
		// the first page's count says 100 (the server counted before 50 more were added)
		client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns<BaseItemDto, int, int, bool, CancellationToken>((_, start, limit, total, _) =>
				Task.FromResult<BaseItemDtoQueryResult?>(new BaseItemDtoQueryResult { Items = [.. titles.Skip(start).Take(limit)], TotalRecordCount = total ? 100 : 0 }));

		var all = await new LibraryPageReader(client.Object, Library).ReadAllAsync();

		Assert.Equal(titles.Select(t => t.Id), all!.Select(t => t.Id));
	}

	[Fact]
	public async Task AServerThatLeavesOutTheStartIndex_IsNotReadForEver()
	{
		var first = Titles(1000);
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns<BaseItemDto, int, int, bool, CancellationToken>((_, _, limit, _, _) =>
				Task.FromResult<BaseItemDtoQueryResult?>(new BaseItemDtoQueryResult { Items = [.. first.Take(limit)], TotalRecordCount = 0 }));
		var reader = new LibraryPageReader(client.Object, Library);

		await reader.ReadAllAsync().WaitAsync(TimeSpan.FromSeconds(10));

		Assert.True(reader.IsDone);
		Assert.Equal(2, reader.Requests); // the second page began with the first one's title: stopped
	}

	[Fact]
	public async Task AShortPageUnderTheCount_AsksOnceMoreFromWhereItEnded()
	{
		var titles = Titles(300);
		var client = new Mock<IJellyfinClient>();
		// the second page comes short (a proxy's short answer); the server still has the rest
		client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns<BaseItemDto, int, int, bool, CancellationToken>((_, start, limit, total, _) =>
				Task.FromResult<BaseItemDtoQueryResult?>(new BaseItemDtoQueryResult { Items = [.. titles.Skip(start).Take(start == 100 ? 50 : limit)], TotalRecordCount = total ? 300 : 0 }));

		var all = await new LibraryPageReader(client.Object, Library).ReadAllAsync();

		Assert.Equal(titles.Select(t => t.Id), all!.Select(t => t.Id));
	}

	[Theory]
	[InlineData(100, 3.0, 0, 0.0, false, 200, true)] // between the target and slow, the first time: a larger page is tried
	[InlineData(200, 3.1, 100, 3.0, true, 400, true)] // twice the titles, about the same time: the cost is the request's, grow
	[InlineData(200, 5.5, 100, 3.0, true, 200, true)] // twice the titles, nearly twice the time: hold
	[InlineData(1000, 3.0, 500, 2.9, true, 1000, true)] // never over the most
	[InlineData(100, 0.5, 0, 0.0, false, 200, false)] // fast: as NextSize
	[InlineData(400, 8.0, 200, 3.0, true, 200, true)] // slow: as NextSize
	public void BetweenTheTargetAndSlow_PagesGrowOnlyWhileTheTimeDoesNot(int size, double seconds, int lastSize, double lastSeconds, bool tried, int next, bool triedAfter)
	{
		var triedLarger = tried;
		Assert.Equal(next, LibraryPageReader.Grow(size, TimeSpan.FromSeconds(seconds), lastSize, TimeSpan.FromSeconds(lastSeconds), ref triedLarger));
		Assert.Equal(triedAfter, triedLarger);
	}

	[Fact]
	public async Task ALibraryOpenedById_ThatFailed_LoadsOnRetry()
	{
		var id = Guid.NewGuid();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(id)).ReturnsAsync(new BaseItemDto { Id = id, Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies });
		var answers = new Queue<IReadOnlyList<BaseItemDto>?>([null, Titles(3)]);
		client.SetupLibrary(() => answers.Dequeue());
		var vm = new LibraryViewModel(client.Object);

		await vm.OnNavigatedTo(id);
		Assert.True(vm.State.IsFailed);

		await vm.State.RetryCommand.ExecuteAsync(null);
		Assert.True(vm.State.IsLoaded);
		Assert.Equal(3, vm.Items.Count);
	}

	[Fact]
	public async Task LeavingWhileTheLibraryIsLookedUp_ReadsNothingOfIt()
	{
		var id = Guid.NewGuid();
		var lookup = new TaskCompletionSource<BaseItemDto?>();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(id)).Returns(lookup.Task);
		client.SetupLibrary(Titles(500));
		var vm = new LibraryViewModel(client.Object);

		var opening = vm.OnNavigatedTo(id);
		await vm.OnNavigatedFrom();
		lookup.SetResult(new BaseItemDto { Id = id, Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies });
		await opening;

		client.Verify(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task EditorialHome_OpenedAgainWhileALibraryIsRead_SharesTheRead()
	{
		// its own server, user and library: nothing EditorialThemeTests keeps (or clears) meanwhile is this test's
		var (server, user, library) = ("http://share-test-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies });
		var held = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(c => c.BaseUrl).Returns(server);
		client.SetupGet(c => c.UserId).Returns(user);
		client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), 0, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(held.Task);

		var first = EditorialGenreSource.Titles(client.Object, [library]);
		var again = EditorialGenreSource.Titles(client.Object, [library]);
		Assert.True(EditorialGenreSource.IsReading(server, user, library.Id!.Value));
		held.SetResult(new BaseItemDtoQueryResult { Items = Titles(3), TotalRecordCount = 3 });

		Assert.Equal(3, (await first).Count);
		Assert.Equal(3, (await again).Count);
		client.VerifyLibraryLoads(Times.Once);
		// done: the read is not kept on its way (a later visit, once FreshFor is past, reads again)
		Assert.False(EditorialGenreSource.IsReading(server, user, library.Id!.Value));
	}
}
