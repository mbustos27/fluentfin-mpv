using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (the release gate's playback-basics, all four themes): Back from the player showed the season page at once and
/// asked the server behind it before the player's Stopped report had reached it; the play's own word (20 % watched) was told,
/// then the refresh's older answer (1 %) over it, and the episode's row kept "1% watched". What was told during the refresh
/// whose last word is older than one before it is told again with the newest (NavigationService.ReplayNewer).
/// </summary>
public sealed class KeptCardsNewerTests
{
	private static readonly DateTimeOffset At = new(2026, 11, 25, 0, 24, 0, TimeSpan.Zero);

	private static UserItemDataDto Data(int percent, DateTimeOffset? lastPlayed, bool played = false) =>
		new() { PlayedPercentage = percent, PlaybackPositionTicks = percent * 60_000_000L, LastPlayedDate = lastPlayed, Played = played };

	[Fact]
	public void ThePlaysWordThenTheRefreshsOlderAnswer_ThePlaysIsToldAgain()
	{
		var episode = Guid.NewGuid();
		var play = Data(20, At.AddSeconds(6));   // RecentPlays, after the Stopped report
		var stale = Data(1, At);                   // the refresh behind, asked before the report

		var again = KeptCards.NewerThanLastTold([(episode, play), (episode, stale)]);

		var told = Assert.Single(again);
		Assert.Equal(episode, told.Id);
		Assert.Same(play, told.Data);
	}

	[Fact]
	public void TheNewestLast_OrNothingNewer_NothingIsToldAgain()
	{
		var a = Guid.NewGuid();
		var b = Guid.NewGuid();
		var c = Guid.NewGuid();

		var again = KeptCards.NewerThanLastTold([
			(a, Data(1, At)), (a, Data(20, At.AddSeconds(6))),          // the newest came last
			(b, Data(5, At)),                                              // told once
			(c, Data(20, At.AddSeconds(6))), (c, Data(20, At.AddSeconds(6))), // the same date twice
		]);

		Assert.Empty(again);
	}

	[Fact]
	public void ALastWordWithNoDate_AMarkAsUnwatched_IsNeverToldOver()
	{
		var episode = Guid.NewGuid();

		var again = KeptCards.NewerThanLastTold([(episode, Data(20, At.AddSeconds(6))), (episode, Data(0, null))]);

		Assert.Empty(again);
	}

	[Fact]
	public void WhatAKeptPageMissed_EachItemsLastWord_UnlessAnEarlierOneIsNewer()
	{
		var e1 = Guid.NewGuid();
		var e2 = Guid.NewGuid();
		var play = Data(20, At.AddSeconds(6));
		var e2Last = Data(12, At.AddSeconds(10));

		var told = KeptCards.LatestTold([(e1, Data(1, At.AddSeconds(-60))), (e2, Data(10, At)), (e1, play), (e1, Data(1, At)), (e2, e2Last)]);

		Assert.Equal([e1, e2], told.Select(x => x.Id));
		Assert.Same(play, told[0].Data);   // the play's word, not the refresh's older answer told after it
		Assert.Same(e2Last, told[1].Data);
	}

	[Fact]
	public void EachItemOnItsOwn()
	{
		var e1 = Guid.NewGuid();
		var e2 = Guid.NewGuid();
		var newer = Data(30, At.AddSeconds(9));

		var again = KeptCards.NewerThanLastTold([(e1, newer), (e2, Data(10, At.AddSeconds(8))), (e1, Data(2, At)), (e2, Data(12, At.AddSeconds(10)))]);

		var told = Assert.Single(again);
		Assert.Equal(e1, told.Id);
		Assert.Same(newer, told.Data);
	}
}
