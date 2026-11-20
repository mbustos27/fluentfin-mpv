using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;
using static FluentFin.Core.Tests.Journal.Events;

namespace FluentFin.Core.Tests;

/// <summary>
/// sg-cw: a Continue Watching that keeps up with the player. Home is made by the player's Back before the player is left, so it
/// asked the server before the backend's Stopped report had reached it, and a play that put an item in Continue Watching (or
/// took it out) showed only after R. <see cref="RecentPlays"/> now shows a play the moment it ends, and the server's answers
/// once it has it (read again while it has not caught up).
/// </summary>
public sealed class RecentPlaysTests : IDisposable
{
	static RecentPlaysTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private const string Server = "https://jellyfin.example";
	private static readonly Guid User = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
	private static readonly long Runtime = 45 * Minute;

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<ILocalSettingsService> _settings = new();
	private readonly Subject<IOutboundWebSocketMessage> _socket = new();
	private readonly Subject<BridgeEvent> _events = new();
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-rewatch-test-" + Guid.NewGuid().ToString("N"));
	private readonly ManualClock _clock = new();
	private readonly HomeCache _cache;
	private readonly RecentPlays _plays;
	private readonly Dictionary<Guid, BaseItemDto> _server = [];
	private int _resumeAsks;
	private int _nextUpAsks;
	private Func<BaseItemDto[]> _resume = () => [];
	private Func<BaseItemDto[]> _nextUp = () => [];

	public RecentPlaysTests()
	{
		_cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), _clock);
		_client.SetupGet(x => x.BaseUrl).Returns(Server);
		_client.SetupGet(x => x.UserId).Returns(User);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([]);
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(() => { Interlocked.Increment(ref _resumeAsks); return new BaseItemDtoQueryResult { Items = [.. _resume()] }; });
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(() => { Interlocked.Increment(ref _nextUpAsks); return new BaseItemDtoQueryResult { Items = [.. _nextUp()] }; });
		_client.Setup(x => x.GetItem(It.IsAny<Guid>())).ReturnsAsync((Guid id) => _server.TryGetValue(id, out var dto) ? Copy(dto) : null);
		_client.Setup(x => x.SetResumePosition(It.IsAny<Guid>(), It.IsAny<long>())).ReturnsAsync(true);
		_settings.Setup(x => x.ReadSetting(ContinueWatchingRules.HideAfterDays.Name, It.IsAny<int>())).Returns(0);
		_plays = new RecentPlays(_events, _client.Object, _clock, ui: null) { CatchUpRetries = [TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30)] };
	}

	public void Dispose()
	{
		_plays.Dispose();
		_cache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private async Task<HomeViewModel> Home(RecentPlays? plays = null)
	{
		var home = new HomeViewModel(_client.Object, _socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(),
			Mock.Of<Settings.ISettings>()), _cache, _settings.Object, _clock, plays ?? _plays);
		await home.OnNavigatedTo(null!);
		return home;
	}

	private static BaseItemDto Episode(string name, int index, bool played = false, long position = 0) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Episode, SeriesName = "Iron Lighthouses", ParentIndexNumber = 1, IndexNumber = index,
		RunTimeTicks = Runtime,
		UserData = new UserItemDataDto { Played = played, PlaybackPositionTicks = position, PlayedPercentage = position > 0 ? 100.0 * position / Runtime : null },
	};

	private static BaseItemDto Copy(BaseItemDto dto, long? position = null, bool? played = null) => new()
	{
		Id = dto.Id, Name = dto.Name, Type = dto.Type, SeriesName = dto.SeriesName, ParentIndexNumber = dto.ParentIndexNumber, IndexNumber = dto.IndexNumber,
		RunTimeTicks = dto.RunTimeTicks,
		UserData = new UserItemDataDto
		{
			Played = played ?? dto.UserData?.Played, PlaybackPositionTicks = position ?? dto.UserData?.PlaybackPositionTicks,
			PlayedPercentage = (position ?? dto.UserData?.PlaybackPositionTicks) is > 0 and var p ? 100.0 * p / Runtime : null,
		},
	};

	// the backend's events of one play of the item: playing, its positions, then the end (docs/BRIDGE.md §5)
	private void Play(BaseItemDto item, string session, long to, string reason = "stopped")
	{
		_events.OnNext(State("playing", item.Id!.Value, session, Runtime));
		_events.OnNext(Position(0, Runtime));
		_events.OnNext(Position(to, Runtime));
		_events.OnNext(Ended(item.Id!.Value, reason));
	}

	private static List<string?> Names(IEnumerable<BaseItemViewModel> items) => [.. items.Select(x => x.Dto.Name)];

	private static bool Until(Func<bool> test) => SpinWait.SpinUntil(test, TimeSpan.FromSeconds(5));

	// ---- RecentPlays: the backend's events

	[Theory]
	[InlineData(20, 45, "stopped", PlayGuess.Resumable)]
	[InlineData(42, 45, "stopped", PlayGuess.Watched)]   // past 90 %
	[InlineData(1, 45, "stopped", PlayGuess.NoResumePoint)] // under 5 %
	[InlineData(2, 4, "stopped", PlayGuess.Watched)]     // a short item (under 5 minutes) is watched once started
	[InlineData(10, 45, "eof", PlayGuess.Watched)]
	public void APlay_IsGuessedAsJellyfinsDefaultRulesWould(int minutes, int runtimeMinutes, string reason, PlayGuess guess)
	{
		var play = new RecentPlay(Guid.NewGuid(), "ps", minutes * Minute, runtimeMinutes * Minute, reason, null, Server, User, _clock.GetUtcNow());
		Assert.Equal(guess, play.Guess);
		Assert.Equal(PlayGuess.Unknown, (play with { RuntimeTicks = null, EndedReason = "stopped" }).Guess);
	}

	[Fact]
	public async Task AServersOwnResumeRules_AreTheGuesses()
	{
		// QA servers: an ordinary user reads /System/Configuration (200 on 10.11.11 and 12.4.0); one set to resume items of 30 s
		_client.Setup(x => x.GetConfiguration()).ReturnsAsync(new ServerConfiguration { MinResumePct = 5, MaxResumePct = 90, MinResumeDurationSeconds = 30 });
		var minute = TimeSpan.FromMinutes(1).Ticks;
		var item = Episode("Pilot", 1);
		var seen = new List<RecentPlay>();
		using var _ = _plays.Changes.Subscribe(seen.Add);

		_events.OnNext(State("playing", item.Id!.Value, "ps-1", minute));
		await Task.Delay(200); // the rules are read as the play starts
		_events.OnNext(Position(minute / 2, minute));
		_events.OnNext(Ended(item.Id!.Value, "stopped"));

		Assert.Equal(PlayGuess.Resumable, Assert.Single(seen).Guess); // by Jellyfin's defaults (5 minutes) a 1-minute item is watched
		Assert.Equal(PlayGuess.Watched, (seen[0] with { Rules = null }).Guess);
	}

	[Fact]
	public void AnEndedPlay_SaysWhereItStopped_BeforeItIsReported()
	{
		var item = Episode("Pilot", 1);
		var seen = new List<RecentPlay>();
		using var _ = _plays.Changes.Subscribe(seen.Add);

		Play(item, "ps-1", 20 * Minute);

		var play = Assert.Single(seen);
		Assert.Equal((item.Id!.Value, (long?)(20 * Minute), (long?)Runtime, false, PlayGuess.Resumable), (play.ItemId, play.PositionTicks, play.RuntimeTicks, play.Reported, play.Guess));
		Assert.True(play.IsOf(Server, User));
		Assert.Equal([play], _plays.Recent(Server, User));
		Assert.Empty(_plays.Recent("https://elsewhere.example", User));
		Assert.Empty(_plays.Recent(Server, Guid.NewGuid()));
	}

	[Fact]
	public async Task AReportedPlay_IsReadFromTheServer_AgainWhileTheServerDoesNotShowIt()
	{
		var item = Episode("Pilot", 1, played: true);
		_server[item.Id!.Value] = item; // the server has not caught up: still watched, no resume point
		var seen = new List<RecentPlay>();
		using var _ = _plays.Changes.Subscribe(seen.Add);
		Play(item, "ps-1", 20 * Minute);

		_events.OnNext(Reported(item.Id!.Value, "ps-1", "sent"));
		await Task.Delay(10);
		_server[item.Id!.Value] = Copy(item, position: 20 * Minute + TimeSpan.FromSeconds(1).Ticks); // ... now it has
		await _plays.CatchingUp;

		var reported = seen.Last();
		Assert.True(reported.Reported);
		Assert.Equal(20 * Minute + TimeSpan.FromSeconds(1).Ticks, reported.ServerData!.PlaybackPositionTicks);
		_client.Verify(x => x.GetItem(item.Id!.Value), Times.Exactly(2));
	}

	[Fact]
	public async Task AServerThatNeverShowsThePlay_IsBelievedAfterTheRetries()
	{
		var item = Episode("Pilot", 1, played: true);
		_server[item.Id!.Value] = item;
		var seen = new List<RecentPlay>();
		using var _ = _plays.Changes.Subscribe(seen.Add);
		Play(item, "ps-1", 20 * Minute);

		_events.OnNext(Reported(item.Id!.Value, "ps-1", "sent"));
		await _plays.CatchingUp;

		Assert.Equal([false, true], seen.Select(x => x.Reported));
		Assert.Equal(0, seen[1].ServerData!.PlaybackPositionTicks);
		_client.Verify(x => x.GetItem(item.Id!.Value), Times.Exactly(3));
	}

	[Fact]
	public async Task AReportTheServerNeverGot_IsReportedAtOnce_WithoutAsking()
	{
		var item = Episode("Pilot", 1);
		var seen = new List<RecentPlay>();
		using var _ = _plays.Changes.Subscribe(seen.Add);
		Play(item, "ps-1", 20 * Minute);

		_events.OnNext(Reported(item.Id!.Value, "ps-1", "failed"));
		await _plays.CatchingUp;

		Assert.Equal([null, "failed"], seen.Select(x => x.Outcome));
		_client.Verify(x => x.GetItem(It.IsAny<Guid>()), Times.Never);
	}

	[Fact]
	public void ANewBackend_ForgetsTheOldOnesSessions()
	{
		var item = Episode("Pilot", 1);
		var seen = new List<RecentPlay>();
		using var _ = _plays.Changes.Subscribe(seen.Add);
		_events.OnNext(State("playing", item.Id!.Value, "ps-1", Runtime));
		_events.OnNext(Position(20 * Minute, Runtime));

		_events.OnNext(Hello());
		_events.OnNext(Ended(item.Id!.Value, "stopped"));
		_events.OnNext(Reported(item.Id!.Value, "ps-1", "sent"));

		Assert.Empty(seen);
	}

	[Fact]
	public async Task OnceTheServerHasThePlay_EverySurfaceShowingTheItemGetsItsUserData()
	{
		var item = Episode("Pilot", 1, played: true);
		_server[item.Id!.Value] = Copy(item, position: 20 * Minute);
		using var plays = new RecentPlays(_events, _client.Object, _clock, ImmediateScheduler.Instance);
		var published = new List<ItemUserDataChangedEventArgs>();
		void OnChanged(object? sender, ItemUserDataChangedEventArgs e)
		{
			if (e.ItemId == item.Id)
			{
				lock (published)
				{
					published.Add(e);
				}
			}
		}

		ItemUserData.Changed += OnChanged;
		try
		{
			Play(item, "ps-1", 20 * Minute);
			_events.OnNext(Reported(item.Id!.Value, "ps-1", "sent"));
			await plays.CatchingUp;
		}
		finally
		{
			ItemUserData.Changed -= OnChanged;
		}

		var e = Assert.Single(published);
		Assert.Equal((20 * Minute, false), (e.UserData.PlaybackPositionTicks, e.Failed));
	}

	// ---- Home: the play at once, then the server's answers

	[Fact]
	public async Task APlayEnded_PutsTheEpisodeAtTheFrontOfContinueWatching_AtOnce_ThoughHomeHadNoCardForIt()
	{
		var other = Episode("Other", 5, position: 10 * Minute);
		var pilot = Episode("Pilot", 1, played: true); // a show watched to the end, its first episode played again
		_server[pilot.Id!.Value] = pilot;
		_resume = () => [other];
		var home = await Home();

		Play(pilot, "ps-1", 20 * Minute);

		Assert.True(Until(() => Names(home.ContinueItems) is ["Pilot", "Other"]), $"shown: {string.Join(", ", Names(home.ContinueItems))}");
		Assert.EndsWith("44% watched", ItemLabels.CardName(home.ContinueItems[0]));
		Assert.Equal(1, _resumeAsks); // nothing asked again yet: the backend has not reported it
	}

	[Fact]
	public async Task AnAnswerAskedBeforeTheReport_DoesNotUndoThePlay()
	{
		var other = Episode("Other", 5, position: 10 * Minute);
		var pilot = Episode("Pilot", 1, played: true);
		_server[pilot.Id!.Value] = pilot;
		var answer = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		_client.Setup(x => x.GetContinueWatching()).Returns(answer.Task); // Home's first answer, asked as the player closed
		var navigated = Home();

		Play(pilot, "ps-1", 20 * Minute);
		await Task.Delay(200);
		answer.SetResult(new BaseItemDtoQueryResult { Items = [other] }); // the server before the Stopped report
		var home = await navigated;

		Assert.True(Until(() => Names(home.ContinueItems) is ["Pilot", "Other"]), $"shown: {string.Join(", ", Names(home.ContinueItems))}");
	}

	[Fact]
	public async Task OnceReported_HomeAsksForBothRowsAgain_AndShowsTheServersAnswer()
	{
		var other = Episode("Other", 5, position: 10 * Minute);
		var pilot = Episode("Pilot", 1, played: true);
		_server[pilot.Id!.Value] = pilot;
		_resume = () => [other];
		var home = await Home();
		Play(pilot, "ps-1", 20 * Minute);
		Assert.True(Until(() => home.ContinueItems.Count == 2));

		var stored = Copy(pilot, position: 20 * Minute + TimeSpan.FromSeconds(2).Ticks);
		_server[pilot.Id!.Value] = stored;
		_resume = () => [stored, other];
		_nextUp = () => [Episode("Second", 2, played: true)]; // the show watched again: the episode after the one played
		_events.OnNext(Reported(pilot.Id!.Value, "ps-1", "sent"));
		await _plays.CatchingUp;

		Assert.True(Until(() => home.NextUpItems.Count == 1 && home.ContinueItems.FirstOrDefault()?.Dto.UserData?.PlaybackPositionTicks == stored.UserData!.PlaybackPositionTicks),
			"the server's answer did not replace the play");
		Assert.Equal((2, 2), (_resumeAsks, _nextUpAsks));
		Assert.Equal(["Second"], Names(home.NextUpItems));
	}

	[Fact]
	public async Task AWatchedPlay_LeavesBothRowsAtOnce_AndTheNextEpisodeComesWithTheServersAnswer()
	{
		var second = Episode("Second", 2);
		var third = Episode("Third", 3);
		_server[second.Id!.Value] = second;
		_nextUp = () => [second];
		var home = await Home();
		Assert.Equal(["Second"], Names(home.NextUpItems));

		Play(second, "ps-1", 43 * Minute); // past 90 %
		Assert.True(Until(() => home.NextUpItems.Count == 0), "the episode played to its end stayed up next");

		_server[second.Id!.Value] = Copy(second, played: true, position: 0);
		_nextUp = () => [third];
		_events.OnNext(Reported(second.Id!.Value, "ps-1", "sent"));
		await _plays.CatchingUp;

		Assert.True(Until(() => Names(home.NextUpItems) is ["Third"]), $"up next: {string.Join(", ", Names(home.NextUpItems))}");
		Assert.Empty(home.ContinueItems);
	}

	[Fact]
	public async Task APlayAsAnotherUserOrOnAnotherServer_ChangesNothing()
	{
		var other = Episode("Other", 5, position: 10 * Minute);
		var pilot = Episode("Pilot", 1, played: true);
		_server[pilot.Id!.Value] = pilot;
		_resume = () => [other];
		_client.SetupGet(x => x.UserId).Returns(Guid.NewGuid()); // the backend played as someone else
		Play(pilot, "ps-1", 20 * Minute);
		_client.SetupGet(x => x.UserId).Returns(User);

		var home = await Home();
		Play(Episode("Unseen", 9), "ps-2", 20 * Minute); // an item Home cannot find: no card

		await Task.Delay(300);
		Assert.Equal(["Other"], Names(home.ContinueItems));
	}

	[Fact]
	public async Task HomeMadeAfterThePlayEnded_ShowsItAtOnce()
	{
		var other = Episode("Other", 5, position: 10 * Minute);
		var pilot = Episode("Pilot", 1, played: true);
		_server[pilot.Id!.Value] = pilot;
		_resume = () => [other];
		Play(pilot, "ps-1", 20 * Minute); // the player closed before Home was made (Back from the series page, then Home)

		var home = await Home();

		Assert.True(Until(() => Names(home.ContinueItems) is ["Pilot", "Other"]), $"shown: {string.Join(", ", Names(home.ContinueItems))}");
	}

	[Fact]
	public async Task RemovingAPlayedEpisode_BeforeItIsReported_DoesNotBringItBack()
	{
		var other = Episode("Other", 5, position: 10 * Minute);
		var pilot = Episode("Pilot", 1, played: true);
		_server[pilot.Id!.Value] = pilot;
		_resume = () => [other];
		var home = await Home();
		Play(pilot, "ps-1", 20 * Minute);
		Assert.True(Until(() => home.ContinueItems.Count == 2));

		await home.RemoveFromContinueWatching(home.ContinueItems[0].Dto);
		Play(Episode("Unseen", 9), "ps-2", 20 * Minute); // anything that shows the row again: another play ends
		await Task.Delay(300);

		Assert.Equal(["Other"], Names(home.ContinueItems));
	}

	// ---- the review of wip/continue-watching

	[Fact]
	public async Task APlayOfAnotherUser_ArrivingWhileHomeIsShown_ChangesNothing()
	{
		var other = Episode("Other", 5, position: 10 * Minute);
		var pilot = Episode("Pilot", 1, played: true);
		_server[pilot.Id!.Value] = pilot;
		_resume = () => [other];
		var home = await Home();

		// played as someone else (a switch of user since), then on another server: Home is this server's and this user's
		await home.OnPlay(new RecentPlay(pilot.Id!.Value, "ps-1", 20 * Minute, Runtime, "stopped", null, Server, Guid.NewGuid(), _clock.GetUtcNow()));
		await home.OnPlay(new RecentPlay(pilot.Id!.Value, "ps-2", 20 * Minute, Runtime, "stopped", null, "https://elsewhere.example", User, _clock.GetUtcNow()));
		Assert.Equal(["Other"], Names(home.ContinueItems));

		await home.OnPlay(new RecentPlay(pilot.Id!.Value, "ps-3", 20 * Minute, Runtime, "stopped", null, Server, User, _clock.GetUtcNow()));
		Assert.Equal(["Pilot", "Other"], Names(home.ContinueItems)); // the same play as this user: shown
	}

	[Fact]
	public async Task TheServersMessageAboutAPlayedItem_EndsItsLocalUpdate()
	{
		var other = Episode("Other", 5, position: 10 * Minute);
		var pilot = Episode("Pilot", 1, played: true);
		_server[pilot.Id!.Value] = pilot;
		_resume = () => [other];
		var home = await Home();
		Play(pilot, "ps-1", 20 * Minute); // a resume point by Jellyfin's rules
		Assert.True(Until(() => Names(home.ContinueItems) is ["Pilot", "Other"]));

		_socket.OnNext(Changed(pilot.Id!.Value, 0, played: true)); // the server's word: watched, no resume point (its own rules)
		Assert.True(Until(() => Names(home.ContinueItems) is ["Other"]), $"shown: {string.Join(", ", Names(home.ContinueItems))}");

		Play(Episode("Unseen", 9), "ps-2", 20 * Minute); // anything that shows the row again: another play ends
		await Task.Delay(300);
		Assert.Equal(["Other"], Names(home.ContinueItems)); // the guess does not come back over the server's word
	}

	[Fact]
	public async Task AnAnswerAskedBeforeTheServersMessage_DoesNotPutAWatchedEpisodeBackUpNext()
	{
		// the reviewer's case: Home's first answers, asked as the player closed (before the Stopped report), come after the
		// server's message about the episode just watched to its end
		var second = Episode("Second", 2);
		_server[second.Id!.Value] = second;
		var resume = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var nextUp = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		_client.Setup(x => x.GetContinueWatching()).Returns(resume.Task);
		_client.Setup(x => x.GetNextUp()).Returns(nextUp.Task);
		var navigated = Home();

		Play(second, "ps-1", 43 * Minute); // past 90 %: watched
		await Task.Delay(200);
		_socket.OnNext(Changed(second.Id!.Value, 0, played: true));
		await Task.Delay(200);
		resume.SetResult(new BaseItemDtoQueryResult { Items = [Episode("Other", 5, position: 10 * Minute)] });
		nextUp.SetResult(new BaseItemDtoQueryResult { Items = [second] }); // the server before the Stopped report
		var home = await navigated;
		await Task.Delay(300);

		Assert.DoesNotContain("Second", Names(home.NextUpItems));
	}

	[Fact]
	public async Task APlayTheServerHas_DropsTheSeasonListsKeptForSwitching()
	{
		// Nixie 1.4.1 integration (sg-cw x season speed): a season's kept list (asked ahead or seen) holds the episode's old
		// state; once the play is the server's, the next click on that season must ask again, not show the old progress
		var season = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = "Season 1" };
		var pilot = Episode("Pilot", 1, played: true);
		_server[pilot.Id!.Value] = Copy(pilot, position: 20 * Minute);
		var listsAsked = 0;
		_client.Setup(x => x.GetItems(It.Is<BaseItemDto>(p => p.Id == season.Id), It.IsAny<bool>()))
			.ReturnsAsync(() => { Interlocked.Increment(ref listsAsked); return new BaseItemDtoQueryResult { Items = [Copy(pilot)] }; });
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: true);
		using var plays = new RecentPlays(_events, _client.Object, _clock, ImmediateScheduler.Instance);
		await kept.GetList(season);
		Assert.True(kept.HasList(season.Id!.Value));

		Play(pilot, "ps-1", 20 * Minute);
		_events.OnNext(Reported(pilot.Id!.Value, "ps-1", "sent"));
		await plays.CatchingUp;

		Assert.False(kept.HasList(season.Id!.Value));
		await kept.GetList(season);
		Assert.Equal(2, listsAsked);
	}

	private static UserDataChangedMessage Changed(Guid id, long ticks, bool played = false) => new()
	{
		Data = new UserDataChangeInfo { UserDataList = [new UserItemDataDto { ItemId = id, PlaybackPositionTicks = ticks, Played = played }] },
	};
}
