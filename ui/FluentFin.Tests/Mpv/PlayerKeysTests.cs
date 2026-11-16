using FluentFin.Controls;
using Windows.System;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv player controls: which keys the player page hands to the player before the focused element sees them
/// (MpvPlayerKeys, the page's PreviewKeyDown). The real key path, with real buttons, is in the fluentfin-controls harness.
/// </summary>
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
