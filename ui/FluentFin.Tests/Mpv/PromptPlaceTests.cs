using FluentFin.Controls;
using Microsoft.UI.Xaml;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// Nixie 1.3.1 (the UX review's DI-M4): the player's prompts (Skip, Next and its countdown's Watch credits) sit above the
/// bottom bar in every theme. Drive-in's nixie console is lifted off the foot by its margin and solid from its edge: the
/// prompts sat on the seek bar's end and over the time left.
/// </summary>
public sealed class PromptPlaceTests
{
	// the bars' styles: the shared one and Cinematic's on a scrim (its padding's top is see-through), Drive-in's console framed
	private static readonly (string Name, double Height, Thickness Margin, Thickness Padding, Thickness Border)[] Bars =
	[
		("shared", 140, new Thickness(0), new Thickness(32, 64, 32, 20), new Thickness(0)),
		("Cinematic", 150, new Thickness(0), new Thickness(96, 72, 96, 24), new Thickness(0)),
		("Drive-in", 132, new Thickness(40, 0, 40, 28), new Thickness(26, 18, 26, 14), new Thickness(2)),
		("Editorial", 156, new Thickness(0), new Thickness(48, 80, 48, 28), new Thickness(0)),
	];

	[Fact]
	public void ThePrompts_SitAboveTheBarsVisibleTop_InEveryTheme()
	{
		foreach (var (name, height, margin, padding, border) in Bars)
		{
			// the bar's visible top above the foot: the controls' top on a scrim, the frame's top for a console
			var visibleTop = margin.Bottom + height - (border.Top > 0 ? 0 : padding.Top);
			var top = MpvPlayerControls.BarTopAbove(height, margin, padding, border);
			var prompts = MpvPlayerControls.PromptMargin(true, top, margin, padding, border);

			Assert.True(prompts.Bottom > visibleTop, $"{name}: the prompts' foot {prompts.Bottom} is not above the bar's top {visibleTop}");
		}
	}

	[Fact]
	public void DriveIns_Prompts_ClearTheConsole_AndLineUpWithItsSide()
	{
		var (_, height, margin, padding, border) = Bars[2];

		var prompts = MpvPlayerControls.PromptMargin(true, MpvPlayerControls.BarTopAbove(height, margin, padding, border), margin, padding, border);

		Assert.Equal(28 + 132 + 12, prompts.Bottom);
		Assert.Equal(40, prompts.Right);
	}

	[Fact]
	public void TheSharedBar_KeepsItsPlace()
	{
		var (_, height, margin, padding, border) = Bars[0];

		var prompts = MpvPlayerControls.PromptMargin(true, MpvPlayerControls.BarTopAbove(height, margin, padding, border), margin, padding, border);

		Assert.Equal(140 - 64 + 12, prompts.Bottom);
		Assert.Equal(32, prompts.Right);
		// bars hidden: a small margin from the foot
		Assert.Equal(24, MpvPlayerControls.PromptMargin(false, 76, margin, padding, border).Bottom);
	}

	[Fact]
	public void ThePromptsRightEdge_IsTheThemesBarPadding_WhereFullScreenEnds()
	{
		// Nixie 1.4.0 (the UX review's CL-m14, E-m7, C-M12): 32 in every theme overhung Editorial's bar by 16 px, Cinematic's by 64
		foreach (var (name, height, margin, padding, border) in Bars)
		{
			var top = MpvPlayerControls.BarTopAbove(height, margin, padding, border);
			// the bar's controls end at its padding (a scrim), or the prompts rest on a framed console's rim
			var controlsEnd = border.Right > 0 ? margin.Right : margin.Right + padding.Right;
			foreach (var shown in new[] { true, false })
			{
				Assert.True(MpvPlayerControls.PromptMargin(shown, top, margin, padding, border).Right == controlsEnd, $"{name}: the prompts end {MpvPlayerControls.PromptMargin(shown, top, margin, padding, border).Right} from the right, the bar's controls {controlsEnd}");
			}
		}
	}
}

/// <summary>Nixie 1.3.1 (the UX review's DI-m2): the countdown's announcement names the control that stops it.</summary>
public sealed class CountdownAnnouncementTests
{
	[Theory]
	[InlineData("Keep watching")]
	[InlineData("Watch credits")]
	public void TheCountdown_NamesTheRealControl_NotCancel(string stop)
	{
		var said = MpvPlayerControls.CountdownAnnouncement(8, "The Fight", stop);

		Assert.Equal($"Next episode in 8 seconds: The Fight. Shift+N plays it now; {stop} keeps this one playing.", said);
		Assert.DoesNotContain("Cancel", said);
	}
}
