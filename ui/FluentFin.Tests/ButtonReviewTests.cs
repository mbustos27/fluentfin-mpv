using FluentFin.Controls;
using FluentFin.Converters;

namespace FluentFin.Tests;

/// <summary>
/// 1.2.0 (Nixie button review): the Undo notice after a whole series or season is marked (I4, I24) and the favourite
/// toggle's heart (I5, I25).
/// </summary>
public sealed class ButtonReviewTests
{
	[Theory]
	[InlineData(2, false, false, true)]
	[InlineData(11, false, false, false)]   // its time is up
	[InlineData(11, true, false, true)]     // the pointer is on it
	[InlineData(11, false, true, true)]     // the keyboard is on it
	public void TheUndoNotice_StaysForItsTime_AndWhileThePointerOrTheKeyboardIsOnIt(int seconds, bool pointerOver, bool focusWithin, bool open)
	{
		Assert.Equal(open, UndoToast.StaysOpen(TimeSpan.FromSeconds(seconds), pointerOver, focusWithin));
		Assert.InRange(UndoToast.ShowFor.TotalSeconds, 5, 20);
	}

	[Theory]
	[InlineData(null, "\uEB51")]
	[InlineData(false, "\uEB51")]
	[InlineData(true, "\uEB52")]
	public void TheFavoriteHeart_IsAnOutlineWhenOff_FilledWhenOn(bool? on, string glyph)
	{
		Assert.Equal(glyph, ItemRowConverters.FavoriteGlyph(on));
	}
}
