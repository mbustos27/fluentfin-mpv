using FluentFin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.7.4 (a friend's report on 1.7.x, playing in a window: "the hud is like almost invis / u can see it v er feitnly /
/// and still click it"; opening the episode again cleared it): the bars always end whole or gone.
/// <para>
/// They fade by implicit animations that XAML plays on the compositor as their Visibility changes (<see cref="ConfigureFade"/>).
/// A fade the compositor stops short leaves the bars where it stopped: shown (Visible, so they take clicks) and drawn at a few
/// percent, and nothing puts them back until the next hide and show. So, once the longest fade is over after each change of the
/// bars, of the player itself (opened, the video come) or of the window (its size, scale, shown or minimized), and at each
/// heartbeat when the compositor says the bars were left short, shown bars glide back whole and in place from where they are
/// drawn. Hidden bars need nothing: collapsed, they are neither drawn nor hit. Off-screen before this, 2 of 9 runs began with
/// the bars left at about 96 % (what cut their fade was not found); tools\ui-harness\offscreen\then-174-hud.ps1's cut part
/// makes the report's state on purpose and fails without this.
/// </para>
/// </summary>
public sealed partial class MpvPlayerControls
{
	/// <summary>How long after a change the bars are settled: the longest fade (0.3 s) and a few frames.</summary>
	internal static readonly TimeSpan BarsSettleAfter = TimeSpan.FromMilliseconds(450);

	private DispatcherQueueTimer? _barsSettle;
	private long _barsChangedAt;
	private XamlRoot? _barsWindow;
	private bool _barsRepairLogged;

	/// <summary>The bars or the window changed: the bars are settled once the fade is over (a later change starts the wait again).</summary>
	private void SettleBarsLater()
	{
		// 1.7.4 integration (1.7.3's close crash class, the review's m2): nothing once the app is closing or the player has gone.
		// The Visibility callback, the view model (still subscribed) and the window hiding at the close all come here
		if (App.IsClosing || !IsLoaded)
		{
			return;
		}

		_barsChangedAt = Environment.TickCount64;
		if (_barsSettle is null)
		{
			_barsSettle = DispatcherQueue.CreateTimer();
			_barsSettle.IsRepeating = false;
			_barsSettle.Interval = BarsSettleAfter;
			// through UiDispatch.Tick: a tick in the window's teardown does nothing, and one that throws is logged, not fatal
			_barsSettle.Tick += UiDispatch.Tick("the player's bars' settle", OnBarsSettle);
		}

		_barsSettle.Stop();
		_barsSettle.Start();
	}

	private void OnBarsSettle()
	{
		// a tick that came due while the UI thread was busy runs even after a later change restarted the wait: that change's
		// fade may not even have begun (seen in the harness's busy-thread cases), so it waits for its own tick
		if (Environment.TickCount64 - _barsChangedAt >= BarsSettleAfter.TotalMilliseconds)
		{
			SettleBars();
		}
	}

	/// <summary>
	/// The player came into the window (its bars fade in as they come: nothing is read until that is over) and watches the
	/// window's size, scale and visibility (minimized, restored), which may cut a fade running through them; or it went (null).
	/// </summary>
	private void WatchWindowForBars(XamlRoot? root)
	{
		if (_barsWindow is { } old)
		{
			old.Changed -= OnBarsWindowChanged;
		}

		_barsWindow = root;
		if (root is not null)
		{
			root.Changed += OnBarsWindowChanged;
			SettleBarsLater();
		}
		else
		{
			_barsSettle?.Stop();
		}
	}

	private void OnBarsWindowChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SettleBarsLater();

	/// <summary>The heartbeat's look: the compositor left a shown bar (or the Next prompt) short of whole after its fade was over.</summary>
	private void SettleBarsIfLeftHalfway()
	{
		if (!IsLoaded || Visibility != Visibility.Visible)
		{
			return;
		}

		if (LeftHalfway(Environment.TickCount64 - _barsChangedAt, [.. Settled().Select(e => (ShownForSettle(e), DrawnOpacity(e)))]))
		{
			SettleBars();
		}
	}

	/// <summary>
	/// Whether shown bars were left short of whole: <paramref name="shown"/> (the bars are meant to show), their fade over
	/// (<paramref name="sinceChangeMs"/> since the last change) and either bar's opacity, as the compositor last set it, under
	/// whole. While a fade runs, the compositor keeps the value from before it: nothing is read then.
	/// </summary>
	internal static bool BarsLeftHalfway(bool shown, long sinceChangeMs, float topOpacity, float bottomOpacity) =>
		LeftHalfway(sinceChangeMs, (shown, topOpacity), (shown, bottomOpacity));

	/// <summary>
	/// 1.7.4 integration (the review's m1): the same for any faded element that takes clicks, each judged on its own
	/// (the Next prompt shows near the end whether the bars do or not). Only shown ones count: a collapsed one is neither
	/// drawn nor hit.
	/// </summary>
	internal static bool LeftHalfway(long sinceChangeMs, params (bool Shown, float Opacity)[] elements) =>
		sinceChangeMs >= BarsSettleAfter.TotalMilliseconds && elements.Any(e => e.Shown && e.Opacity < Whole);

	/// <summary>
	/// What the settle puts right: the bars, and the Next prompt (NextPill), which has the same fade (<see cref="ConfigureFade"/>)
	/// and takes clicks: left faint, a click meant for the video would start the next episode. The toast takes none.
	/// </summary>
	private FrameworkElement[] Settled() => [TopBar, BottomBar, NextPill];

	/// <summary>Whether an element is shown and so settled: the bars while the controls show, the Next prompt while it shows.</summary>
	private bool ShownForSettle(FrameworkElement element) =>
		element.Visibility == Visibility.Visible && (element == NextPill || Controls is { ControlsVisible: true });

	/// <summary>Whole enough: under 98 % is a fade left short; the rest no one sees.</summary>
	private const float Whole = 0.98f;

	/// <summary>A bar's opacity as the compositor holds it (a fade's end value, or where a stopped one left it).</summary>
	private static float DrawnOpacity(UIElement bar) => ElementCompositionPreview.GetElementVisual(bar).Opacity;

	/// <summary>How long shown bars take to glide back to whole from where a cut fade left them.</summary>
	internal static readonly TimeSpan BarsGlide = TimeSpan.FromMilliseconds(120);

	/// <summary>
	/// Shown bars (and a shown Next prompt) put back whole and in place (any fade still on them ends there); hidden ones are
	/// left to their fade.
	/// </summary>
	internal void SettleBars()
	{
		// the player hidden (no video yet, or gone): its bars fade with it, and in again as it shows (a settle follows that)
		if (!IsLoaded || Visibility != Visibility.Visible)
		{
			return;
		}

		foreach (var bar in Settled())
		{
			// each only while it shows (the bars with the controls, the prompt near the end)
			if (!ShownForSettle(bar))
			{
				continue;
			}

			var visual = ElementCompositionPreview.GetElementVisual(bar);
			var left = visual.Opacity;
			if (left < Whole && !_barsRepairLogged)
			{
				// once per player: the evidence of the report, should it come again
				_barsRepairLogged = true;
				App.GetService<ILogger<MpvPlayerControls>>().LogInformation("[player] the bars were at {Percent:0} % once their fade was due to be over (a fade cut short, or not begun on a busy UI thread): drawn whole again ({State})", left * 100, OverlayState());
			}

			// from where the bar is drawn now, any fade still on it replaced rather than stopped (a stop writes the value it
			// stopped at back to the visual, which could land after one set here). Whole bars glide nowhere
			visual.StartAnimation("Opacity", Glide(visual.Compositor, 1f));
			visual.StartAnimation("Translation.Y", Glide(visual.Compositor, 0f));
		}
	}

	private static ScalarKeyFrameAnimation Glide(Compositor compositor, float to)
	{
		var glide = compositor.CreateScalarKeyFrameAnimation();
		glide.InsertExpressionKeyFrame(0f, "this.StartingValue");
		glide.InsertKeyFrame(1f, to);
		glide.Duration = BarsGlide;
		return glide;
	}

	/// <summary>
	/// The harness's cut fade (HarnessPointer, dev builds): the bars' fades stopped where they are now, as the compositor stops
	/// one short; what is left is what <see cref="SettleBars"/> must put right (tools\ui-harness\offscreen\then-174-hud.ps1).
	/// </summary>
	internal void HarnessCutFades()
	{
		foreach (var bar in Settled())
		{
			var visual = ElementCompositionPreview.GetElementVisual(bar);
			visual.StopAnimation("Opacity");
			visual.StopAnimation("Translation.Y");
		}
	}
}
