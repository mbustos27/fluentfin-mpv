using FluentFin.Themes.Editorial.Controls;
using Windows.System;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.7.4 (rows-gate: "Editorial's library genre strip: End doesn't reach the last genre"): End and Home take a strip of
/// tabs to its last and first tab that can take the keyboard, and the strip to its very end and start, so the last tab clears
/// what lies over the strip's end (the Library's fade and its "More genres" chevron).
/// </summary>
public sealed class EditorialTabKeysTests
{
	private static readonly bool[] AllOn = [true, true, true, true, true];

	[Theory]
	[InlineData(VirtualKey.End, 0, 4)]
	[InlineData(VirtualKey.End, 4, 4)]
	[InlineData(VirtualKey.Home, 3, 0)]
	[InlineData(VirtualKey.Right, 1, 2)]
	[InlineData(VirtualKey.Right, 4, 4)] // no wrap
	[InlineData(VirtualKey.Left, 0, 0)]
	public void TheKeys_MoveAlongTheStrip(VirtualKey key, int at, int expected) =>
		Assert.Equal(expected, EditorialTabList.KeyTarget(key, at, AllOn, horizontal: true));

	[Fact]
	public void EndAndHome_SkipTabsThatCannotTakeTheKeyboard()
	{
		bool[] lastOff = [false, true, true, true, false];
		Assert.Equal(3, EditorialTabList.KeyTarget(VirtualKey.End, 1, lastOff, horizontal: true));
		Assert.Equal(1, EditorialTabList.KeyTarget(VirtualKey.Home, 3, lastOff, horizontal: true));
		Assert.Equal(3, EditorialTabList.KeyTarget(VirtualKey.Right, 3, lastOff, horizontal: true));
		Assert.Null(EditorialTabList.KeyTarget(VirtualKey.Down, 1, lastOff, horizontal: true));
	}

	[Theory]
	[InlineData(4, 5, 640.0, 640.0)] // the last: the strip's very end, its trailing room clear of the fade and the chevron
	[InlineData(0, 5, 640.0, 0.0)]   // the first: its start
	public void TheFirstAndTheLastTab_TakeTheStripToItsEnds(int target, int count, double scrollable, double expected) =>
		Assert.Equal(expected, EditorialTabList.EdgeOffset(target, count, scrollable));

	[Fact]
	public void ATabBetween_IsOnlyBroughtIntoView() =>
		Assert.Null(EditorialTabList.EdgeOffset(2, 5, 640));
}
