using FluentFin.Core.Journal;
using static FluentFin.Core.Tests.Journal.Events;

namespace FluentFin.Core.Tests.Journal;

public sealed class AutoLogRulesTests
{
	private static readonly long Runtime = 45 * Minute;
	private static readonly JournalUserData Unplayed = new(false, 0, null);
	private static readonly JournalUserData PlayedOnce = new(true, 1, DateTimeOffset.UnixEpoch);

	private static PlayCompleted Play(string outcome = "sent", string? ended = "eof", long start = 0, long? last = null, long? watched = null, long? runtime = null) =>
		new("ps", Guid.NewGuid(), outcome, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ended, start, last ?? runtime ?? Runtime, runtime ?? Runtime, watched ?? (runtime ?? Runtime) - start);

	[Fact]
	public void Sent_PlayedFlipped_IsLogged()
	{
		// the server's verdict wins over the session's own: it counted this play (its "Max resume %" may be lower than ours)
		var decision = AutoLogRules.Decide(Play(ended: "stopped", last: 30 * Minute, watched: 30 * Minute), Unplayed, PlayedOnce, canAskServer: true);

		Assert.True(decision.Log);
		Assert.True(decision.OnServer);
	}

	[Fact]
	public void Sent_NotPlayedOnTheServer_IsNotLogged_WhateverThePlayCount()
	{
		// Jellyfin counts a play when it starts: an abandoned play raises PlayCount without Played
		var decision = AutoLogRules.Decide(Play(ended: "stopped", last: 20 * Minute, watched: 20 * Minute), Unplayed, new JournalUserData(false, 1, null), canAskServer: true);

		Assert.False(decision.Log);
	}

	[Fact]
	public void Sent_AnAbandonedRewatch_IsNotLogged_ThoughPlayCountRose()
	{
		// a server that keeps Played through a new play: only the count moved, and the session stopped at 40 %
		var abandoned = Play(ended: "stopped", last: 18 * Minute, watched: 18 * Minute);

		Assert.False(AutoLogRules.Decide(abandoned, PlayedOnce, PlayedOnce with { PlayCount = 2 }, canAskServer: true).Log);
	}

	[Fact]
	public void Sent_AFinishedRewatch_IsLogged_WhenPlayCountRose()
	{
		var finished = Play(ended: "eof");

		Assert.True(AutoLogRules.Decide(finished, PlayedOnce, PlayedOnce with { PlayCount = 2 }, canAskServer: true).Log);
		Assert.False(AutoLogRules.Decide(finished, PlayedOnce, PlayedOnce, canAskServer: true).Log);
	}

	[Fact]
	public void Sent_WithoutABefore_NeedsTheSessionToEndAtTheEnd()
	{
		Assert.True(AutoLogRules.Decide(Play(ended: "stopped", last: (long)(Runtime * 0.9)), null, PlayedOnce, canAskServer: true).Log);
		Assert.False(AutoLogRules.Decide(Play(ended: "stopped", last: (long)(Runtime * 0.89)), null, PlayedOnce, canAskServer: true).Log);
	}

	[Fact]
	public void Sent_WhenTheServerCannotBeAsked_TheSessionsEndDecides()
	{
		Assert.True(AutoLogRules.Decide(Play(ended: "eof"), Unplayed, null, canAskServer: true).Log);
		Assert.True(AutoLogRules.Decide(Play(ended: "eof"), null, PlayedOnce, canAskServer: false).OnServer);
		Assert.False(AutoLogRules.Decide(Play(ended: "stopped", last: 20 * Minute, watched: 20 * Minute), Unplayed, null, canAskServer: false).Log);
	}

	[Fact]
	public void Failed_AtTheEnd_IsLoggedLocally()
	{
		var eof = AutoLogRules.Decide(Play("failed", ended: "eof", last: 30 * Minute), null, null, canAskServer: true);
		var ninety = AutoLogRules.Decide(Play("failed", ended: "replaced", last: (long)(Runtime * 0.95)), null, null, canAskServer: true);

		Assert.True(eof.Log);
		Assert.False(eof.OnServer);
		Assert.True(ninety.Log);
		Assert.False(ninety.OnServer);
	}

	[Fact]
	public void Failed_InTheMiddle_IsNotLogged()
	{
		Assert.False(AutoLogRules.Decide(Play("failed", ended: "stopped", last: 22 * Minute, watched: 22 * Minute), null, null, canAskServer: true).Log);
		Assert.False(AutoLogRules.Decide(Play("failed", ended: "error", last: 22 * Minute, watched: 22 * Minute), null, null, canAskServer: true).Log);
	}

	[Theory]
	[InlineData("none")]
	[InlineData("abandoned")]
	[InlineData("")]
	public void OtherOutcomes_AreNeverLogged(string outcome)
	{
		Assert.False(AutoLogRules.Decide(Play(outcome), Unplayed, PlayedOnce, canAskServer: true).Log);
	}

	[Fact]
	public void AShortPlay_IsIgnored_EvenWhenTheServerCountsIt()
	{
		// opened, jumped to the credits, let it end: 90 seconds watched
		var seekToEnd = Play(ended: "eof", start: 0, watched: TimeSpan.FromSeconds(90).Ticks);

		Assert.True(AutoLogRules.IsShortPlay(seekToEnd));
		Assert.False(AutoLogRules.Decide(seekToEnd, Unplayed, PlayedOnce, canAskServer: true).Log);
	}

	[Fact]
	public void FinishingAResumedPlay_IsNotShort()
	{
		// resumed at 95 %, watched the last two minutes
		var resumed = Play(ended: "eof", start: (long)(Runtime * 0.95), watched: 2 * Minute);

		Assert.False(AutoLogRules.IsShortPlay(resumed));
		Assert.True(AutoLogRules.Decide(resumed, Unplayed, PlayedOnce, canAskServer: true).Log);
	}

	[Fact]
	public void AShortEpisode_NeedsHalfItsRuntime_NotThreeMinutes()
	{
		var twoMinutes = 2 * Minute;

		Assert.False(AutoLogRules.IsShortPlay(Play(runtime: twoMinutes, watched: Minute)));
		Assert.True(AutoLogRules.IsShortPlay(Play(runtime: twoMinutes, watched: Minute - 1)));
		// with no runtime known at all: three minutes
		var unknown = new PlayCompleted("ps", Guid.NewGuid(), "sent", null, default, null, "eof", 0, 4 * Minute, null, 3 * Minute - 1);
		Assert.True(AutoLogRules.IsShortPlay(unknown));
		Assert.False(AutoLogRules.IsShortPlay(unknown with { WatchedTicks = 3 * Minute }));
	}
}
