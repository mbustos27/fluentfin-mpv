using FluentFin.Core.Shortcuts;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (the back gate's samesearch cell): after a search the keyboard stays in the search box, and the Back key did
/// nothing there: two searches, then Back, stayed on the second. The Back and Forward keys type nothing, so they step back
/// and forward from a text box too; every other key there is the box's.
/// </summary>
public sealed class BackKeysInTextTests
{
	[Fact]
	public void TheBackAndForwardKeys_StepFromATextBox_EveryOtherKeyIsTheBoxs()
	{
		Assert.Equal(AppShortcut.Back, AppShortcuts.For(AppShortcuts.Keys.BrowserBack, control: false, shift: false, alt: false, inTextInput: true));
		Assert.Equal(AppShortcut.Forward, AppShortcuts.For(AppShortcuts.Keys.BrowserForward, control: false, shift: false, alt: false, inTextInput: true));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.Left, control: false, shift: false, alt: true, inTextInput: true)); // Alt+Left stays the box's
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.R, control: false, shift: false, alt: false, inTextInput: true));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.Slash, control: false, shift: false, alt: false, inTextInput: true));
	}
}
