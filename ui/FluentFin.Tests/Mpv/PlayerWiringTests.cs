namespace FluentFin.Tests.Mpv;

/// <summary>
/// Seaglass 1.1.3 (review): the player's WinUI code cannot run in these tests, so the few lines that carry a fix are read
/// from the source: each test fails with that fix reverted. The behaviour behind them is tested where it lives
/// (PlayerPageLeaveTests, MpvControlsViewModelTests) and measured off-screen (then-volume.ps1, fullscreen-burst.ps1).
/// </summary>
public sealed class PlayerWiringTests
{
	private static string Source(params string[] path) => File.ReadAllText(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. path])));

	/// <summary>The body of the method <paramref name="name"/> (its first declaration), braces matched.</summary>
	private static string Body(string source, string name)
	{
		var at = source.IndexOf($" {name}(", StringComparison.Ordinal);
		Assert.True(at >= 0, $"no method {name}");
		var open = source.IndexOf('{', at);
		var depth = 0;
		for (var i = open; i < source.Length; i++)
		{
			depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
			if (depth == 0)
			{
				return source[open..(i + 1)];
			}
		}

		throw new InvalidOperationException($"{name}: no end");
	}

	/// <summary>
	/// The volume fill glides from where the compositor draws it: 1.1.2 stopped a storyboard on every notch, which put the
	/// width back to its base (an empty bar), so every notch emptied the bar and filled it again.
	/// </summary>
	[Fact]
	public void The_volume_fill_glides_from_where_it_is_drawn_never_from_a_stopped_storyboard()
	{
		var controls = Source("Controls", "MpvPlayerControls.xaml.cs");
		var fill = Body(controls, "AnimateVolumeFill");
		Assert.Contains("InsertExpressionKeyFrame(0f, \"this.StartingValue\")", fill);
		Assert.Contains("StartAnimation(\"Size\"", fill);
		Assert.DoesNotContain("Storyboard", controls);
		Assert.DoesNotContain("DoubleAnimation", controls);
	}

	/// <summary>
	/// 1.2.0 (the interaction audit, item 8): a click on the video goes to the view model, which plays or pauses at once; no
	/// timer waits the double-click time any more (1.1.3's made the picture go on for half a second after a click).
	/// </summary>
	[Fact]
	public void A_click_on_the_video_plays_or_pauses_at_once_through_the_view_model()
	{
		var controls = Source("Controls", "MpvPlayerControls.xaml.cs");
		var tapped = Body(controls, "OnVideoTapped");
		Assert.Contains("VideoClicked()", tapped);
		Assert.DoesNotContain("TogglePause", tapped);
		Assert.DoesNotContain("Timer", tapped);
		Assert.DoesNotContain("TogglePause", Body(controls, "OnVideoDoubleTapped"));
		Assert.DoesNotContain("ClickWaitOver", controls);
	}

	/// <summary>
	/// 1.2.0 (the interaction audit, item 1): Escape on the player page never goes back (it stopped the video in a window,
	/// measured live in 0.4 s); it follows PlayerEscape: full screen off, else the controls shown.
	/// </summary>
	[Fact]
	public void Escape_never_leaves_the_player()
	{
		var escape = Body(Source("Views", "VideoPlayerPage.xaml.cs"), "OnEscape");
		Assert.Contains("PlayerEscape.For(", escape);
		Assert.DoesNotContain("GoBack", escape);
		Assert.DoesNotContain("Navigate", escape);
	}

	/// <summary>Leaving the page detaches the video before the switch ends, then restores the window (PlayerPageLeave).</summary>
	[Fact]
	public void Leaving_the_player_page_goes_through_the_leave_order()
	{
		var leave = Body(Source("Views", "VideoPlayerPage.xaml.cs"), "OnNavigatedFrom");
		Assert.Contains("PlayerPageLeave.Run(", leave);
		var detach = leave.IndexOf("DetachVideo", StringComparison.Ordinal);
		var end = leave.IndexOf("EndSwitch()", StringComparison.Ordinal);
		var restore = leave.IndexOf("LeaveFullScreen()", StringComparison.Ordinal);
		Assert.True(detach >= 0 && detach < end && end < restore, "detach, then end the switch, then restore");
	}

	/// <summary>
	/// Seaglass 1.1.3 integration: the page unloads while the window closes too, when the window's AppWindow is already gone.
	/// Unloaded's stop-waiting-for-the-restore read it unguarded: a NullReferenceException at every close from the player
	/// (then-season.ps1 ends there: "[ERR] Unhandled exception" at StopWaitingForRestore), and the rest of OnUnloaded (the
	/// key handler, the chrome, PlayerPageRemoval) never ran.
	/// </summary>
	[Fact]
	public void Unloading_during_the_close_never_reads_a_gone_AppWindow()
	{
		var page = Source("Views", "VideoPlayerPage.xaml.cs");
		var unloaded = Body(page, "OnUnloaded");
		Assert.Contains("StopWaitingForRestore()", unloaded);
		var stop = Body(page, "StopWaitingForRestore");
		Assert.DoesNotContain("App.MainWindow.AppWindow.", stop);
		Assert.Contains("App.MainWindow.AppWindow is { } appWindow", stop);
	}
}
