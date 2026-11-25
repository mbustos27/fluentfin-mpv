using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.4 (the owner: "going back to the collection from a movie in the collection makes u wait for the collection to load
/// again"): the pages just left are kept for Back and Forward (PageKeep), never another session's, a few at most, only while the
/// history reaches them; a kept page shows as it was and is refreshed behind only when something changed, keeping its cards.
/// </summary>
public sealed class PageKeepTests
{
	private sealed record Entry(Type Page, object? Parameter);

	private sealed class CollectionPage;

	private sealed class MoviePage;

	private sealed class HomePage;

	private static PageKeep<Entry> Keep(TimeProvider? time = null) => new(x => x.Page, x => x.Parameter, time);

	private const string Alice = "http://server|alice";

	[Fact]
	public void APageLeft_IsGivenBackToItsEntry_Once()
	{
		var keep = Keep();
		var set = new BaseItemDto { Id = Guid.NewGuid() };
		var entry = new Entry(typeof(CollectionPage), set);
		var vm = new object();
		keep.Keep(entry, vm, Alice, changes: 3);

		var back = keep.Take(entry, Alice, changes: 3);
		Assert.Same(vm, back?.ViewModel);
		Assert.False(back?.Stale);
		// taken: it is the page's again (a second Back to the same entry is a page left anew, kept anew)
		Assert.Null(keep.Take(entry, Alice, changes: 3));
	}

	[Fact]
	public void AnEntryWrappedAgain_IsFoundByItsPageAndParameter()
	{
		var keep = Keep();
		var set = new BaseItemDto { Id = Guid.NewGuid() };
		var vm = new object();
		keep.Keep(new Entry(typeof(CollectionPage), set), vm, Alice, 0);

		// the same place in the history (another wrapper of the entry): the same page with the very same parameter
		Assert.Same(vm, keep.Take(new Entry(typeof(CollectionPage), set), Alice, 0)?.ViewModel);
		// never another collection's: another parameter (even with the same id) is another place
		keep.Keep(new Entry(typeof(CollectionPage), set), vm, Alice, 0);
		Assert.Null(keep.Take(new Entry(typeof(CollectionPage), new BaseItemDto { Id = set.Id }), Alice, 0));
	}

	[Fact]
	public void AnotherUserOrServer_IsNeverGivenAKeptPage()
	{
		var keep = Keep();
		var entry = new Entry(typeof(CollectionPage), new BaseItemDto { Id = Guid.NewGuid() });
		keep.Keep(entry, new object(), Alice, 0);

		Assert.Null(keep.Take(entry, "http://server|bob", 0));
		// and it is gone, not waiting for Alice's return on another sign-in
		Assert.Equal(0, keep.Count);
		keep.Keep(entry, new object(), Alice, 0);
		Assert.Null(keep.Take(entry, "http://other|alice", 0));
	}

	[Fact]
	public void SomethingChanged_OrKeptLong_MakesItStale()
	{
		var time = new ManualClock();
		var keep = Keep(time);
		var a = new Entry(typeof(CollectionPage), new BaseItemDto { Id = Guid.NewGuid() });
		var b = new Entry(typeof(MoviePage), new BaseItemDto { Id = Guid.NewGuid() });
		keep.Keep(a, new object(), Alice, changes: 5);
		keep.Keep(b, new object(), Alice, changes: 5);

		// a film marked watched on the page Back leaves (the change count moved): refreshed behind
		Assert.True(keep.Take(a, Alice, changes: 6)?.Stale);
		// kept longer than Home's saved page stays fresh: refreshed behind too
		time.Advance(PageKeep<Entry>.FreshFor + TimeSpan.FromSeconds(1));
		Assert.True(keep.Take(b, Alice, changes: 5)?.Stale);
	}

	[Fact]
	public void OnlyAFewPagesAreKept_TheOldestGoesFirst()
	{
		var keep = Keep();
		var entries = Enumerable.Range(0, PageKeep<Entry>.Depth + 2).Select(i => new Entry(typeof(MoviePage), new BaseItemDto { Id = Guid.NewGuid() })).ToList();
		var vms = entries.Select(_ => new object()).ToList();
		List<object> dropped = [];
		for (var i = 0; i < entries.Count; i++)
		{
			dropped.AddRange(keep.Keep(entries[i], vms[i], Alice, 0));
		}

		Assert.Equal(PageKeep<Entry>.Depth, keep.Count);
		Assert.Equal([vms[0], vms[1]], dropped);
		Assert.Null(keep.Take(entries[0], Alice, 0));
		Assert.Same(vms[^1], keep.Take(entries[^1], Alice, 0)?.ViewModel);
	}

	[Fact]
	public void WhatTheHistoryNoLongerReaches_IsLetGo()
	{
		var keep = Keep();
		var home = new Entry(typeof(HomePage), null);
		var set = new Entry(typeof(CollectionPage), new BaseItemDto { Id = Guid.NewGuid() });
		var film = new Entry(typeof(MoviePage), new BaseItemDto { Id = Guid.NewGuid() });
		keep.Keep(home, new object(), Alice, 0);
		keep.Keep(set, new object(), Alice, 0);
		keep.Keep(film, new object(), Alice, 0);

		// R loaded the collection again (its old entry replaced), a new page cleared Forward (the film)
		Assert.Equal(2, keep.Retain([home]).Count);
		Assert.Equal(1, keep.Count);
		Assert.True(keep.Has(home));
		// a sign-out, a theme switch, a new shell: everything goes
		Assert.Single(keep.Clear());
		Assert.Equal(0, keep.Count);
	}

	// ---- a kept page's refresh behind -----------------------------------------------------------------------------------------

	[Fact]
	public async Task ARefreshBehind_ShowsNoLoadingState_AndAFailureKeepsThePage()
	{
		var state = new LoadState("this collection");
		await state.RunAsync(() => Task.FromResult(LoadOutcome.Loaded));
		List<LoadStatus> seen = [];
		state.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(LoadState.Status))
			{
				seen.Add(state.Status);
			}
		};

		var gate = new TaskCompletionSource<LoadOutcome>();
		var behind = state.RefreshBehindAsync(() => gate.Task);
		Assert.Equal(LoadStatus.Loaded, state.Status);
		gate.SetResult(LoadMessages.CouldNotLoad("this collection"));
		Assert.False(await behind);
		Assert.Equal(LoadStatus.Loaded, state.Status);
		Assert.True(await state.RefreshBehindAsync(() => throw new HttpRequestException("gone")) is false);
		Assert.Equal(LoadStatus.Loaded, state.Status);
		Assert.Empty(seen); // never Loading, never Failed: the page Back showed stays as it is
	}

	private static List<BaseItemDto> Films(int count) =>
		[.. Enumerable.Range(1, count).Select(i => new BaseItemDto
		{
			Id = Guid.NewGuid(),
			Name = $"Film {i}",
			Type = BaseItemDto_Type.Movie,
			ProductionYear = 1990 + i,
			PremiereDate = new DateTimeOffset(1990 + i, 1, 1, 0, 0, 0, TimeSpan.Zero),
			UserData = new UserItemDataDto { Played = false },
		})];

	private static BaseItemDto Copy(BaseItemDto film, bool played) => new()
	{
		Id = film.Id,
		Name = film.Name,
		Type = film.Type,
		ProductionYear = film.ProductionYear,
		PremiereDate = film.PremiereDate,
		UserData = new UserItemDataDto { Played = played },
	};

	private static (CollectionViewModel Vm, Mock<IJellyfinClient> Client) Collection(BaseItemDto set, Func<List<BaseItemDto>> films)
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(set.Id!.Value)).ReturnsAsync(set);
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = films() });
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		return (new CollectionViewModel(client.Object, commands), client);
	}

	[Fact]
	public async Task ACollectionComingBack_Unchanged_AsksTheServerNothing()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Name = "Copper and Glass", Type = BaseItemDto_Type.BoxSet };
		var films = Films(6);
		var (vm, client) = Collection(set, () => films);
		await vm.OnNavigatedTo(set);
		Assert.True(vm.CanKeep);
		client.Invocations.Clear();
		var cards = vm.Entries;

		await ((IKeptOnBack)vm).Returned(set, stale: false);

		Assert.Empty(client.Invocations);
		Assert.Same(cards, vm.Entries);
	}

	[Fact]
	public async Task ACollectionComingBack_AfterAFilmWasMarkedWatched_KeepsItsCards_AndShowsIt()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Name = "Copper and Glass", Type = BaseItemDto_Type.BoxSet };
		var films = Films(6);
		var (vm, _) = Collection(set, () => films);
		await vm.OnNavigatedTo(set);
		var cards = vm.Entries;
		var marked = films[2].Id!.Value;
		Assert.EndsWith("0 of 6 watched", vm.Summary);

		// the film's own page marked it watched; the server now says so
		films = [.. films.Select(f => Copy(f, played: f.Id == marked))];
		List<Guid> told = [];
		void OnChanged(object? s, ItemUserDataChangedEventArgs e) => told.Add(e.ItemId);
		ItemUserData.Changed += OnChanged;
		try
		{
			await ((IKeptOnBack)vm).Returned(set, stale: true);
		}
		finally
		{
			ItemUserData.Changed -= OnChanged;
		}

		// the same cards (the focus and the scroll stay where Back put them), the marked one says watched, and its card heard it
		Assert.Same(cards, vm.Entries);
		Assert.True(vm.Entries[2].Item.UserData?.Played);
		Assert.Contains(marked, told);
		Assert.DoesNotContain(told, id => id != marked && vm.Entries.Any(e => e.Item.Id == id)); // (the static event: other tests may tell their own)
		Assert.EndsWith("1 of 6 watched", vm.Summary);
		Assert.Equal(LoadStatus.Loaded, vm.State.Status);
	}

	[Fact]
	public async Task ACollectionComingBack_WithAFilmRemoved_ShowsItGone()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Name = "Rainy Sunday", Type = BaseItemDto_Type.BoxSet };
		var films = Films(5);
		var (vm, _) = Collection(set, () => films);
		await vm.OnNavigatedTo(set);
		var gone = films[1].Id;

		films = [.. films.Where(f => f.Id != gone)];
		await ((IKeptOnBack)vm).Returned(set, stale: true);

		Assert.Equal(4, vm.Entries.Count);
		Assert.DoesNotContain(vm.Entries, x => x.Item.Id == gone);
	}

	[Fact]
	public async Task ACollectionThatCouldNotLoad_IsNotKept()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Name = "Copper and Glass", Type = BaseItemDto_Type.BoxSet };
		var (vm, _) = Collection(set, () => null!);
		await vm.OnNavigatedTo(set);

		Assert.Equal(LoadStatus.Failed, vm.State.Status);
		Assert.False(vm.CanKeep);
	}
	[Fact]
	public async Task AFilmComingBack_AfterAPlay_ShowsItsResumePoint_WithoutAskingForItsTracksAgain()
	{
		var id = Guid.NewGuid();
		var film = new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie, Name = "Wild Canyons", RunTimeTicks = 6_000_000_000, UserData = new UserItemDataDto { PlaybackPositionTicks = 0 } };
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(id)).ReturnsAsync(film);
		client.Setup(x => x.GetItemCollections(id)).ReturnsAsync([]);
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		var vm = new MovieViewModel(client.Object, commands);
		await vm.OnNavigatedTo(film);
		Assert.True(vm.CanKeep);
		var shown = vm.Dto;
		Assert.False(vm.HasResumePoint);

		// played for a while (the player's page opened and left: stale), the server now has a resume point
		client.Setup(x => x.GetItem(id)).ReturnsAsync(new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie, Name = "Wild Canyons", RunTimeTicks = 6_000_000_000,
			UserData = new UserItemDataDto { PlaybackPositionTicks = 1_200_000_000, PlayedPercentage = 20 } });
		List<string?> changed = [];
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		await ((IKeptOnBack)vm).Returned(film, stale: true);

		Assert.Same(shown, vm.Dto); // the page's record, in place (its tracks and pickers as the user left them)
		Assert.True(vm.HasResumePoint);
		Assert.Contains(nameof(MovieViewModel.PlayLabel), changed);
		Assert.DoesNotContain(nameof(MovieViewModel.Dto), changed);
	}

	// (1.7.5: results that may be out of date are searched again behind them: SearchBackRefreshTests. With no search shown, nothing)
	[Fact]
	public async Task SearchResultsComingBack_WithNoSearchShown_AskNothing()
	{
		var client = new Mock<IJellyfinClient>();
		var vm = new SearchResultsViewModel(client.Object);
		client.Invocations.Clear();

		await ((IKeptOnBack)vm).Returned("the", stale: true);

		Assert.Empty(client.Invocations);
	}

	/// <summary>
	/// Nixie 1.7.5 (1.7.4's KNOWN-ISSUES line): a big library left before all its titles came is kept, and on Back reads on from
	/// where leaving stopped it (the page it was reading is asked again), with the titles it had shown at once; 1.7.4 loaded it
	/// all again, from its first page.
	/// </summary>
	[Fact]
	public async Task ABigLibraryLeftWhileItsTitlesCame_IsKept_AndReadsOnFromWhereItStopped()
	{
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var films = Films(450);
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // every page after the first waits for it
		var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var asked = new System.Collections.Concurrent.ConcurrentQueue<int>();
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000175"));
		client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool total, CancellationToken token) =>
			{
				asked.Enqueue(start);
				if (start > 0)
				{
					waiting.TrySetResult();
					await held.Task.WaitAsync(token);
				}

				return new BaseItemDtoQueryResult { Items = [.. films.Skip(start).Take(limit)], TotalRecordCount = total ? films.Count : null };
			});
		using var ui = new UiThread();
		var vm = await ui.Invoke(() => new LibraryViewModel(client.Object));
		await ui.Run(() => vm.OnNavigatedTo(library)).WaitAsync(TimeSpan.FromSeconds(20));
		await waiting.Task.WaitAsync(TimeSpan.FromSeconds(20));
		var shown = await ui.Invoke(() => vm.Items.Count);
		Assert.InRange(shown, 1, 449);

		// a film opened while the rest still came: the library is left (its reading stops), and kept
		await ui.Run(vm.OnNavigatedFrom);
		await ui.Invoke(() => vm.RestLoaded).Unwrap().WaitAsync(TimeSpan.FromSeconds(20)); // the stopped read is over
		Assert.True(await ui.Invoke(() => vm.IsLoadingMore && vm.CanKeep));
		Assert.Equal(shown, await ui.Invoke(() => vm.Items.Count));

		// Back: the same titles at once, then the rest from where it stopped
		asked.Clear();
		await ui.Run(() => ((IKeptOnBack)vm).Returned(library, stale: false));
		Assert.Equal(shown, await ui.Invoke(() => vm.Items.Count));
		held.SetResult();
		await ui.Invoke(() => vm.RestLoaded).Unwrap().WaitAsync(TimeSpan.FromSeconds(20));

		Assert.Equal((false, false, 450), await ui.Invoke(() => (vm.IsLoadingMore, vm.LoadStopped, vm.Items.Count)));
		Assert.Equal(shown, asked.First()); // the page leaving stopped, asked again; never the first page
		Assert.DoesNotContain(0, asked);
	}

	/// <summary>
	/// Nixie 1.7.5 (review Major 1, the reviewer's probe B): a page whose answer came just as the library was left (the reader had
	/// counted it) was dropped, and Back read on from after it: 1,000 of 1,200 titles, with nothing said. It goes in now.
	/// </summary>
	[Fact]
	public async Task ABigLibraryLeftAsAPageCame_KeepsThatPage_AndReadsOnToTheEnd()
	{
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var films = Films(1200);
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-0000000001b5"));
		var later = 0;
		client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool total, CancellationToken token) =>
			{
				if (start > 0 && Interlocked.Increment(ref later) == 1)
				{
					waiting.TrySetResult();
					await held.Task.ConfigureAwait(false); // an answer already on its way: it comes whatever the token says
				}

				return new BaseItemDtoQueryResult { Items = [.. films.Skip(start).Take(limit)], TotalRecordCount = total ? films.Count : null };
			});
		using var ui = new UiThread();
		var vm = await ui.Invoke(() => new LibraryViewModel(client.Object));
		await ui.Run(() => vm.OnNavigatedTo(library)).WaitAsync(TimeSpan.FromSeconds(20));
		await waiting.Task.WaitAsync(TimeSpan.FromSeconds(20));

		// the answer comes (the reader takes it off the page's thread); before the library's own step runs, a film is opened
		await ui.Run(async () =>
		{
			held.SetResult();
			Thread.Sleep(500);
			await vm.OnNavigatedFrom();
		}).WaitAsync(TimeSpan.FromSeconds(20));
		await ui.Invoke(() => vm.RestLoaded).Unwrap().WaitAsync(TimeSpan.FromSeconds(20));
		Assert.True(await ui.Invoke(() => vm.IsLoadingMore && vm.CanKeep));

		await ui.Run(() => ((IKeptOnBack)vm).Returned(library, stale: false));
		await ui.Invoke(() => vm.RestLoaded).Unwrap().WaitAsync(TimeSpan.FromSeconds(20));

		Assert.Equal((false, false, 1200), await ui.Invoke(() => (vm.IsLoadingMore, vm.LoadStopped, vm.Items.Count)));
	}
}
