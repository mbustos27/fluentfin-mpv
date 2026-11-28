using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.9 (the scroll stream): the dev build's frame clock (HarnessFrames), which the scroll gate (scroll190) reads: what a
/// timed stretch's frames were, from their timestamps. Apart from WinUI.
/// </summary>
public sealed class HarnessFramesTests
{
	private const long Hz = 1000; // a tick a millisecond

	[Fact]
	public void SlowFrames_AreTheOnesOver50Ms_TogetherWithTheirTime()
	{
		// frames at 16, 32, 132 (100 ms), 148, 208 (60 ms), 224; the stop at 274 (50 ms: not over)
		long[] frames = [16, 32, 132, 148, 208, 224];
		var stats = FrameStats.Of(frames, 0, 274, Hz, [0, 0, 0, 0, 0, 0], 100);
		Assert.Equal(6, stats.Frames);
		Assert.Equal(274, stats.Ms);
		Assert.Equal(2, stats.Slow);
		Assert.Equal(160, stats.SlowMs);
		Assert.Equal(100, stats.LongestMs);
	}

	[Fact]
	public void AStallBeforeTheFirstFrame_OrAfterTheLast_IsAFrameToo()
	{
		// the UI thread busy from the start for 300 ms, and again 200 ms before the stop
		var stats = FrameStats.Of([300, 316], 0, 516, Hz, [0, 0], 100);
		Assert.Equal(2, stats.Slow);
		Assert.Equal(500, stats.SlowMs);
	}

	[Fact]
	public void ThePagesEnd_IsWhenTheOffsetFirstReachesItWithinAPixel()
	{
		var stats = FrameStats.Of([100, 200, 300, 400], 0, 500, Hz, [10, 998.5, 999.5, 1000], 1000);
		Assert.Equal(300, stats.ReachedEndMs);
		Assert.Null(FrameStats.Of([100, 200], 0, 300, Hz, [10, 20], 1000).ReachedEndMs);
		Assert.Null(FrameStats.Of([100], 0, 200, Hz, [0], double.NaN).ReachedEndMs); // no page scroller
	}

	[Fact]
	public void P95_IsTheFrameTimeNineteenInTwentyStayUnder()
	{
		// 19 frames of 10 ms, then one of 200 ms; the stop with the last frame
		var frames = Enumerable.Range(1, 19).Select(i => (long)i * 10).Append(390).ToArray();
		var stats = FrameStats.Of(frames, 0, 390, Hz, new double[frames.Length], 100);
		Assert.Equal(10, stats.P95Ms);
		Assert.Equal(200, stats.LongestMs);
	}
}
