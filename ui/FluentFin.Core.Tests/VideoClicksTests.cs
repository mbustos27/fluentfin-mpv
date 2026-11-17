using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.3: a click on the video plays or pauses after the double-click time; a double click is full screen only
/// (<see cref="VideoClicks"/>). 1.1.2 paused at the first click and unpaused at the second: the playback stuttered.
/// </summary>
public sealed class VideoClicksTests
{
	private readonly DateTimeOffset _t0 = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);
	private readonly VideoClicks _clicks = new(TimeSpan.FromMilliseconds(500));

	[Fact]
	public void A_single_click_plays_or_pauses_once_its_wait_is_over()
	{
		_clicks.Clicked();
		Assert.True(_clicks.IsPending);
		Assert.True(_clicks.WaitOver(_t0.AddMilliseconds(500)));
		Assert.False(_clicks.IsPending);
		Assert.False(_clicks.WaitOver(_t0.AddMilliseconds(600)), "once");
	}

	[Fact]
	public void A_double_click_drops_the_first_clicks_play_or_pause()
	{
		_clicks.Clicked();
		Assert.False(_clicks.DoubleClicked(_t0.AddMilliseconds(250)), "nothing to undo: nothing happened");
		Assert.False(_clicks.IsPending);
		Assert.False(_clicks.WaitOver(_t0.AddMilliseconds(500)), "and its timer, if it still fires, does nothing");
	}

	[Fact]
	public void A_double_click_just_after_the_wait_undoes_it()
	{
		_clicks.Clicked();
		Assert.True(_clicks.WaitOver(_t0));
		Assert.True(_clicks.DoubleClicked(_t0.AddMilliseconds(30)));
		Assert.False(_clicks.DoubleClicked(_t0.AddMilliseconds(60)), "once");
	}

	[Fact]
	public void A_double_click_long_after_a_click_undoes_nothing()
	{
		_clicks.Clicked();
		Assert.True(_clicks.WaitOver(_t0));
		Assert.False(_clicks.DoubleClicked(_t0.AddSeconds(3)));
	}

	[Theory]
	[InlineData(0, 500)]
	[InlineData(-5, 500)]
	[InlineData(200, 200)]
	[InlineData(60000, 500)]
	public void The_wait_is_the_systems_double_click_time_within_reason(int ms, int expected) =>
		Assert.Equal(TimeSpan.FromMilliseconds(expected), new VideoClicks(TimeSpan.FromMilliseconds(ms)).DoubleClickTime);
}
