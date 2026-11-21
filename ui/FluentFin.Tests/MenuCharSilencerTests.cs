using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>
/// 1.5: Alt chords end without Windows' error sound. The window answers WM_MENUCHAR with MNC_CLOSE in its high word (0 there,
/// MNC_IGNORE, is what makes Windows beep); every other message is left to Windows. The running window's answer is checked
/// off-screen (then-alt-keys.ps1: SendMessage(WM_MENUCHAR) returns 0x10000, and Alt+Enter is answered).
/// </summary>
public sealed class MenuCharSilencerTests
{
	[Fact]
	public void WmMenuChar_IsAnsweredClose()
	{
		Assert.Equal((nint)0x10000, MenuCharSilencer.Answer(0x0120));
	}

	[Theory]
	[InlineData(0x0100u)] // WM_KEYDOWN
	[InlineData(0x0104u)] // WM_SYSKEYDOWN
	[InlineData(0x0106u)] // WM_SYSCHAR
	[InlineData(0x0112u)] // WM_SYSCOMMAND: Alt+Space and Alt+F4 stay Windows'
	[InlineData(0x0000u)]
	public void OtherMessages_AreLeftToWindows(uint message)
	{
		Assert.Null(MenuCharSilencer.Answer(message));
	}

	[Theory]
	[InlineData('\r', "Enter")]
	[InlineData('x', "x")]
	[InlineData('\x1b', "0x1B")]
	public void TheLogNamesTheKey(char c, string name)
	{
		Assert.Equal(name, MenuCharSilencer.KeyName(c));
	}
}
