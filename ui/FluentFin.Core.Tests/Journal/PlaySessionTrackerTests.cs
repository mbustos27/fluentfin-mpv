using FluentFin.Core.Journal;
using static FluentFin.Core.Tests.Journal.Events;

namespace FluentFin.Core.Tests.Journal;

public sealed class PlaySessionTrackerTests
{
	private static readonly long Runtime = 45 * Minute;
	private readonly PlaySessionTracker _tracker = new();
	private DateTimeOffset _now = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

	private List<TrackerSignal> Feed(params IEnumerable<FluentFin.Core.Mpv.BridgeEvent> events)
	{
		var signals = new List<TrackerSignal>();
		foreach (var e in events)
		{
			_now = _now.AddMilliseconds(500);
			signals.AddRange(_tracker.Handle(e, _now));
		}

		return signals;
	}

	[Fact]
	public void ASession_OpensOnItsFirstState_AndCompletesOnItsReported_WithWhatWasSeen()
	{
		var item = Guid.NewGuid();
		var opened = Feed(Item(item, runTimeTicks: Runtime), State("opening", item, "ps-1"), State("playing", item, "ps-1", Runtime), State("playing", item, "ps-1", Runtime));
		var open = Assert.IsType<PlayOpened>(Assert.Single(opened));
		Assert.Equal(("ps-1", item), (open.PlaySessionId, open.ItemId));

		Feed(Play(0, Runtime, Runtime));
		Feed(Ended(item, "eof"), Idle());
		var endedAt = _now;
		var done = Assert.IsType<PlayCompleted>(Assert.Single(Feed(Reported(item, "ps-1", "sent"))));

		Assert.Equal("sent", done.Outcome);
		Assert.Equal("eof", done.EndedReason);
		Assert.Equal(endedAt.AddMilliseconds(-500), done.EndedAt);
		Assert.Equal(0, done.StartPositionTicks);
		Assert.Equal(Runtime, done.LastPositionTicks);
		Assert.Equal(Runtime, done.WatchedTicks);
		Assert.Equal(Runtime, done.RuntimeTicks);
		Assert.Equal("Episode", done.Item!.Type);
		Assert.Equal(0, _tracker.OpenCount);
	}

	[Fact]
	public void Seeks_AreNotWatching()
	{
		var item = Guid.NewGuid();
		Feed(State("playing", item, "ps-1", Runtime));
		Feed(Play(0, 2 * Minute, Runtime));
		// a jump to near the end, then the last minute
		Feed(Play(43 * Minute, 44 * Minute, Runtime));

		var done = Assert.IsType<PlayCompleted>(Assert.Single(Feed(Reported(item, "ps-1", "sent"))));

		Assert.Equal(3 * Minute, done.WatchedTicks);
		Assert.Equal(44 * Minute, done.LastPositionTicks);
	}

	[Fact]
	public void PositionsAfterTheItemEnded_DoNotMoveItsEnd()
	{
		// next episode: the old session ends (replaced), a position of the new item can come before the new state
		var first = Guid.NewGuid();
		var second = Guid.NewGuid();
		Feed(State("playing", first, "ps-1", Runtime));
		Feed(Play(40 * Minute, 42 * Minute, Runtime));
		Feed(Ended(first, "replaced"), Position(0, Runtime), State("opening", second, "ps-2"), State("playing", second, "ps-2", Runtime));
		Feed(Play(0, Minute, Runtime));

		var signals = Feed(Reported(first, "ps-1", "sent"), Reported(second, "ps-2", "sent"));
		var a = Assert.IsType<PlayCompleted>(signals[0]);
		var b = Assert.IsType<PlayCompleted>(signals[1]);

		Assert.Equal((first, (string?)"replaced", (long?)(42 * Minute), 2 * Minute), (a.ItemId, a.EndedReason, a.LastPositionTicks, a.WatchedTicks));
		Assert.Equal((second, (string?)null, (long?)Minute, Minute), (b.ItemId, b.EndedReason, b.LastPositionTicks, b.WatchedTicks));
	}

	[Fact]
	public void AReportedForASessionNoStateShowed_CompletesNothing()
	{
		Assert.Empty(Feed(Reported(Guid.NewGuid(), "never-seen", "none")));
	}

	[Fact]
	public void AReportedBeforeItsState_KeepsTheSessionFromOpening()
	{
		var item = Guid.NewGuid();
		Feed(Reported(item, "ps-1", "sent"));

		Assert.Empty(Feed(State("playing", item, "ps-1")));
		Assert.Equal(0, _tracker.OpenCount);
	}

	[Fact]
	public void ANewBackendProcess_ForgetsTheSessionsOfTheOldOne()
	{
		var item = Guid.NewGuid();
		Feed(State("playing", item, "ps-1", Runtime), Position(Minute, Runtime));

		Feed(Hello());

		Assert.Equal(0, _tracker.OpenCount);
		Assert.Empty(Feed(Reported(item, "ps-1", "failed")));
	}

	[Fact]
	public void IdleAndPlayFileStates_OpenNothing_AndUnreadableEventsAreIgnored()
	{
		Assert.Empty(Feed(Idle(), Of("state", new { state = "playing", itemId = (string?)null, playSessionId = (string?)null }), State("playing", Guid.NewGuid(), null)));
		Assert.Empty(Feed(Of("position", new { positionTicks = "soon" }), Of("item", new { itemId = 5 }), Of("ended", "x"), Of("reported", new { })));
	}

	[Fact]
	public void TheDuration_FromPositions_WinsOverTheServersRuntime()
	{
		var item = Guid.NewGuid();
		Feed(Item(item, runTimeTicks: 50 * Minute), State("playing", item, "ps-1"));
		Feed(Play(0, Minute, 44 * Minute));

		var done = Assert.IsType<PlayCompleted>(Assert.Single(Feed(Reported(item, "ps-1", "sent"))));

		Assert.Equal(44 * Minute, done.RuntimeTicks);
	}
}
