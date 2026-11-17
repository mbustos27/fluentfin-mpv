using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.3: a full-screen switch of the player page start to end (<see cref="FullScreenTransition"/>): what hides
/// the window, when the presenter changes, when the window shows again, and what ends a switch. The clock moves only when a
/// test moves it; the presenter change "blocks" by moving it.
/// </summary>
public sealed class FullScreenTransitionTests
{
	private TimeSpan _now;
	private readonly List<string> _calls = [];
	private readonly List<Action> _afterFrame = [];
	private TimeSpan _presenterBlocks = TimeSpan.FromMilliseconds(60);
	private Exception? _presenterThrows;
	private bool _full;
	private bool _minimized;

	private FullScreenTransition Make(FullScreenHide mode) => new(mode, () => _now, new FullScreenTransition.Host
	{
		Hide = how =>
		{
			_calls.Add($"hide {how}");
			return true;
		},
		Unhide = () => _calls.Add("unhide"),
		SetPresenter = fullScreen =>
		{
			_calls.Add($"presenter {fullScreen}");
			_now += _presenterBlocks;
			if (_presenterThrows is { } ex)
			{
				throw ex;
			}

			_full = fullScreen;
		},
		PrepareInset = fullScreen => _calls.Add($"inset {fullScreen}"),
		Settled = () => _calls.Add("settled"),
		AfterFrame = _afterFrame.Add,
		Failed = ex => _calls.Add($"failed {ex.Message}"),
		Shown = (elapsed, frames, how) => _calls.Add($"shown {how}"),
		Minimized = () => _minimized,
		SwitchWhenRestored = fullScreen => _calls.Add($"when restored {fullScreen}"),
	});

	private void Wait(double ms) => _now += TimeSpan.FromMilliseconds(ms);

	private void RunAfterFrame()
	{
		var work = _afterFrame.ToList();
		_afterFrame.Clear();
		work.ForEach(w => w());
	}

	[Theory]
	[InlineData(null, null, FullScreenHide.Cover)]
	[InlineData("cover", null, FullScreenHide.Cover)]
	[InlineData(" Cloak ", null, FullScreenHide.Cloak)]
	[InlineData("none", null, FullScreenHide.None)]
	[InlineData("off", null, FullScreenHide.None)]
	[InlineData("sparkles", null, FullScreenHide.Cover)]
	[InlineData("cover", "1", FullScreenHide.None)]   // 1.1.2's FLUENTFIN_NO_CLOAK=1 still turns hiding off
	[InlineData("cloak", "1", FullScreenHide.None)]
	[InlineData(null, "0", FullScreenHide.Cover)]
	public void The_hide_comes_from_the_environment(string? seaglassFullscreen, string? noCloak, FullScreenHide expected) =>
		Assert.Equal(expected, FullScreenCloak.ModeFrom(seaglassFullscreen, noCloak));

	[Theory]
	[InlineData(true, true, true)]
	[InlineData(false, true, false)]   // detached: the page is being left (Back, Esc twice, Alt+Left from full screen)
	[InlineData(true, false, false)]   // mpv's own controls: mpv's window
	[InlineData(false, false, false)]
	public void Only_a_page_showing_the_in_process_video_waits_and_hides(bool attached, bool composition, bool waits)
	{
		Assert.Equal(waits, FullScreenCloak.WaitsForLayout(attached, composition));
		Assert.Equal(waits, FullScreenCloak.Hides(FullScreenHide.Cover, attached, composition));
		Assert.False(FullScreenCloak.Hides(FullScreenHide.None, attached, composition));
	}

	/// <summary>Finding 2: leaving the player from full screen hid the window 172-281 ms for a layout that never came.</summary>
	[Fact]
	public void Leaving_the_page_switches_at_once_and_hides_nothing()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(false, videoAttached: false, attachedComposition: true);

		Assert.Equal(["presenter False", "settled"], _calls);
		Assert.False(transition.IsRunning);
		Assert.Equal(FullScreenHide.None, transition.Hidden);
		Assert.Empty(_afterFrame);
	}

	/// <summary>Finding 1: the cover goes up, a frame draws it, then the presenter changes; the subtitles' inset before all (finding 5).</summary>
	[Fact]
	public void The_cover_is_drawn_for_a_frame_before_the_presenter_changes()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		Assert.Equal(["inset True", "hide Cover"], _calls);
		Assert.True(transition.AwaitsCoverFrame);
		Assert.Equal(FullScreenHide.Cover, transition.Hidden);

		transition.Frame(holding: false);
		Assert.DoesNotContain("presenter True", _calls);
		RunAfterFrame();
		Assert.Equal(["inset True", "hide Cover", "presenter True"], _calls);
		Assert.True(_full);
		Assert.True(transition.IsRunning, "still hidden: no layout yet");
	}

	/// <summary>Finding 3: the window stays hidden while the picture is held (a held frame looked ~7 % too small), then two frames.</summary>
	[Fact]
	public void The_window_shows_once_the_layout_is_in_and_the_hold_has_ended()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		transition.Frame(false);
		RunAfterFrame();

		transition.Frame(false);
		Wait(16);
		Assert.True(transition.IsRunning, "no layout yet");
		transition.Layout();
		for (var i = 0; i < 4; i++)
		{
			transition.Frame(holding: true);
			Wait(16);
		}

		Assert.True(transition.IsRunning, "held");
		transition.Frame(false);
		Assert.True(transition.IsRunning, "the first frame without the hold is not yet shown");
		Wait(16);
		transition.Frame(false);
		Assert.False(transition.IsRunning);
		Assert.Equal(["inset True", "hide Cover", "presenter True", "unhide", "shown Cover", "settled"], _calls);
	}

	[Fact]
	public void A_hold_that_starts_again_waits_again()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		transition.Frame(false);
		RunAfterFrame();
		transition.Layout();
		transition.Frame(false);
		transition.Frame(holding: true);
		transition.Frame(false);
		Assert.True(transition.IsRunning);
		transition.Frame(false);
		Assert.False(transition.IsRunning);
	}

	[Fact]
	public void Frames_before_the_presenter_change_do_not_count()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		transition.Frame(false);    // the cover's frame: the presenter change is queued after it
		transition.Layout();        // (a layout of the old size, say)
		for (var i = 0; i < 5; i++)
		{
			transition.Frame(false);
		}

		Assert.True(transition.IsRunning);
		Assert.True(transition.Hidden == FullScreenHide.Cover);
		RunAfterFrame();
		transition.Frame(false);
		transition.Frame(false);
		Assert.False(transition.IsRunning);
	}

	/// <summary>
	/// Finding 3, cloaked: the 70 ms minimum counts from the presenter change's return, not from before it (the change blocks
	/// 44-103 ms, so counted from the start the window showed almost at once after it).
	/// </summary>
	[Fact]
	public void Cloaked_the_minimum_counts_from_the_presenter_change_returning()
	{
		_presenterBlocks = TimeSpan.FromMilliseconds(80);
		var transition = Make(FullScreenHide.Cloak);
		transition.Apply(true, true, true);
		Assert.Equal(["inset True", "hide Cloak", "presenter True"], _calls);
		transition.Layout();
		for (var i = 0; i < 4; i++)
		{
			Wait(16);
			transition.Frame(false);
		}

		// 144 ms from the start, 64 from the presenter's return
		Assert.True(transition.IsRunning);
		Wait(6);
		transition.Frame(false);
		Assert.False(transition.IsRunning);
		Assert.Contains("shown Cloak", _calls);
	}

	[Fact]
	public void The_limit_shows_the_window_whatever_the_layout()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		transition.Frame(false);
		RunAfterFrame();
		_now = FullScreenReveal.SoftLimit - TimeSpan.FromMilliseconds(1);
		transition.LimitTimer();
		Assert.True(transition.IsRunning);
		_now = FullScreenReveal.SoftLimit;
		transition.LimitTimer();
		Assert.False(transition.IsRunning);
		Assert.Equal(["inset True", "hide Cover", "presenter True", "unhide", "shown Cover", "settled"], _calls);
		Assert.True(FullScreenReveal.HardLimit > FullScreenReveal.SoftLimit, "the limit off the UI thread is the backstop");
	}

	/// <summary>
	/// A window minimized mid-switch renders no frame: at the limit the presenter change that waited for the cover's frame is
	/// made all the same (what was asked for happens), then the window shows.
	/// </summary>
	[Fact]
	public void At_the_limit_a_presenter_change_still_waiting_for_the_covers_frame_is_made()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		_now = FullScreenReveal.SoftLimit;
		transition.LimitTimer();
		Assert.Equal(["inset True", "hide Cover", "presenter True", "unhide", "shown Cover", "settled"], _calls);
		Assert.True(_full);
		Assert.False(transition.IsRunning);
	}

	[Fact]
	public void At_the_limit_a_presenter_change_queued_but_not_run_is_made_once()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		transition.Frame(false);
		_now = FullScreenReveal.SoftLimit;
		transition.LimitTimer();
		RunAfterFrame();
		Assert.Single(_calls, c => c == "presenter True");
		Assert.True(_full);
		Assert.False(transition.IsRunning);
	}

	/// <summary>
	/// Minimized while the cover was drawn: the presenter change on a minimized window left it neither minimized nor full
	/// screen (measured); it is made when the window is restored instead, and this switch ends.
	/// </summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Minimized_before_the_presenter_change_it_waits_for_the_restore(bool frameCame)
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		_minimized = true;
		if (frameCame)
		{
			transition.Frame(false);
			RunAfterFrame();
		}
		else
		{
			_now = FullScreenReveal.SoftLimit;
			transition.LimitTimer();
		}

		Assert.Equal(["inset True", "hide Cover", "when restored True", "unhide", "shown Cover", "settled"], _calls);
		Assert.False(_full);
		Assert.False(transition.IsRunning);
	}

	/// <summary>The page going mid-switch ends it; a presenter change still queued behind the cover's frame does not run.</summary>
	[Fact]
	public void The_page_going_ends_the_switch_and_drops_a_queued_presenter_change()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		transition.Frame(false);
		transition.End();
		Assert.Equal(["inset True", "hide Cover", "unhide", "shown Cover", "settled"], _calls);

		RunAfterFrame();
		Assert.DoesNotContain("presenter True", _calls);
		Assert.False(_full);
	}

	[Fact]
	public void A_presenter_change_that_throws_after_the_frame_shows_the_window_and_ends_the_switch()
	{
		_presenterThrows = new InvalidOperationException("no presenter");
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		transition.Frame(false);
		RunAfterFrame();
		Assert.Equal(["inset True", "hide Cover", "presenter True", "failed no presenter", "unhide", "shown Cover", "settled"], _calls);
		Assert.False(transition.IsRunning);
	}

	[Fact]
	public void A_presenter_change_that_throws_at_once_shows_the_window_and_is_thrown()
	{
		_presenterThrows = new InvalidOperationException("no presenter");
		var transition = Make(FullScreenHide.Cloak);
		Assert.Throws<InvalidOperationException>(() => transition.Apply(true, true, true));
		Assert.Equal(["inset True", "hide Cloak", "presenter True", "unhide", "settled"], _calls);
		Assert.Equal(FullScreenHide.None, transition.Hidden);
		Assert.False(transition.IsRunning);
	}

	[Fact]
	public void Without_hiding_the_switch_still_waits_for_its_layout()
	{
		var transition = Make(FullScreenHide.None);
		transition.Apply(true, true, true);
		Assert.Equal(["inset True", "presenter True"], _calls);
		Assert.True(transition.IsRunning);
		transition.Layout();
		transition.Frame(false);
		transition.Frame(false);
		Assert.False(transition.IsRunning);
		Assert.Equal(["inset True", "presenter True", "settled"], _calls);
	}

	[Fact]
	public void A_switch_after_the_last_one_starts_afresh()
	{
		var transition = Make(FullScreenHide.Cover);
		transition.Apply(true, true, true);
		transition.Frame(false);
		var stale = _afterFrame.ToList();
		transition.End();
		_afterFrame.Clear();

		_calls.Clear();
		transition.Apply(false, true, true);
		stale.ForEach(w => w());   // the first switch's queued change, late: dropped
		Assert.Equal(["inset False", "hide Cover"], _calls);
		transition.Frame(false);
		RunAfterFrame();
		Assert.Equal(["inset False", "hide Cover", "presenter False"], _calls);
	}
}
