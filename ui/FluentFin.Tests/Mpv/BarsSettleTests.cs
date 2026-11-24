using FluentFin.Controls;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// Nixie 1.7.4, a friend's report on 1.7.x: the player's bars stuck at about 5 % over the video, still taking clicks, until the
/// episode was opened again. A fade the compositor stops short leaves the bars where it stopped; the bars are now settled after
/// each change of the bars or the window, and at each heartbeat when the compositor says they were left short. What is drawn is
/// checked on the off-screen harness (tools\ui-harness\offscreen\then-174-hud.ps1: its cut part fails without the settle).
/// </summary>
public sealed class BarsSettleTests
{
	[Fact]
	public void ShownBarsLeftShortOfWhole_AfterTheirFade_AreSettled()
	{
		Assert.True(MpvPlayerControls.BarsLeftHalfway(shown: true, sinceChangeMs: 2000, topOpacity: 0.05f, bottomOpacity: 0.05f));
		Assert.True(MpvPlayerControls.BarsLeftHalfway(shown: true, sinceChangeMs: 2000, topOpacity: 1f, bottomOpacity: 0.6f));
	}

	[Theory]
	[InlineData(true, 2000, 1f, 1f, "whole: nothing to do")]
	[InlineData(true, 100, 0f, 0f, "a fade in runs: the compositor keeps the value from before it until it ends")]
	[InlineData(false, 2000, 0.05f, 0.05f, "hidden: collapsed bars are neither drawn nor hit, whatever their opacity")]
	public void NothingIsSettled_WhenWhole_InAFade_OrHidden(bool shown, long sinceChangeMs, float top, float bottom, string why)
	{
		Assert.False(MpvPlayerControls.BarsLeftHalfway(shown, sinceChangeMs, top, bottom), why);
	}

	[Fact]
	public void TheSettleWaitsOutTheLongestFade()
	{
		// the hide fades over 0.3 s (ConfigureFade): settled before that, a running fade would be cut to whole
		Assert.True(MpvPlayerControls.BarsSettleAfter >= TimeSpan.FromMilliseconds(300) + TimeSpan.FromMilliseconds(100));
		Assert.True(MpvPlayerControls.BarsSettleAfter <= TimeSpan.FromSeconds(1));
	}

	[Fact]
	public void EveryChange_TheWindow_AndTheHeartbeat_LeadToTheSettle()
	{
		var code = File.ReadAllText(FindUp("ui", "FluentFin", "Controls", "MpvPlayerControls.xaml.cs"));
		var bars = File.ReadAllText(FindUp("ui", "FluentFin", "Controls", "MpvPlayerControls.Bars.cs"));

		// a change of the bars' visibility (a call commented out does not count)
		var changed = code[code.IndexOf("if (e.PropertyName == nameof(MpvControlsViewModel.ControlsVisible))", StringComparison.Ordinal)..];
		Assert.Matches(Live("SettleBarsLater();"), changed[..changed.IndexOf('}')]);
		// new controls, and the window's size, scale and visibility, watched while the player is loaded
		Assert.Matches(Live("self.SettleBarsLater();"), code);
		// the player hidden and shown again with its video: XAML fades the bars out and in with it, settled after that
		Assert.Matches(Live("RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SettleBarsLater());"), code);
		Assert.Matches(Live("WatchWindowForBars(XamlRoot);"), code);
		Assert.Matches(Live("WatchWindowForBars(null);"), code);
		Assert.Matches(Live("root.Changed += OnBarsWindowChanged;"), bars);
		// the heartbeat
		var tick = code.Replace("\r\n", "\n", StringComparison.Ordinal);
		tick = tick[tick.IndexOf("private void OnTick(", StringComparison.Ordinal)..];
		// the whole method (1.7.3's closing guard opens it with a block of its own)
		Assert.Matches(Live("SettleBarsIfLeftHalfway();"), tick[..tick.IndexOf("\n\t}", StringComparison.Ordinal)]);
		// whole and in place, whatever fade was on them
		var settle = bars[bars.IndexOf("internal void SettleBars()", StringComparison.Ordinal)..];
		settle = settle[..settle.IndexOf("internal void HarnessCutFades()", StringComparison.Ordinal)];
		Assert.Matches(Live("visual.StartAnimation(\"Opacity\", Glide(visual.Compositor, 1f));"), settle);
		Assert.Matches(Live("visual.StartAnimation(\"Translation.Y\", Glide(visual.Compositor, 0f));"), settle);
		// from where the bars are drawn (no stop first: a stopped fade's value would come back after the one set)
		Assert.DoesNotContain("StopAnimation", settle);
		Assert.Contains("glide.InsertExpressionKeyFrame(0f, \"this.StartingValue\");", bars);
	}

	// ---- 1.7.4 integration: the review's Minors ------------------------------------------------------------------------

	[Fact]
	public void TheNextPrompt_LeftFaint_IsSettled_WhetherTheBarsShowOrNot()
	{
		// m1: the Next prompt has the bars' fade and takes clicks (a click meant for the video started the next episode)
		Assert.True(MpvPlayerControls.LeftHalfway(2000, (false, 0f), (false, 0f), (true, 0.05f)));
		Assert.False(MpvPlayerControls.LeftHalfway(2000, (false, 0f), (false, 0f), (false, 0.05f)));
		Assert.False(MpvPlayerControls.LeftHalfway(100, (false, 0f), (false, 0f), (true, 0.05f)));

		var code = File.ReadAllText(FindUp("ui", "FluentFin", "Controls", "MpvPlayerControls.xaml.cs"));
		var bars = File.ReadAllText(FindUp("ui", "FluentFin", "Controls", "MpvPlayerControls.Bars.cs"));
		// the settle, the heartbeat's look and the harness's cut all take the prompt; each element only while it shows
		Assert.Matches(Live("private FrameworkElement[] Settled() => [TopBar, BottomBar, NextPill];"), bars);
		Assert.Contains("element.Visibility == Visibility.Visible && (element == NextPill || Controls is { ControlsVisible: true })", bars);
		var settle = bars[bars.IndexOf("internal void SettleBars()", StringComparison.Ordinal)..];
		Assert.Contains("foreach (var bar in Settled())", settle[..settle.IndexOf("internal void HarnessCutFades()", StringComparison.Ordinal)]);
		Assert.Contains("if (!ShownForSettle(bar))", settle);
		// the prompt coming starts the wait, as the bars do
		var changed = code[code.IndexOf("else if (e.PropertyName == nameof(MpvControlsViewModel.IsNextPromptVisible))", StringComparison.Ordinal)..];
		Assert.Matches(Live("SettleBarsLater(); // 1.7.4 integration"), changed[..changed.IndexOf('}')]);
	}

	[Fact]
	public void TheSettle_DoesNothingOnceTheAppIsClosing_OrThePlayerHasGone()
	{
		// m2 (1.7.3's close crash class): the timer is never started then, and its tick goes through UiDispatch.Tick
		var bars = File.ReadAllText(FindUp("ui", "FluentFin", "Controls", "MpvPlayerControls.Bars.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
		var later = bars[bars.IndexOf("private void SettleBarsLater()", StringComparison.Ordinal)..];
		later = later[..later.IndexOf("private void OnBarsSettle()", StringComparison.Ordinal)];
		Assert.Contains("private void SettleBarsLater()\n\t{\n\t\t// 1.7.4 integration", later);
		Assert.Matches(Live("if (App.IsClosing || !IsLoaded)"), later);
		Assert.True(later.IndexOf("if (App.IsClosing || !IsLoaded)", StringComparison.Ordinal) < later.IndexOf("_barsChangedAt =", StringComparison.Ordinal));
		Assert.Matches(Live("_barsSettle.Tick += UiDispatch.Tick(\"the player's bars' settle\", OnBarsSettle);"), later);
		Assert.DoesNotContain("Tick += OnBarsSettle", bars);
	}

	[Fact]
	public void AnUnloadedAfterANewLoaded_KeepsTheHeartbeatAndTheSettle()
	{
		// m4 (for 1.9's picture in picture): a player moved gets its new Loaded before the old place's Unloaded
		var code = File.ReadAllText(FindUp("ui", "FluentFin", "Controls", "MpvPlayerControls.xaml.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
		var unloaded = code[code.IndexOf("Unloaded += (_, _) =>", StringComparison.Ordinal)..];
		unloaded = unloaded[..unloaded.IndexOf("};", StringComparison.Ordinal)];
		var guard = unloaded.IndexOf("if (IsLoaded)\n\t\t\t{\n\t\t\t\treturn;\n\t\t\t}", StringComparison.Ordinal);
		Assert.True(guard > 0, "the Unloaded handler returns while the player is loaded again");
		Assert.True(guard < unloaded.IndexOf("StopTick();", StringComparison.Ordinal));
		Assert.True(guard < unloaded.IndexOf("WatchWindowForBars(null);", StringComparison.Ordinal));
		// and a second Loaded doesn't listen to the glass mode twice
		var loaded = code[code.IndexOf("Loaded += (_, _) =>", StringComparison.Ordinal)..];
		Assert.Matches(Live("GlassMode.Changed -= OnGlassModeChanged;"), loaded[..loaded.IndexOf("};", StringComparison.Ordinal)]);
	}

	/// <summary>A statement on a line of its own that is not commented out.</summary>
	private static System.Text.RegularExpressions.Regex Live(string statement) =>
		new($@"(?m)^\s*{System.Text.RegularExpressions.Regex.Escape(statement)}");

	private static string FindUp(params string[] parts)
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine([dir.FullName, .. parts]);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new FileNotFoundException(Path.Combine(parts));
	}
}
