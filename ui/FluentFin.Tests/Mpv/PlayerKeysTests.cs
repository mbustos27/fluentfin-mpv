using FluentFin.Controls;
using Windows.System;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv player controls: which keys the player page hands to the player before the focused element sees them
/// (MpvPlayerKeys, the page's PreviewKeyDown). The real key path, with real buttons, is in the fluentfin-controls harness.
/// </summary>
[Collection(AloneCollection.Name)]
public sealed class PlayerKeysTests
{
	[Theory]
	[InlineData(VirtualKey.Space)]
	[InlineData(VirtualKey.F)]
	[InlineData(VirtualKey.Right)]
	[InlineData(VirtualKey.M)]
	public void WhileTheVideoIsNotShown_NoKeyIsTaken(VirtualKey key)
	{
		// the loading screen (Cancel) or an error (Go back): its button has the keyboard
		Assert.False(MpvPlayerKeys.Takes(key, videoShown: false, onAButton: false));
		Assert.False(MpvPlayerKeys.Takes(key, videoShown: false, onAButton: true));
	}

	/// <summary>
	/// Seaglass 1.1.3: held, F and F11 switched full screen about 15 times a second and Escape left full screen and then the
	/// player (stopping it): their auto-repeat does nothing; the first press does its job.
	/// </summary>
	[Theory]
	[InlineData(VirtualKey.F, false, false)]
	[InlineData(VirtualKey.F, true, true)]
	[InlineData(VirtualKey.F11, true, true)]
	[InlineData(VirtualKey.Escape, true, true)]
	[InlineData(VirtualKey.Escape, false, false)]
	[InlineData(VirtualKey.Right, true, false)]   // a held arrow keeps seeking
	[InlineData(VirtualKey.Up, true, false)]      // a held Up keeps raising the volume
	[InlineData(VirtualKey.Space, true, false)]
	public void TheFullScreenKeysAndEscapeIgnoreTheirAutoRepeat(VirtualKey key, bool wasKeyDown, bool ignored) =>
		Assert.Equal(ignored, MpvPlayerKeys.IgnoresRepeat(key, wasKeyDown));

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

	[Fact]
	public void EscapeIsNeverTaken_ItIsThePagesAccelerator()
	{
		Assert.False(MpvPlayerKeys.Takes(VirtualKey.Escape, videoShown: true, onAButton: false));
		Assert.False(MpvPlayerKeys.Takes(VirtualKey.Escape, videoShown: true, onAButton: true));
	}

	[Theory]
	[InlineData(VirtualKey.Space)]
	[InlineData(VirtualKey.Enter)]
	[InlineData(VirtualKey.GamepadA)]
	public void SpaceAndEnter_PressAFocusedButton(VirtualKey key)
	{
		Assert.False(MpvPlayerKeys.Takes(key, videoShown: true, onAButton: true));
	}

	[Theory]
	[InlineData(VirtualKey.F)]
	[InlineData(VirtualKey.Right)]
	[InlineData(VirtualKey.Up)]
	[InlineData(VirtualKey.M)]
	[InlineData(VirtualKey.K)]
	public void ThePlayersOtherKeys_WorkFromAButtonToo(VirtualKey key)
	{
		Assert.True(MpvPlayerKeys.Takes(key, videoShown: true, onAButton: true));
	}

	[Fact]
	public void SpaceOverTheVideo_IsThePlayers()
	{
		Assert.True(MpvPlayerKeys.Takes(VirtualKey.Space, videoShown: true, onAButton: false));
		Assert.True(MpvPlayerKeys.Takes(VirtualKey.Space, videoShown: true, source: null));
		Assert.False(MpvPlayerKeys.PressedByKeys("not an element"));
	}
}
