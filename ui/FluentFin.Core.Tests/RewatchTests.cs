using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;
using static FluentFin.Core.Tests.Journal.Events;

namespace FluentFin.Core.Tests;

/// <summary>
/// sg-cw: "sometimes rewatching a show doesn't put it back in Continue Watching". Seen on the QA servers (Jellyfin 10.11.11 and
/// 12.4.0): Next Up lists a show watched again only with rewatching (JellyfinClient, HomeRequestTests), and Jellyfin 12 keeps an
/// episode watched while it is watched again (10.11 unmarks it as it starts), so Home, which dropped every watched item from
/// Continue Watching, never showed it there.
/// </summary>
public sealed class RewatchTests : IDisposable
{
	static RewatchTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private static readonly long Runtime = 45 * Minute;

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<ILocalSettingsService> _settings = new();
	private readonly Subject<IOutboundWebSocketMessage> _socket = new();
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-rewatch-test-" + Guid.NewGuid().ToString("N"));
	private readonly ManualClock _clock = new();
	private readonly HomeCache _cache;
	private Func<BaseItemDto[]> _resume = () => [];
	private Func<BaseItemDto[]> _nextUp = () => [];

	public RewatchTests()
	{
		_cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), _clock);
		_client.SetupGet(x => x.BaseUrl).Returns("https://jellyfin.example");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("cccccccc-0000-0000-0000-000000000003"));
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([]);
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = [.. _resume()] });
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = [.. _nextUp()] });
		_client.Setup(x => x.SetResumePosition(It.IsAny<Guid>(), It.IsAny<long>())).ReturnsAsync(true);
		_settings.Setup(x => x.ReadSetting(ContinueWatchingRules.HideAfterDays.Name, It.IsAny<int>())).Returns(0);
	}

	public void Dispose()
	{
		_cache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private async Task<HomeViewModel> Home()
	{
		var home = new HomeViewModel(_client.Object, _socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(),
			Mock.Of<Settings.ISettings>()), _cache, _settings.Object, _clock);
		await home.OnNavigatedTo(null!);
		return home;
	}

	private static readonly Guid Lighthouses = Guid.NewGuid();

	private static BaseItemDto Episode(string name, int index, bool played = false, long position = 0, Guid? series = null, string show = "Iron Lighthouses") => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Episode, SeriesName = show, SeriesId = series ?? Lighthouses, ParentIndexNumber = 1, IndexNumber = index,
		RunTimeTicks = Runtime,
		UserData = new UserItemDataDto { Played = played, PlaybackPositionTicks = position, PlayedPercentage = position > 0 ? 100.0 * position / Runtime : null },
	};

	private static List<string?> Names(IEnumerable<BaseItemViewModel> items) => [.. items.Select(x => x.Dto.Name)];

	private static bool Until(Func<bool> test) => SpinWait.SpinUntil(test, TimeSpan.FromSeconds(5));

	// ---- Jellyfin 12: an episode watched again is watched and in progress at once

	[Fact]
	public async Task AnEpisodeWatchedAgain_IsInContinueWatching_ThoughTheServerStillMarksItWatched()
	{
		var again = Episode("Pilot", 1, played: true, position: 20 * Minute);
		_resume = () => [again, Episode("Done", 2, played: true)]; // the second: a saved page racing "mark as watched"

		var home = await Home();

		Assert.Equal(["Pilot"], Names(home.ContinueItems));
		Assert.EndsWith("44% watched", ItemLabels.CardName(home.ContinueItems[0])); // not "watched": its bar shows 44 %
	}

	// 1.5 fixes: Settings' "Include shows I'm rewatching" off: an episode watched again (watched, with a resume point) is out
	[Fact]
	public async Task WithRewatchingLeftOut_AnEpisodeWatchedAgain_IsNotInContinueWatching()
	{
		_settings.Setup(x => x.ReadSetting(Settings.SettingKeys.LeaveOutRewatching.Name, It.IsAny<bool>())).Returns(true);
		var again = Episode("Pilot", 1, played: true, position: 20 * Minute);
		_resume = () => [again, Episode("Other", 2, position: 5 * Minute)];

		var home = await Home();

		Assert.Equal(["Other"], Names(home.ContinueItems));
		Assert.Equal(["Other"], ContinueWatchingRules.Visible([again, Episode("Other", 2, position: 5 * Minute)], DateTimeOffset.UtcNow, 0, includeRewatching: false).Select(x => x.Name));
		Assert.Equal(2, ContinueWatchingRules.Visible([again, Episode("Other", 2, position: 5 * Minute)], DateTimeOffset.UtcNow, 0).Count);
	}

	[Fact]
	public async Task TheServersMessages_KeepAnEpisodeWatchedAgain_AndAWatchedMarkWithNoResumePointTakesItOut()
	{
		var again = Episode("Pilot", 1, played: true, position: 20 * Minute);
		_resume = () => [again, Episode("Other", 2, position: 5 * Minute)];
		var home = await Home();

		_socket.OnNext(Changed(again.Id!.Value, 25 * Minute, played: true));
		Assert.True(Until(() => home.ContinueItems.Single(x => x.Id == again.Id).Dto.UserData!.PlaybackPositionTicks == 25 * Minute), "the progress of an episode watched again was not applied");

		_socket.OnNext(Changed(again.Id!.Value, 0, played: true));
		Assert.True(Until(() => home.ContinueItems.All(x => x.Id != again.Id)), "watched to the end, it stayed");
	}

	[Fact]
	public async Task RemovingAnEpisodeWatchedAgain_KeepsItsUndo_PastTheRemovalsOwnMessage()
	{
		var again = Episode("Pilot", 1, played: true, position: 20 * Minute);
		_resume = () => [again, Episode("Other", 2, position: 5 * Minute)];
		var home = await Home();
		await home.RemoveFromContinueWatching(again);

		_socket.OnNext(Changed(again.Id!.Value, 0, played: true)); // the removal's own message: still watched, no resume point
		await Task.Delay(300);
		Assert.True(home.Notice is { CanUndo: true }, "the Undo went with the removal's own message");

		await home.UndoRemoval();
		Assert.Equal(["Pilot", "Other"], Names(home.ContinueItems));
	}

	[Fact]
	public async Task TheRemovalsOwnMessage_AfterTheUndoOfAnEpisodeWatchedAgain_DoesNotTakeItOutAgain()
	{
		var again = Episode("Pilot", 1, played: true, position: 20 * Minute);
		_resume = () => [again, Episode("Other", 2, position: 5 * Minute)];
		var home = await Home();
		await home.RemoveFromContinueWatching(again);
		await home.UndoRemoval();

		_socket.OnNext(Changed(again.Id!.Value, 0, played: true));
		await Task.Delay(300);

		Assert.Equal(["Pilot", "Other"], Names(home.ContinueItems));
	}

	// ---- the review of wip/continue-watching: one show once on Home

	[Fact]
	public async Task UpNext_LeavesOutAShowContinueWatchingHas()
	{
		// QA 12.4: S1 E1 watched again and stopped midway in Continue Watching, and Next Up (with rewatching) listing S1 E2
		var other = Guid.NewGuid();
		_resume = () => [Episode("Pilot", 1, played: true, position: 20 * Minute)];
		_nextUp = () => [Episode("Second", 2, played: true), Episode("Elsewhere", 4, series: other, show: "The Iron Circus")];

		var home = await Home();

		Assert.Equal(["Pilot"], Names(home.ContinueItems));
		Assert.Equal(["Elsewhere"], Names(home.NextUpItems));
	}

	[Fact]
	public async Task UpNext_ShowsOneEpisodeOfAShow_TheOneWatchedTowardMostRecently()
	{
		// QA 10.11 and 12.4: S1 E1 to S2 E1 watched, then S1 E2 played again to the end: Next Up lists S1 E3 (after the one
		// watched again, first: its show was watched most recently through it) and S2 E2 (after the last one watched)
		var other = Guid.NewGuid();
		_nextUp = () => [Episode("Third", 3, played: true), Episode("Elsewhere", 4, series: other, show: "The Iron Circus"), Episode("Fifth", 5)];

		var home = await Home();

		Assert.Equal(["Third", "Elsewhere"], Names(home.NextUpItems));
	}

	[Fact]
	public async Task MarkedWatched_AnEpisodeWatchedAgain_LeavesContinueWatching()
	{
		// QA 10.11 and 12.4: Mark as watched (POST /UserPlayedItems) clears the resume point, also of an episode watched again
		// that 12.4 keeps watched while it plays: watched and no resume point is out; watched with one stays
		var again = Episode("Pilot", 1, played: true, position: 20 * Minute);
		_resume = () => [again, Episode("Other", 2, position: 5 * Minute)];
		var home = await Home();
		Assert.Equal(["Pilot", "Other"], Names(home.ContinueItems));

		_socket.OnNext(Changed(again.Id!.Value, 0, played: true)); // the server's message after the mark
		Assert.True(Until(() => Names(home.ContinueItems) is ["Other"]), "an episode marked watched stayed in Continue Watching");

		// and the page saved meanwhile (the item record the mark answered: watched, no resume point) shows it no more
		again.UserData!.PlaybackPositionTicks = 0;
		again.UserData.PlayedPercentage = null;
		Assert.Empty(ContinueWatchingRules.Visible([again], DateTimeOffset.UtcNow, 0));
	}

	private static UserDataChangedMessage Changed(Guid id, long ticks, bool played = false) => new()
	{
		Data = new UserDataChangeInfo { UserDataList = [new UserItemDataDto { ItemId = id, PlaybackPositionTicks = ticks, Played = played }] },
	};
}
