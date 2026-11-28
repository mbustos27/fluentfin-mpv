using FluentFin.Views;
using Windows.System;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.9 (the scroll stream; older than 1.9): Page Down and Page Up on the Collections page (they did nothing there: three
/// Page Downs from a tile moved the page 0 px). How far each moves it, apart from WinUI.
/// </summary>
public sealed class CollectionsPageKeysTests
{
	[Theory]
	[InlineData(VirtualKey.PageDown, 1000, 850)]
	[InlineData(VirtualKey.PageUp, 1000, -850)]
	[InlineData(VirtualKey.PageDown, 712, 605.2)]
	public void APageKey_MovesThePageMostOfAView(VirtualKey key, double viewport, double pixels) =>
		Assert.Equal(pixels, CollectionsPage.PageKeyPixels(key, viewport)!.Value, 3);

	[Theory]
	[InlineData(VirtualKey.Down)]   // the rows' own: the next row
	[InlineData(VirtualKey.Home)]   // a row's first tile
	[InlineData(VirtualKey.End)]    // its last
	[InlineData(VirtualKey.GamepadRightShoulder)] // a row's page sideways
	[InlineData(VirtualKey.GamepadRightTrigger)]  // something on the page already acts on the triggers (the scroll gate)
	[InlineData(VirtualKey.GamepadLeftTrigger)]
	[InlineData(VirtualKey.Space)]
	public void AnyOtherKey_IsLeftToTheRowsAndTheTiles(VirtualKey key) => Assert.Null(CollectionsPage.PageKeyPixels(key, 1000));
}
