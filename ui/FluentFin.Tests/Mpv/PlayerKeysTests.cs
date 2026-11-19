using FluentFin.Controls;
using FluentFin.Core.Mpv;
using Windows.System;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv player controls: the WinUI side of the player page's key router (MpvPlayerKeys; the decisions themselves are
/// FluentFin.Core's PlayerKeyRouter, tested as a matrix in PlayerKeyRouterTests). The real key path, with real buttons, is in
/// the off-screen key matrix (tools\ui-harness\offscreen\then-keys-matrix.ps1) and the fluentfin-controls harness.
/// </summary>
[Collection(AloneCollection.Name)]
public sealed class PlayerKeysTests
{
	/// <summary>Nixie 1.3.1: nothing focused is the player's (the router's kind needs no window for it); so is anything not an element.</summary>
	[Fact]
	public void NothingFocused_IsThePlayers()
	{
		Assert.Equal(PlayerFocus.Player, MpvPlayerKeys.FocusOf(null));
		Assert.Equal(PlayerFocus.Player, MpvPlayerKeys.FocusOf("not an element"));
		Assert.Equal("SeekBar", MpvPlayerKeys.SeekBarName);
	}

	/// <summary>
	/// Seaglass 1.1.3: held, F and F11 switched full screen about 15 times a second and Escape left full screen and then the
	/// player (stopping it): their auto-repeat does nothing; the first press does its job. (The router's own repeat rule,
	/// wider since 1.3.1, is PlayerKeyRouterTests'.)
	/// </summary>
	[Theory]
	[InlineData(VirtualKey.F, false, false)]
	[InlineData(VirtualKey.F, true, true)]
	[InlineData(VirtualKey.F11, true, true)]
	[InlineData(VirtualKey.Escape, true, true)]
	[InlineData(VirtualKey.Escape, false, false)]
	[InlineData(VirtualKey.Right, true, false)]   // a held arrow keeps seeking
	[InlineData(VirtualKey.Up, true, false)]      // a held Up keeps raising the volume
	public void TheFullScreenKeysAndEscapeIgnoreTheirAutoRepeat(VirtualKey key, bool wasKeyDown, bool ignored) =>
		Assert.Equal(ignored, MpvPlayerKeys.IgnoresRepeat(key, wasKeyDown));

	/// <summary>
	/// Nixie 1.3.1 (the double click "kinda weird"): a click on a bar's scrim where it only fades into the picture is the
	/// video's: the bottom bar's top padding, the top bar's bottom padding. The rest of a bar is the controls'.
	/// </summary>
	[Theory]
	[InlineData(10, 200, true, true)]     // the bottom bar's fade (top padding 64)
	[InlineData(63, 200, true, true)]
	[InlineData(64, 200, true, false)]    // its controls
	[InlineData(190, 200, true, false)]
	[InlineData(150, 160, false, true)]   // the top bar's fade (bottom padding 56)
	[InlineData(104, 160, false, false)]  // its Back and title
	[InlineData(20, 160, false, false)]
	public void TheScrimsFade_IsTheVideos(double y, double height, bool bottomBar, bool video) =>
		Assert.Equal(video, MpvPlayerControls.InScrim(y, height,
			bottomBar ? new Microsoft.UI.Xaml.Thickness(32, 64, 32, 20) : new Microsoft.UI.Xaml.Thickness(24, 16, 32, 56), bottomBar));

	/// <summary>The harness's mouse (HarnessPointer) never listens in a release package, and only when asked for exactly.</summary>
	[Theory]
	[InlineData("1", false, true)]
	[InlineData("1", true, false)]
	[InlineData(null, false, false)]
	[InlineData("true", false, false)]
	[InlineData("0", false, false)]
	public void TheHarnessPointer_ListensInDevBuildsOnlyWhenAsked(string? value, bool release, bool listens) =>
		Assert.Equal(listens, FluentFin.Services.HarnessPointer.Listens(value, release));

	/// <summary>Seaglass 1.1.3: the subtitles' inset for a video height (the full-screen switch sends it for the coming one).</summary>
	[Theory]
	[InlineData(160, 64, 1000, 0.128)]
	[InlineData(160, 64, 1440, 0.08888888888888889)]
	[InlineData(20, 64, 1000, 0)]      // a bar shorter than half its padding covers nothing
	public void TheBottomBarCoversItsShareOfTheVideosHeight(double bar, double padding, double height, double share) =>
		Assert.Equal(share, MpvPlayerControls.BottomBarCover(bar, padding, height)!.Value, 6);

	[Theory]
	[InlineData(0, 64, 1000)]
	[InlineData(160, 64, 0)]
	public void AnUnmeasuredBarOrVideoGivesNoInset(double bar, double padding, double height) =>
		Assert.Null(MpvPlayerControls.BottomBarCover(bar, padding, height));
}
