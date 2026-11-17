using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.3: leaving the player page in the middle of a full-screen switch (<see cref="PlayerPageLeave"/>), with the
/// page's real pieces wired as the page wires them: <see cref="FullScreenSwitch"/> applies through
/// <see cref="FullScreenTransition"/>, whose end settles the switch. The review's repro: F, Esc 35 ms later, Esc 10 ms after
/// that (the second Esc is Back).
/// </summary>
public sealed class PlayerPageLeaveTests
{
	private TimeSpan _now;
	private bool _full;
	private bool _attached = true;
	private bool _composition = true;
	private int _hidden;
	private readonly List<Action> _afterFrame = [];
	private readonly FullScreenTransition _transition;
	private readonly FullScreenSwitch _switch;

	public PlayerPageLeaveTests()
	{
		_transition = new FullScreenTransition(FullScreenHide.Cover, () => _now, new FullScreenTransition.Host
		{
			Hide = _ =>
			{
				_hidden++;
				return true;
			},
			Unhide = () => _hidden = 0,
			SetPresenter = fullScreen =>
			{
				_now += TimeSpan.FromMilliseconds(60);
				_full = fullScreen;
			},
			PrepareInset = _ => { },
			Settled = () => _switch!.Settled(),
			AfterFrame = _afterFrame.Add,
		});
		_switch = new FullScreenSwitch(() => _full, fullScreen => _transition.Apply(fullScreen, _attached, _composition));
	}

	private void RunAfterFrame()
	{
		var work = _afterFrame.ToList();
		_afterFrame.Clear();
		work.ForEach(w => w());
	}

	/// <summary>What the page does as it is left: the leave helper with the page's three steps.</summary>
	private void Leave() => PlayerPageLeave.Run(
		() => (_attached, _composition) = (false, false),
		_transition.End,
		() =>
		{
			// the view model's LeaveFullScreen: restore when the window is (or is going) full screen
			if (_switch.Target)
			{
				_switch.Request(false);
			}
		});

	private void AssertLeftWindowed()
	{
		RunAfterFrame();   // anything still queued behind a frame
		Assert.False(_full, "the window is windowed");
		Assert.Equal(0, _hidden);
		Assert.False(_transition.IsRunning, "no switch is left running on the page that went");
		Assert.False(_switch.IsSwitching);
		Assert.Empty(_afterFrame);
	}

	[Fact]
	public void F_then_Esc_then_Back_mid_switch_leaves_the_window_windowed_and_shown()
	{
		_switch.Request(true);       // F: the cover, then its frame, then the presenter
		_transition.Frame(false);
		RunAfterFrame();
		Assert.True(_full);
		Assert.Equal(FullScreenSwitch.Outcome.Queued, _switch.Request(false));   // Esc, 35 ms later: waits for the switch

		Leave();                     // the second Esc: Back
		AssertLeftWindowed();
	}

	[Fact]
	public void Leaving_before_the_covers_frame_drops_the_switch_and_stays_windowed()
	{
		_switch.Request(true);
		Leave();
		AssertLeftWindowed();
	}

	[Fact]
	public void Leaving_once_in_full_screen_restores_the_window()
	{
		_switch.Request(true);
		_transition.Frame(false);
		RunAfterFrame();
		_transition.Layout();
		_transition.Frame(false);
		_transition.Frame(false);
		Assert.False(_transition.IsRunning);
		Assert.True(_full);

		Leave();
		AssertLeftWindowed();
	}

	[Fact]
	public void Leaving_while_a_switch_out_waits_for_its_layout_ends_windowed()
	{
		_switch.Request(true);
		_transition.Frame(false);
		RunAfterFrame();
		_transition.Layout();
		_transition.Frame(false);
		_transition.Frame(false);
		_switch.Request(false);      // Esc: out, covered, waiting for its frame
		_transition.Frame(false);
		Leave();
		AssertLeftWindowed();
	}
}

/// <summary>Seaglass 1.1.3: a hide's limit from another thread acts only for its own hide (<see cref="HideGeneration"/>).</summary>
public sealed class HideGenerationTests
{
	[Fact]
	public void A_late_limit_of_the_switch_before_does_not_show_the_next_one()
	{
		var guard = new HideGeneration();
		var first = guard.Next();
		var shown = 0;
		var second = guard.Next();   // the first hide ended, the next began
		Assert.False(guard.RunIfCurrent(first, () => shown++));
		Assert.Equal(0, shown);
		Assert.True(guard.RunIfCurrent(second, () => shown++));
		Assert.Equal(1, shown);
		guard.Next();                // the second ended normally: its limit is stale too
		Assert.False(guard.RunIfCurrent(second, () => shown++));
		Assert.Equal(1, shown);
	}
}
