using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4: Continue Watching kept clean. A card's menu takes an item out of the row at once (its resume point cleared
/// on the server, Jellyfin's own way: its resume list is the items with one), with an Undo that puts both back; items not
/// played for a chosen number of days can be hidden (on this PC only); Next Up does not repeat what Continue Watching shows.
/// </summary>
public sealed class ContinueWatchingTests : IDisposable
{
	static ContinueWatchingTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<ILocalSettingsService> _settings = new();
	private readonly Subject<IOutboundWebSocketMessage> _socket = new();
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-cw-test-" + Guid.NewGuid().ToString("N"));
	private readonly ManualClock _clock = new();
	private readonly HomeCache _cache;
	private readonly List<(Guid Id, long Ticks)> _resumeSet = [];
	private TaskCompletionSource<bool> _resumeAnswer = Answered(true);

	public ContinueWatchingTests()
	{
		_cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), _clock);
		_client.SetupGet(x => x.BaseUrl).Returns("https://jellyfin.example");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("cccccccc-0000-0000-0000-000000000002"));
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([]);
		_client.Setup(x => x.SetResumePosition(It.IsAny<Guid>(), It.IsAny<long>()))
			.Returns((Guid id, long ticks) => { lock (_resumeSet) { _resumeSet.Add((id, ticks)); } return _resumeAnswer.Task; });
		HideAfter(0);
	}

	public void Dispose()
	{
		_cache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private static TaskCompletionSource<bool> Answered(bool ok)
	{
		var answer = new TaskCompletionSource<bool>();
		answer.SetResult(ok);
		return answer;
	}

	private void HideAfter(int days) =>
		_settings.Setup(x => x.ReadSetting(ContinueWatchingRules.HideAfterDays.Name, It.IsAny<int>())).Returns(days);

	private void Answer(BaseItemDto[] resume, BaseItemDto[]? nextUp = null)
	{
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [.. resume] });
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [.. nextUp ?? []] });
	}

	private async Task<HomeViewModel> Home()
	{
		var home = new HomeViewModel(_client.Object, _socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(),
			Mock.Of<Settings.ISettings>()), _cache, _settings.Object, _clock);
		await home.OnNavigatedTo(null!);
		return home;
	}

	private BaseItemDto Item(string name, long position = 5_000, double daysAgo = 1) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie,
		UserData = new UserItemDataDto { PlaybackPositionTicks = position, LastPlayedDate = _clock.GetUtcNow() - TimeSpan.FromDays(daysAgo) },
	};

	private static List<string?> Names(IEnumerable<BaseItemViewModel> items) => [.. items.Select(x => x.Dto.Name)];

	// ---- Remove from Continue Watching

	[Fact]
	public async Task Remove_TakesTheCardOutAtOnce_AndClearsItsResumePointOnTheServer()
	{
		var a = Item("A"); var b = Item("B", position: 7_777); var c = Item("C");
		Answer([a, b, c]);
		var home = await Home();
		_resumeAnswer = new TaskCompletionSource<bool>(); // the server has not answered yet

		var removing = home.RemoveFromContinueWatching(b);

		Assert.Equal(["A", "C"], Names(home.ContinueItems));
		Assert.Equal([(b.Id!.Value, 0L)], _resumeSet);
		Assert.True(home.Notice is { CanUndo: true });
		Assert.Equal("Removed B from Continue Watching", home.Notice!.Message);

		_resumeAnswer.SetResult(true);
		await removing;
		Assert.Equal(["A", "C"], Names(home.ContinueItems));
	}

	[Fact]
	public async Task Undo_PutsTheCardBackWhereItWas_AndItsResumePointOnTheServer()
	{
		var a = Item("A"); var b = Item("B", position: 7_777); var c = Item("C");
		Answer([a, b, c]);
		var home = await Home();
		await home.RemoveFromContinueWatching(b);

		await home.UndoRemoval();

		Assert.Equal(["A", "B", "C"], Names(home.ContinueItems));
		Assert.Equal([(b.Id!.Value, 0L), (b.Id!.Value, 7_777L)], _resumeSet);
		Assert.Null(home.Notice);

		// a second Undo does nothing more
		await home.UndoRemoval();
		Assert.Equal(2, _resumeSet.Count);
	}

	[Fact]
	public async Task Undo_BeforeTheServerAnswered_WaitsForTheRemovalFirst()
	{
		var a = Item("A", position: 4_444);
		Answer([a, Item("B")]);
		var home = await Home();
		_resumeAnswer = new TaskCompletionSource<bool>();
		var removing = home.RemoveFromContinueWatching(a);
		var removal = _resumeAnswer;

		var undoing = home.UndoRemoval();
		Assert.Equal(["A", "B"], Names(home.ContinueItems)); // back at once
		Assert.Single(_resumeSet);                          // the restore waits: it must not reach the server before the reset

		_resumeAnswer = Answered(true);
		removal.SetResult(true);
		await removing;
		await undoing;
		Assert.Equal([(a.Id!.Value, 0L), (a.Id!.Value, 4_444L)], _resumeSet);
	}

	[Fact]
	public async Task ARemovalTheServerDidNotTake_PutsTheCardBack_AndSaysSo()
	{
		var a = Item("A"); var b = Item("B");
		Answer([a, b]);
		var home = await Home();
		_resumeAnswer = Answered(false);

		await home.RemoveFromContinueWatching(a);

		Assert.Equal(["A", "B"], Names(home.ContinueItems));
		Assert.True(home.Notice is { CanUndo: false });
		Assert.StartsWith("Couldn't remove A", home.Notice!.Message);
	}

	[Fact]
	public async Task TheRemovalsOwnMessage_ArrivingAfterItsUndo_DoesNotTakeTheCardOutAgain()
	{
		var a = Item("A", position: 4_444);
		Answer([a, Item("B")]);
		var home = await Home();
		await home.RemoveFromContinueWatching(a);
		await home.UndoRemoval();

		// the server's two messages, in order, both after the Undo: the reset, then the restore
		_socket.OnNext(Changed(a.Id!.Value, 0));
		_socket.OnNext(Changed(a.Id!.Value, 4_444));
		await Task.Delay(300);

		Assert.Equal(["A", "B"], Names(home.ContinueItems));

		// later changes are the server's as before: played elsewhere, the item leaves
		_socket.OnNext(Changed(a.Id!.Value, 0, played: true));
		Assert.True(SpinWait.SpinUntil(() => home.ContinueItems.Count == 1, TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public async Task WithoutTheServersWord_ItsLaterChangesAreNotIgnoredForGood()
	{
		var a = Item("A", position: 4_444);
		Answer([a, Item("B")]);
		var home = await Home();
		await home.RemoveFromContinueWatching(a);
		await home.UndoRemoval(); // the server never says (no websocket)

		_clock.Advance(HomeViewModel.OwnResumePointFor + TimeSpan.FromSeconds(1));
		_socket.OnNext(Changed(a.Id!.Value, 0)); // reset from another app

		Assert.True(SpinWait.SpinUntil(() => home.ContinueItems.Count == 1, TimeSpan.FromSeconds(5)), "the server's change was ignored");
	}

	[Fact]
	public async Task BackToHome_TheRemovedCardIsNotShownAgain()
	{
		var a = Item("A"); var b = Item("B");
		Answer([a, b]);
		var home = await Home();
		await home.RemoveFromContinueWatching(a);
		await home.OnNavigatedFrom();

		_client.Setup(x => x.GetContinueWatching()).Returns(new TaskCompletionSource<BaseItemDtoQueryResult?>().Task); // no answer yet
		var back = new HomeViewModel(_client.Object, _socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(),
			Mock.Of<Settings.ISettings>()), _cache, _settings.Object, _clock);
		_ = back.OnNavigatedTo(null!);

		Assert.True(SpinWait.SpinUntil(() => !back.IsContinueLoading, TimeSpan.FromSeconds(5)));
		Assert.Equal(["B"], Names(back.ContinueItems));
	}

	[Fact]
	public async Task TheRowsCards_AndOnlyThem_OfferTheRemoval_WhileHomeIsShown()
	{
		var a = Item("A");
		Answer([a]);
		var home = await Home();

		Assert.Same(home, ContinueWatchingRows.RowOf(home.ContinueItems[0].Dto));
		Assert.Null(ContinueWatchingRows.RowOf(new BaseItemDto { Id = a.Id, Name = "A" })); // the same item's card in another row
		Assert.Contains(ItemMenu.For(a, null, includePlay: true, canEditSegments: false, inContinueWatching: true).SelectMany(x => x),
			x => x.Command == ItemMenuCommand.RemoveFromContinueWatching && x.Text == "Remove from Continue Watching");

		await home.OnNavigatedFrom();
		Assert.Null(ContinueWatchingRows.RowOf(home.ContinueItems[0].Dto));
	}

	// ---- hide stale items

	[Fact]
	public async Task StaleItems_AreHidden_WhenTheSettingSaysSo_AndOnlyOnThisPc()
	{
		Answer([Item("Yesterday", daysAgo: 1), Item("Two months", daysAgo: 61), Item("Last month", daysAgo: 29)]);
		HideAfter(30);

		var home = await Home();

		Assert.Equal(["Yesterday", "Last month"], Names(home.ContinueItems));
		Assert.Empty(_resumeSet); // nothing asked of the server
	}

	[Fact]
	public async Task TheHomeViewModelsSetting_SavesTheChoice_AndTheRowFollowsAtOnce()
	{
		var saved = 0;
		_settings.Setup(x => x.ReadSetting(ContinueWatchingRules.HideAfterDays.Name, It.IsAny<int>())).Returns(() => saved);
		_settings.Setup(x => x.SaveSetting(ContinueWatchingRules.HideAfterDays.Name, It.IsAny<int>())).Callback((string _, int v) => saved = v);
		Answer([Item("Yesterday", daysAgo: 1), Item("Two months", daysAgo: 61)]);
		var home = await Home();
		Assert.Equal((0, 2), (home.HideContinueWatchingAfterDays, home.ContinueItems.Count));

		home.HideContinueWatchingAfterDays = 30;
		Assert.Equal(30, saved);
		Assert.Equal(["Yesterday"], Names(home.ContinueItems));

		home.HideContinueWatchingAfterDays = 45; // not a choice: ignored
		Assert.Equal(30, saved);

		home.HideContinueWatchingAfterDays = 0;
		Assert.Equal(["Yesterday", "Two months"], Names(home.ContinueItems));
		Assert.Empty(_resumeSet); // nothing asked of the server
	}

	[Fact]
	public async Task AnItemMarkedWatched_IsNotShown_EvenIfTheAnswerStillListsIt()
	{
		var done = Item("Done", daysAgo: 1);
		done.UserData!.Played = true;
		Answer([Item("Yesterday", daysAgo: 1), done]);

		var home = await Home();

		Assert.Equal(["Yesterday"], Names(home.ContinueItems));
	}

	[Fact]
	public void TheNotice_NamesAnEpisodeByItsShowAndPlace_InTheSharedFormat()
	{
		var episode = new BaseItemDto { Type = BaseItemDto_Type.Episode, Name = "After the Machine", SeriesName = "Iron Lighthouses ", ParentIndexNumber = 3, IndexNumber = 4 };

		Assert.Equal("Iron Lighthouses S3 E4", ContinueWatchingRules.NameOf(episode));
		Assert.Equal("Iron Lighthouses", ContinueWatchingRules.NameOf(new BaseItemDto { Type = BaseItemDto_Type.Episode, Name = "x", SeriesName = "Iron Lighthouses" }));
		Assert.Equal("A Quiet Harbor", ContinueWatchingRules.NameOf(new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "A Quiet Harbor" }));
	}

	[Fact]
	public async Task StaleItems_AreShown_ByDefault()
	{
		Answer([Item("Yesterday", daysAgo: 1), Item("A year", daysAgo: 365)]);

		var home = await Home();

		Assert.Equal(["Yesterday", "A year"], Names(home.ContinueItems));
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(-5, 0)]
	[InlineData(30, 30)]
	[InlineData(45, 60)]
	[InlineData(400, 90)]
	public void TheSavedSetting_ReadsAsAChoiceSettingsOffers(int saved, int read)
	{
		HideAfter(saved);
		Assert.Equal(read, ContinueWatchingRules.ReadHideAfterDays(_settings.Object));
	}

	[Fact]
	public void TheSettingsChoices_AreNamed()
	{
		Assert.Equal([0, 30, 60, 90], ContinueWatchingRules.HideAfterDaysChoices);
		Assert.Equal(["Never", "After 30 days", "After 60 days", "After 90 days"], ContinueWatchingRules.HideAfterDaysChoices.Select(ContinueWatchingRules.HideAfterDaysLabel));
	}

	// ---- Next Up does not repeat Continue Watching

	[Fact]
	public async Task NextUp_LeavesOutWhatContinueWatchingShows()
	{
		var episode = Item("S1E4");
		Answer([episode], [new BaseItemDto { Id = episode.Id, Name = "S1E4", Type = BaseItemDto_Type.Episode }, Item("Other show S2E1", position: 0)]);

		var home = await Home();

		Assert.Equal(["S1E4"], Names(home.ContinueItems));
		Assert.Equal(["Other show S2E1"], Names(home.NextUpItems));
	}

	// ---- the hero: what is new, never Continue Watching

	[Fact]
	public async Task TheHero_FeaturesNextUp_NotContinueWatching()
	{
		var resume = Item("Resume");
		resume.BackdropImageTags = ["bd"];
		var next = new BaseItemDto { Id = Guid.NewGuid(), Name = "Next", Type = BaseItemDto_Type.Movie, BackdropImageTags = ["bd2"], UserData = new UserItemDataDto() };
		Answer([resume], [next]);

		var home = await Home();

		Assert.Equal(["Next"], home.Hero.Select(x => x.Title));
	}

	// ---- the review of wip/114-library

	[Fact]
	public async Task ProgressElsewhere_WhileTheUndoWaits_DropsTheUndo_SoItCannotPutTheOldPointBack()
	{
		var a = Item("A", position: 4_444);
		Answer([a, Item("B")]);
		var home = await Home();
		await home.RemoveFromContinueWatching(a);
		_socket.OnNext(Changed(a.Id!.Value, 0));     // the removal's own message
		_socket.OnNext(Changed(a.Id!.Value, 9_000)); // watched on another device meanwhile

		Assert.True(SpinWait.SpinUntil(() => home.Notice is null, TimeSpan.FromSeconds(5)), "the Undo stayed");
		await home.UndoRemoval(); // Ctrl+Z after all: nothing

		Assert.DoesNotContain(_resumeSet, x => x.Ticks == 4_444);
		var back = Assert.Single(home.ContinueItems, x => x.Id == a.Id); // in the row again, as the server has it
		Assert.Equal(9_000, back.Dto.UserData!.PlaybackPositionTicks);
	}

	[Fact]
	public async Task MarkedWatchedElsewhere_WhileTheUndoWaits_DropsTheUndo_AndTheItemStaysOut()
	{
		var a = Item("A", position: 4_444);
		Answer([a, Item("B")]);
		var home = await Home();
		await home.RemoveFromContinueWatching(a);
		_socket.OnNext(Changed(a.Id!.Value, 0, played: true));

		Assert.True(SpinWait.SpinUntil(() => home.Notice is null, TimeSpan.FromSeconds(5)), "the Undo stayed");
		Assert.DoesNotContain(home.ContinueItems, x => x.Id == a.Id);
	}

	[Fact]
	public async Task PlayedRightAfterAnUndo_IsFinal_NotIgnoredAsTheRemovalsMessage()
	{
		var a = Item("A", position: 4_444);
		Answer([a, Item("B")]);
		var home = await Home();
		await home.RemoveFromContinueWatching(a);
		await home.UndoRemoval();

		_socket.OnNext(Changed(a.Id!.Value, 0, played: true)); // the server's messages merged: the Undo, then "mark as watched"

		Assert.True(SpinWait.SpinUntil(() => home.ContinueItems.All(x => x.Id != a.Id), TimeSpan.FromSeconds(5)), "a watched item stayed in Continue Watching");
	}

	[Fact]
	public async Task NewProgressRightAfterAnUndo_IsApplied()
	{
		var a = Item("A", position: 4_444);
		Answer([a, Item("B")]);
		var home = await Home();
		await home.RemoveFromContinueWatching(a);
		await home.UndoRemoval();

		_socket.OnNext(Changed(a.Id!.Value, 9_500));

		Assert.True(SpinWait.SpinUntil(() => home.ContinueItems.Single(x => x.Id == a.Id).Dto.UserData!.PlaybackPositionTicks == 9_500, TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public async Task AnUndo_AfterASignInAsSomeoneElse_DoesNothing()
	{
		var a = Item("A", position: 4_444);
		Answer([a, Item("B")]);
		var home = await Home();
		await home.RemoveFromContinueWatching(a);

		_client.SetupGet(x => x.UserId).Returns(Guid.NewGuid()); // signed in as another user
		await home.UndoRemoval();

		Assert.DoesNotContain(_resumeSet, x => x.Ticks == 4_444);
		Assert.Null(home.Notice);
		Assert.DoesNotContain(home.ContinueItems, x => x.Id == a.Id);
	}

	[Fact]
	public async Task AnUndo_OnAnotherServer_DoesNothing()
	{
		var a = Item("A", position: 4_444);
		Answer([a, Item("B")]);
		var home = await Home();
		await home.RemoveFromContinueWatching(a);

		_client.SetupGet(x => x.BaseUrl).Returns("https://other.example");
		await home.UndoRemoval();

		Assert.DoesNotContain(_resumeSet, x => x.Ticks == 4_444);
	}

	// ---- the items review: a change the server pushes reaches the item record too (the menu and the toggles read it)

	[Fact]
	public async Task AServerPushedChange_ReachesTheItemRecord_InEveryRow_SoTheMenuSaysTheNewState()
	{
		var resume = Item("Resume", position: 4_000);
		var next = new BaseItemDto { Id = Guid.NewGuid(), Name = "Next", Type = BaseItemDto_Type.Episode, UserData = new UserItemDataDto { Played = false } };
		var movies = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };
		var recent = new BaseItemDto { Id = Guid.NewGuid(), Name = "Recent", Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { Played = false } };
		Answer([resume], [next]);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetLatestItems(It.IsAny<BaseItemDto>())).ReturnsAsync([recent]);
		var home = await Home();
		Assert.True(SpinWait.SpinUntil(() => home.RecentItems.Any(r => r.Items.Count == 1), TimeSpan.FromSeconds(5)));

		_socket.OnNext(new UserDataChangedMessage { Data = new UserDataChangeInfo { UserDataList =
		[
			new UserItemDataDto { ItemId = resume.Id, PlaybackPositionTicks = 9_000, PlayedPercentage = 30, IsFavorite = true },
			new UserItemDataDto { ItemId = next.Id, IsFavorite = true },
			new UserItemDataDto { ItemId = recent.Id, Played = true, PlayCount = 1 },
		] } });

		var cw = home.ContinueItems.Single();
		Assert.True(SpinWait.SpinUntil(() => cw.Dto.UserData!.PlaybackPositionTicks == 9_000, TimeSpan.FromSeconds(5)), "the record kept the old position");
		Assert.Equal((true, 30d), (cw.Dto.UserData!.IsFavorite, cw.Dto.UserData.PlayedPercentage));
		Assert.Equal((true, 30d), (cw.UserData!.IsFavorite, cw.UserData.PlayedPercentage));

		var nu = home.NextUpItems.Single();
		Assert.True(nu.Dto.UserData!.IsFavorite);
		Assert.Contains(ItemMenu.For(nu.Dto, null, true, false).SelectMany(g => g), e => e.Text == "Remove from favorites");

		var ra = home.RecentItems.Single().Items.Single();
		Assert.Equal((true, 1), (ra.Dto.UserData!.Played, ra.Dto.UserData.PlayCount));
		Assert.True(ra.UserData!.Played);
		Assert.Contains(ItemMenu.For(ra.Dto, null, true, false).SelectMany(g => g), e => e.Text == "Mark as unwatched");
	}

	private static UserDataChangedMessage Changed(Guid id, long ticks, bool played = false) => new()
	{
		Data = new UserDataChangeInfo { UserDataList = [new UserItemDataDto { ItemId = id, PlaybackPositionTicks = ticks, Played = played }] },
	};
}

public sealed class ContinueWatchingSettingTests
{
	[Fact]
	public void TheSettingsCard_ReadsAndSavesTheChoice()
	{
		var stored = new Dictionary<string, object?>();
		var settings = new Mock<ILocalSettingsService>();
		settings.Setup(x => x.ReadSetting(ContinueWatchingRules.HideAfterDays.Name, It.IsAny<int>()))
			.Returns((string key, int fallback) => stored.TryGetValue(key, out var v) ? (int)v! : fallback);
		settings.Setup(x => x.SaveSetting(ContinueWatchingRules.HideAfterDays.Name, It.IsAny<int>()))
			.Callback((string key, int value) => stored[key] = value);
		var card = new ContinueWatchingSettingViewModel(settings.Object);

		Assert.Equal("Never", card.Selected.Label); // off by default
		Assert.Equal(["Never", "After 30 days", "After 60 days", "After 90 days"], card.Choices.Select(c => c.Label));

		card.Selected = card.Choices[2];
		Assert.Equal(60, stored[ContinueWatchingRules.HideAfterDays.Name]);
		Assert.Equal(60, ContinueWatchingRules.ReadHideAfterDays(settings.Object));
		Assert.Equal("After 60 days", card.Selected.Label);
	}
}

/// <summary>Seaglass 1.1.4 (interaction audit, item 5): Back to Home puts the user where they were.</summary>
public sealed class HomePlaceTests
{
	[Fact]
	public void HomesPlace_IsKeptForTheUserWhoLeftIt()
	{
		var alice = Guid.NewGuid();
		var item = Guid.NewGuid();
		HomePlace.Remember(alice, new HomePlace(640, "New in Movies", item));

		Assert.Equal(new HomePlace(640, "New in Movies", item), HomePlace.Recall(alice));
		Assert.Null(HomePlace.Recall(Guid.NewGuid()));
	}
}

/// <summary>The review, item 1: the notice's Undo cannot be held open for good.</summary>
public sealed class NoticeTimingTests
{
	[Fact]
	public void TheNotice_Goes_After10s_Or5sAfterAHold_AndAt30sEvenWhileHeld()
	{
		Assert.Equal(TimeSpan.FromSeconds(10), NoticeTiming.Remaining(TimeSpan.Zero, held: false, afterHold: false));
		Assert.Equal(TimeSpan.FromSeconds(5), NoticeTiming.Remaining(TimeSpan.FromSeconds(12), held: false, afterHold: true));
		Assert.Equal(TimeSpan.FromSeconds(22), NoticeTiming.Remaining(TimeSpan.FromSeconds(8), held: true, afterHold: false)); // runs on to the hard limit
		Assert.Equal(TimeSpan.FromSeconds(2), NoticeTiming.Remaining(TimeSpan.FromSeconds(28), held: false, afterHold: true)); // never past it
		Assert.Equal(TimeSpan.Zero, NoticeTiming.Remaining(TimeSpan.FromSeconds(31), held: true, afterHold: false));

		Assert.False(NoticeTiming.Closes(TimeSpan.FromSeconds(20), held: true));
		Assert.True(NoticeTiming.Closes(TimeSpan.FromSeconds(30), held: true));
		Assert.True(NoticeTiming.Closes(TimeSpan.FromSeconds(10), held: false));
	}

	[Fact]
	public void HomePage_UsesIt_AndNeverPutsTheFocusOnTheNotice()
	{
		var code = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin/Views/HomePage.xaml.cs"));

		Assert.Contains("NoticeTiming.Closes(NoticeShownFor, held)", code);
		Assert.Contains("_noticeTimer.Interval = NoticeTiming.Remaining(NoticeShownFor, held, afterHold);", code);
		Assert.DoesNotContain("UndoButton.Focus(", code);
	}
}
