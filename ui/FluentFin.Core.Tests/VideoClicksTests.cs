using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0: a click on the video plays or pauses at once; a double click puts back the play state from before its first click
/// (<see cref="VideoClicks"/>). 1.1.3 waited the double-click time before every click's play or pause.
/// </summary>
public sealed class VideoClicksTests
{
	private readonly DateTimeOffset _t0 = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);
	private readonly VideoClicks _clicks = new(TimeSpan.FromMilliseconds(500));

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void A_double_click_within_the_time_puts_back_the_state_before_the_click(bool pausedBefore)
	{
		_clicks.Clicked(_t0, pausedBefore);
		Assert.Equal(pausedBefore, _clicks.DoubleClicked(_t0.AddMilliseconds(500)));
		Assert.Null(_clicks.DoubleClicked(_t0.AddMilliseconds(520)));
	}

	[Fact]
	public void A_double_click_long_after_a_click_puts_nothing_back()
	{
		_clicks.Clicked(_t0, pausedBefore: false);
		Assert.Null(_clicks.DoubleClicked(_t0.AddMilliseconds(501)));
	}

	[Fact]
	public void Another_play_or_pause_in_between_leaves_nothing_to_put_back()
	{
		_clicks.Clicked(_t0, pausedBefore: false);
		_clicks.Cancel();
		Assert.Null(_clicks.DoubleClicked(_t0.AddMilliseconds(100)));
		Assert.Null(new VideoClicks(TimeSpan.FromMilliseconds(500)).DoubleClicked(_t0));
	}

	[Theory]
	[InlineData(0, 500)]
	[InlineData(-5, 500)]
	[InlineData(200, 200)]
	[InlineData(60000, 500)]
	public void The_wait_is_the_systems_double_click_time_within_reason(int ms, int expected) =>
		Assert.Equal(TimeSpan.FromMilliseconds(expected), new VideoClicks(TimeSpan.FromMilliseconds(ms)).DoubleClickTime);
}
