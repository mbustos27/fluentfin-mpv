namespace FluentFin.Core.Mpv;

/// <summary>
/// Seaglass 1.1.3: how the window is kept from showing a half-done layout while the player switches into or out of full
/// screen (docs/research/fullscreen-smoothness.md). <c>SEAGLASS_FULLSCREEN</c> picks it, to compare on a real screen.
/// </summary>
public enum FullScreenHide
{
	/// <summary>The default: a black cover over the whole window (the title bar row too), drawn before the switch and taken
	/// away once the new layout is in; the screen shows black for that time, never what is behind the window.</summary>
	Cover,

	/// <summary>1.1.2's: the window cloaked (DWM), so whatever is behind it shows for that time.</summary>
	Cloak,

	/// <summary>Nothing hidden: the half-done frames show (<c>FLUENTFIN_NO_CLOAK=1</c> too).</summary>
	None,
}

/// <summary>
/// Seaglass: the window is hidden (covered or cloaked) for the length of a full-screen switch. The hide is remembered the
/// moment it is made, and anything that goes wrong afterwards shows the window again: it must never be left hidden.
/// </summary>
public static class FullScreenCloak
{
	/// <param name="cloak">Hides the window; true when it did.</param>
	/// <param name="uncloak">Shows it again.</param>
	/// <param name="remember">Records whether the window is hidden now (the page's state).</param>
	/// <param name="body">The rest of the switch.</param>
	public static void Run(Func<bool> cloak, Action uncloak, Action<bool> remember, Action body)
	{
		var cloaked = cloak();
		remember(cloaked);
		try
		{
			body();
		}
		catch
		{
			if (cloaked)
			{
				remember(false);
				uncloak();
			}

			throw;
		}
	}

	/// <summary>
	/// The hide asked for: <c>SEAGLASS_FULLSCREEN</c> = <c>cover</c> (the default, also for anything unknown), <c>cloak</c>
	/// or <c>none</c>; <c>FLUENTFIN_NO_CLOAK=1</c> (1.1.2's switch) means none whatever the other says.
	/// </summary>
	public static FullScreenHide ModeFrom(string? seaglassFullscreen, string? noCloak)
	{
		if (noCloak?.Trim() == "1")
		{
			return FullScreenHide.None;
		}

		return seaglassFullscreen?.Trim().ToLowerInvariant() switch
		{
			"cloak" => FullScreenHide.Cloak,
			"none" or "off" => FullScreenHide.None,
			_ => FullScreenHide.Cover,
		};
	}

	/// <summary>The hide from this process's environment.</summary>
	public static FullScreenHide FromEnvironment() =>
		ModeFrom(Environment.GetEnvironmentVariable("SEAGLASS_FULLSCREEN"), Environment.GetEnvironmentVariable("FLUENTFIN_NO_CLOAK"));

	/// <summary>
	/// A switch waits for its new layout only while the page shows FluentFin's in-process video: then the panel takes the new
	/// size and its picture's hold. Detached (the page is being left: Back, Esc twice, Alt+Left from full screen) no layout
	/// of the player comes, and waiting for one hid the window for its whole time limit (1.1.2: 172-281 ms). With mpv's own
	/// controls (mpv's window) nothing is waited for either.
	/// </summary>
	public static bool WaitsForLayout(bool videoAttached, bool attachedComposition) => videoAttached && attachedComposition;

	/// <summary>Whether this switch hides the window: one that waits for its layout, unless hiding is turned off.</summary>
	public static bool Hides(FullScreenHide mode, bool videoAttached, bool attachedComposition) =>
		mode != FullScreenHide.None && WaitsForLayout(videoAttached, attachedComposition);
}

/// <summary>
/// Seaglass 1.1.3: when a hidden full-screen switch shows the window again (UI thread; the page feeds it its own
/// <see cref="System.Diagnostics.Stopwatch"/> time and the frames XAML renders). It shows once the panel has its new size,
/// the picture's hold has ended (a held frame is the old picture scaled: shown, it looked a few percent too small), and
/// XAML has rendered <see cref="FramesAfterReady"/> frames since, and, cloaked, not before <see cref="Minimum"/> after the
/// presenter change returned (DWM uncloaks at once but shows what XAML committed a frame or two later; the cover goes in the
/// same stream of commits as the layout, so it needs no such wait). The minimum is timed from the return of the blocking
/// presenter change (44-103 ms), not from before it. <see cref="SoftLimit"/> shows it anyway (the UI thread's own timer);
/// <see cref="HardLimit"/> is the one kept off the UI thread (the cover's own compositor animation, a thread-pool timer for
/// the cloak), for a UI thread that does not come back.
/// </summary>
public sealed class FullScreenReveal(FullScreenHide mode)
{
	/// <summary>From the start of the switch, the UI thread shows the window whatever the layout.</summary>
	public static readonly TimeSpan SoftLimit = TimeSpan.FromMilliseconds(300);

	/// <summary>From the start of the switch, the window shows without the UI thread (a stalled one).</summary>
	public static readonly TimeSpan HardLimit = TimeSpan.FromMilliseconds(500);

	private TimeSpan? _presenterSet;
	private bool _layoutSeen;
	private int _readyFrames;

	public FullScreenHide Mode => mode;

	/// <summary>The shortest time from the presenter change's return to showing the window.</summary>
	public TimeSpan Minimum => mode == FullScreenHide.Cloak ? TimeSpan.FromMilliseconds(70) : TimeSpan.Zero;

	/// <summary>The rendered frames with the layout in and no hold, counting the first, before the window shows.</summary>
	public int FramesAfterReady => mode == FullScreenHide.Cloak ? 3 : 2;

	/// <summary>The presenter change returned at <paramref name="now"/> (the window has its new size).</summary>
	public void PresenterSet(TimeSpan now) => _presenterSet ??= now;

	/// <summary>The panel took its new size (its hold, if any, is in place).</summary>
	public void LayoutSeen() => _layoutSeen = true;

	/// <summary>
	/// XAML is rendering a frame at <paramref name="now"/>, the picture held or not: true when the window is to show now.
	/// Frames before the presenter change returned do not count (some render inside it, at the new window size).
	/// </summary>
	public bool Frame(TimeSpan now, bool holding)
	{
		if (_presenterSet is not { } set)
		{
			return false;
		}

		if (!_layoutSeen || holding)
		{
			// a hold that starts again (a second size change) waits again
			_readyFrames = 0;
			return false;
		}

		_readyFrames++;
		return _readyFrames >= FramesAfterReady && now - set >= Minimum;
	}

}

/// <summary>
/// Seaglass 1.1.3: one full-screen switch of the player page, start to end, apart from WinUI so every rule of it can be
/// tested (the page only wires it: <see cref="Host"/>'s actions, XAML's rendered frames, the panel's new size, a timer and
/// the page going). In order: nothing of the player's on the page (it is being left, or mpv's own window shows the
/// video): the presenter changes at once and the switch is done. Otherwise the subtitles' inset for the new size goes
/// first, the window is hidden (<see cref="FullScreenHide"/>; remembered at once, shown again if anything throws), the
/// presenter changes (with the cover: right after the first frame that draws it), and the window shows when
/// <see cref="FullScreenReveal"/> says, at its time limit, or when the page goes. UI thread only.
/// </summary>
public sealed class FullScreenTransition(FullScreenHide mode, Func<TimeSpan> clock, FullScreenTransition.Host host)
{
	/// <summary>What the page does for the switch.</summary>
	public sealed class Host
	{
		/// <summary>Hides the window this way (never None); true when it did.</summary>
		public required Func<FullScreenHide, bool> Hide { get; init; }

		/// <summary>Shows it again (whatever hid it).</summary>
		public required Action Unhide { get; init; }

		/// <summary>Changes the window's presenter (and the title bar with it): blocks for 40-100 ms.</summary>
		public required Action<bool> SetPresenter { get; init; }

		/// <summary>Sends the subtitles' inset for the size the video is about to have.</summary>
		public required Action<bool> PrepareInset { get; init; }

		/// <summary>The switch is over (the next request may go).</summary>
		public required Action Settled { get; init; }

		/// <summary>Runs this after the frame being rendered now (the dispatcher's queue).</summary>
		public required Action<Action> AfterFrame { get; init; }

		/// <summary>A presenter change that runs after a frame threw (it cannot be thrown to anyone): logged.</summary>
		public Action<Exception>? Failed { get; init; }

		/// <summary>The window showed again: after this long, this many rendered frames, hidden this way.</summary>
		public Action<TimeSpan, int, FullScreenHide>? Shown { get; init; }

		/// <summary>The window is minimized now (a presenter change then left it neither minimized nor full screen).</summary>
		public Func<bool>? Minimized { get; init; }

		/// <summary>Makes this switch once the window is restored (the page asks for it again then).</summary>
		public Action<bool>? SwitchWhenRestored { get; init; }
	}

	private TimeSpan _started;
	private FullScreenReveal? _reveal;

	/// <summary>The presenter change still to make (the cover's frame first): the state it goes to.</summary>
	private bool? _presenterAfterFrame;

	/// <summary>It is queued behind the frame that drew the cover.</summary>
	private bool _presenterQueued;

	private int _generation;
	private int _frames;

	/// <summary>A switch is waiting for its layout (the page listens to frames and the panel's size meanwhile).</summary>
	public bool IsRunning => _reveal is not null;

	/// <summary>What hides the window now (None: it shows).</summary>
	public FullScreenHide Hidden { get; private set; } = FullScreenHide.None;

	/// <summary>The cover is drawn and the presenter change waits for its frame.</summary>
	public bool AwaitsCoverFrame => _presenterAfterFrame is not null;

	/// <summary>The switch's time so far.</summary>
	public TimeSpan Elapsed => clock() - _started;

	/// <summary>
	/// Switches to <paramref name="fullScreen"/>: <paramref name="videoAttached"/> and <paramref name="attachedComposition"/> are
	/// the page's (<see cref="FullScreenCloak.WaitsForLayout"/>). Throws what the presenter change threw (the window shown again).
	/// </summary>
	public void Apply(bool fullScreen, bool videoAttached, bool attachedComposition)
	{
		_generation++;
		_started = clock();
		_frames = 0;
		_presenterAfterFrame = null;
		_presenterQueued = false;
		if (!FullScreenCloak.WaitsForLayout(videoAttached, attachedComposition))
		{
			host.SetPresenter(fullScreen);
			End();
			return;
		}

		host.PrepareInset(fullScreen);
		var hide = FullScreenCloak.Hides(mode, videoAttached, attachedComposition) ? mode : FullScreenHide.None;
		_reveal = new FullScreenReveal(hide);
		try
		{
			FullScreenCloak.Run(
				() => hide != FullScreenHide.None && host.Hide(hide),
				host.Unhide,
				hidden => Hidden = hidden ? hide : FullScreenHide.None,
				() =>
				{
					if (Hidden == FullScreenHide.Cover)
					{
						// the presenter changes once a frame with the cover in it has been rendered (Frame)
						_presenterAfterFrame = fullScreen;
					}
					else
					{
						SetPresenterNow(fullScreen);
					}
				});
		}
		catch
		{
			// shown again already (FullScreenCloak.Run): the switch is over
			End();
			throw;
		}
	}

	private void SetPresenterNow(bool fullScreen)
	{
		host.SetPresenter(fullScreen);
		_reveal?.PresenterSet(Elapsed);
	}

	/// <summary>The panel took its new size (and its picture's hold).</summary>
	public void Layout() => _reveal?.LayoutSeen();

	/// <summary>XAML renders a frame; <paramref name="holding"/>: the picture's hold is on.</summary>
	public void Frame(bool holding)
	{
		if (_reveal is not { } reveal)
		{
			return;
		}

		_frames++;
		if (_presenterAfterFrame is not null)
		{
			if (!_presenterQueued)
			{
				// this frame draws the cover: the presenter changes right after it
				_presenterQueued = true;
				var generation = _generation;
				host.AfterFrame(() =>
				{
					if (generation == _generation)
					{
						PresenterAfterFrame();
					}
				});
			}

			return;
		}

		if (reveal.Frame(Elapsed, holding))
		{
			End();
		}
	}

	/// <summary>
	/// The presenter change that waited for the cover's frame, once (after that frame, or at the limit when no frame came: a
	/// window minimized mid-switch renders none). Nothing once the switch has ended (the page went).
	/// </summary>
	private void PresenterAfterFrame()
	{
		if (_reveal is null || _presenterAfterFrame is not { } fullScreen)
		{
			return;
		}

		_presenterAfterFrame = null;
		if (host.Minimized?.Invoke() == true)
		{
			// minimized while the cover was drawn (1.1.2 changed the presenter before a minimize could come): made on the
			// restore instead, as a switch of its own; this one ends
			host.SwitchWhenRestored?.Invoke(fullScreen);
			End();
			return;
		}

		try
		{
			SetPresenterNow(fullScreen);
		}
		catch (Exception ex)
		{
			host.Failed?.Invoke(ex);
			End();
		}
	}

	/// <summary>The running switch's number: the page's limit timer belongs to the switch it was started for.</summary>
	public int Generation => _generation;

	/// <summary>
	/// The page's timer for <see cref="FullScreenReveal.SoftLimit"/> fired: the window shows whatever the layout. The tick
	/// is the limit (the timer can fire a fraction of a millisecond before the switch's own clock says so; a check of the
	/// clock then left the switch running for good, and F and Esc dead). A presenter change still waiting for the cover's
	/// frame is made first (what was asked for still happens).
	/// </summary>
	public void LimitReached()
	{
		if (_reveal is null)
		{
			return;
		}

		PresenterAfterFrame();
		if (_reveal is not null)
		{
			End();
		}
	}

	/// <summary>
	/// The window shows again and the switch is over (also when the page goes: a presenter change still waiting for the
	/// cover's frame is dropped): a request that waited goes next.
	/// </summary>
	public void End()
	{
		_reveal = null;
		_presenterAfterFrame = null;
		_presenterQueued = false;
		if (Hidden != FullScreenHide.None)
		{
			var how = Hidden;
			Hidden = FullScreenHide.None;
			host.Unhide();
			host.Shown?.Invoke(Elapsed, _frames, how);
		}

		host.Settled();
	}
}

/// <summary>
/// Seaglass 1.1.3: a hide's limit that runs on another thread (the cloak's thread-pool timer) acts only for the hide it was
/// set for: a limit from the switch before, firing late, must not show the next switch's window early. Thread-safe.
/// </summary>
public sealed class HideGeneration
{
	private readonly object _lock = new();
	private int _current;

	/// <summary>A new hide (or none: the last one ended): its number; every earlier number goes stale.</summary>
	public int Next()
	{
		lock (_lock)
		{
			return ++_current;
		}
	}

	/// <summary>Runs <paramref name="action"/> (under the lock) only while <paramref name="generation"/> is the current hide's.</summary>
	public bool RunIfCurrent(int generation, Action action)
	{
		lock (_lock)
		{
			if (generation != _current)
			{
				return false;
			}

			action();
			return true;
		}
	}
}

/// <summary>
/// Seaglass 1.1.3: the player page being left (navigated from). In this order: the video is detached first, so a request
/// that waited for the running switch switches at once, with no layout to wait for and nothing hidden; then the running
/// switch ends (its hide goes; a presenter change still waiting for the cover's frame is dropped); then the window is
/// restored, whatever the switch was doing. Ended first (1.1.3's first try), a waiting request started a new covered switch
/// on the page being left, and the restore saw its target and did nothing: the cover sat over the next page, or the window
/// stayed full screen.
/// </summary>
public static class PlayerPageLeave
{
	public static void Run(Action detachVideo, Action endSwitch, Action restoreWindow)
	{
		detachVideo();
		endSwitch();
		restoreWindow();
	}
}
