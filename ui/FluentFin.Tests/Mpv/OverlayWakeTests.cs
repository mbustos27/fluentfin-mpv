using FluentFin.Controls;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// Nixie 1.4.0, the user's report on 1.3.1: "we've lost the player controls overlay" ("i opened the player and then hit the
/// spacebar and it like locked me in keyboard only mode"). A move of the mouse always shows the bars, whatever XAML's pointer
/// events did: the heartbeat reads the cursor itself (CursorWakes), and the player hears every move, handled or not. No mode
/// to get out of: tools\ui-harness\offscreen\then-keys-matrix.ps1's overlay section checks every transition in every theme.
/// </summary>
public sealed class OverlayWakeTests
{
	[Fact]
	public void ACursorThatMovedOverThePlayer_WithTheAppInFront_ShowsTheBars()
	{
		Assert.True(MpvPlayerControls.CursorWakes((100, 100), (101, 100), overPlayer: true, inFront: true));
	}

	[Theory]
	[InlineData(false, true, "the cursor is not over the player (the title bar, another window)")]
	[InlineData(true, false, "another app is in front: its cursor is not ours")]
	public void NoWake_OffThePlayer_OrForAnotherApp(bool overPlayer, bool inFront, string why)
	{
		Assert.False(MpvPlayerControls.CursorWakes((100, 100), (140, 120), overPlayer, inFront), why);
	}

	[Fact]
	public void NoWake_ForACursorThatStayedStill_OrWasNotReadBefore()
	{
		// a cursor resting over the video must let the bars hide; the first reading has nothing to compare with
		Assert.False(MpvPlayerControls.CursorWakes((100, 100), (100, 100), overPlayer: true, inFront: true));
		Assert.False(MpvPlayerControls.CursorWakes(null, (100, 100), overPlayer: true, inFront: true));
	}

	[Fact]
	public void ThePlayerHearsEveryMove_HandledOrNot_AndTheHeartbeatReadsTheCursor()
	{
		var code = File.ReadAllText(FindUp("ui", "FluentFin", "Controls", "MpvPlayerControls.xaml.cs"));
		var xaml = File.ReadAllText(FindUp("ui", "FluentFin", "Controls", "MpvPlayerControls.xaml"));

		Assert.Contains("Root.AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);", code);
		Assert.DoesNotContain("PointerMoved=\"OnPointerMoved\"", xaml);
		var tick = code[code.IndexOf("private void OnTick(", StringComparison.Ordinal)..];
		// 1.7.3 fix round: after its guard for the close (nothing in the window's teardown), the method's own body
		Assert.Contains("WakeOnCursorMove();", tick[..tick.IndexOf("\n\t}", StringComparison.Ordinal)]);
	}

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
