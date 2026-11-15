using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: the progressive home page. Every request goes out at once, each part of the page shows a placeholder
/// until its own answer is in, the "Recently added" rows keep the server's view order whatever order the answers come in,
/// and a row with nothing (or a failed request) goes away. The client is a mock whose answers the test releases one by one.
/// </summary>
public sealed class HomeViewModelTests
{
	static HomeViewModelTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private static readonly BaseItemDto Movies = View("Movies", BaseItemDto_CollectionType.Movies);
	private static readonly BaseItemDto Music = View("Music", BaseItemDto_CollectionType.Music);
	private static readonly BaseItemDto Shows = View("Shows", BaseItemDto_CollectionType.Tvshows);
	private static readonly BaseItemDto Kids = View("Kids", BaseItemDto_CollectionType.Movies);
	private static readonly BaseItemDto Collections = View("Collections", BaseItemDto_CollectionType.Boxsets);

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly TaskCompletionSource<BaseItemDtoQueryResult?> _continue = new();
	private readonly TaskCompletionSource<BaseItemDtoQueryResult?> _nextUp = new();
	private readonly Dictionary<Guid, TaskCompletionSource<List<BaseItemDto>?>> _latest = [];
	private readonly List<string> _latestAsked = [];
	private readonly Subject<IOutboundWebSocketMessage> _socket = new();

	public HomeViewModelTests()
	{
		_client.Setup(x => x.GetContinueWatching()).Returns(_continue.Task);
		_client.Setup(x => x.GetNextUp()).Returns(_nextUp.Task);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([Movies, Music, Shows, Kids, Collections]);
		foreach (var view in new[] { Movies, Music, Shows, Kids, Collections })
		{
			_latest[view.Id!.Value] = new();
		}
		_client.Setup(x => x.GetLatestItems(It.IsAny<BaseItemDto>()))
			.Returns((BaseItemDto library) => { lock (_latestAsked) { _latestAsked.Add(library.Name!); } return _latest[library.Id!.Value].Task; });
	}

	private HomeViewModel NewHome() => new(_client.Object, _socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<Settings.ISettings>()));

	[Fact]
	public async Task EveryRowIsAskedForAtOnce_AndAppearsWhenItsOwnAnswerIsIn_InViewOrder()
	{
		var home = NewHome();
		var navigation = home.OnNavigatedTo(null!);

		// nothing has answered: every part of the page is a placeholder, every row has been asked for, Music has no row
		Assert.True(home.IsLoading);
		Assert.True(home.IsContinueLoading);
		Assert.True(home.IsNextUpLoading);
		Assert.Equal(["Movies", "Shows", "Kids", "Collections"], home.RecentItems.Select(x => x.Library.Name));
		Assert.All(home.RecentItems, x => Assert.True(x.IsLoading));
		Assert.Equal(["Movies", "Shows", "Kids", "Collections"], _latestAsked);
		_client.Verify(x => x.GetContinueWatching(), Times.Once);
		_client.Verify(x => x.GetNextUp(), Times.Once);

		// the third row answers first: it shows, the rows above it stay placeholders in their places
		_latest[Kids.Id!.Value].SetResult([Item("Kid 1"), Item("Kid 2")]);
		Assert.Equal(["Movies", "Shows", "Kids", "Collections"], home.RecentItems.Select(x => x.Library.Name));
		var kids = home.RecentItems[2];
		Assert.False(kids.IsLoading);
		Assert.Equal(["Kid 1", "Kid 2"], kids.Items.Select(x => x.Dto.Name));
		Assert.True(home.RecentItems[0].IsLoading);
		Assert.True(home.RecentItems[1].IsLoading);

		// nothing recent, or a failed request: the row goes
		_latest[Collections.Id!.Value].SetResult([]);
		_latest[Shows.Id!.Value].SetResult(null);
		Assert.Equal(["Movies", "Kids"], home.RecentItems.Select(x => x.Library.Name));

		// Continue Watching shows on its own; an empty Next Up stops loading and has no items (its section goes)
		_continue.SetResult(new BaseItemDtoQueryResult { Items = [Item("Half watched")] });
		Assert.False(home.IsContinueLoading);
		Assert.True(home.HasContinueItems);
		Assert.Equal(["Half watched"], home.ContinueItems.Select(x => x.Dto.Name));
		_nextUp.SetResult(new BaseItemDtoQueryResult { Items = [] });
		Assert.False(home.IsNextUpLoading);
		Assert.False(home.HasNextUpItems);

		// the page is loading until the last answer
		Assert.True(home.IsLoading);
		Assert.False(navigation.IsCompleted);
		_latest[Movies.Id!.Value].SetResult([Item("Movie 1")]);
		await navigation;
		Assert.False(home.IsLoading);
		Assert.Equal(["Movies", "Kids"], home.RecentItems.Select(x => x.Library.Name));
		Assert.All(home.RecentItems, x => Assert.False(x.IsLoading));
		_client.Verify(x => x.GetLatestItems(It.Is<BaseItemDto>(v => v.Id == Music.Id)), Times.Never);
	}

	[Fact]
	public async Task FailedContinueAndNextUp_StopLoading_WithNoSection()
	{
		var home = NewHome();
		var navigation = home.OnNavigatedTo(null!);

		_continue.SetResult(null);
		_nextUp.SetResult(null);
		foreach (var latest in _latest.Values)
		{
			latest.TrySetResult(null);
		}
		await navigation;

		Assert.False(home.IsContinueLoading);
		Assert.False(home.IsNextUpLoading);
		Assert.False(home.HasContinueItems);
		Assert.False(home.HasNextUpItems);
		Assert.Empty(home.RecentItems);
		Assert.False(home.IsLoading);
	}

	[Fact]
	public void AWatchedItem_LeavesContinueWatching_WhileOtherRowsAreStillLoading()
	{
		var watched = Item("Half watched");
		var home = NewHome();
		_ = home.OnNavigatedTo(null!);
		_continue.SetResult(new BaseItemDtoQueryResult { Items = [watched, Item("Other")] });
		Assert.True(home.IsLoading);

		_socket.OnNext(new UserDataChangedMessage
		{
			Data = new UserDataChangeInfo { UserDataList = [new UserItemDataDto { ItemId = watched.Id, Played = true, PlaybackPositionTicks = 0 }] },
		});

		// the handler runs on the main-thread scheduler, a moment later in a test
		Assert.True(SpinWait.SpinUntil(() => home.ContinueItems.Count == 1, TimeSpan.FromSeconds(5)), "the watched item stayed in Continue Watching");
		Assert.Equal(["Other"], home.ContinueItems.Select(x => x.Dto.Name));
		Assert.True(home.HasContinueItems);
	}

	private static BaseItemDto View(string name, BaseItemDto_CollectionType type) =>
		new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder, CollectionType = type };

	private static BaseItemDto Item(string name, Guid? id = null) =>
		new() { Id = id ?? Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { PlaybackPositionTicks = 100 } };
}
