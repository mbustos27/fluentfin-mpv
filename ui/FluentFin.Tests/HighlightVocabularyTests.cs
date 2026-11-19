using FluentFin.Controls;
using FluentFin.Themes.Cinematic.Controls;
using FluentFin.Themes.Editorial.Controls;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (the library UX review, X9): one highlight per meaning on every theme's card. The keyboard's card shows its ring
/// and actions; a free pointer lifts a card; a pointer resting on a card while the keyboard is on another card only brightens it
/// (two lifted cards, two zoomed stills with Play glyphs, were the fault); Editorial's open poster wears the accent rim, never
/// the focus ring; the art grows, the words never do.
/// </summary>
public sealed class HighlightVocabularyTests
{
	[Fact]
	public void A_pointer_over_a_card_while_the_keyboard_is_on_another_only_brightens_it()
	{
		var hovered = new CardActions();
		hovered.PointerEntered();
		Assert.True(hovered.Shown); // a free pointer: the actions and the lift
		Assert.False(hovered.Brightened);

		Assert.True(hovered.KeyboardElsewhere(true)); // the keyboard arrives on another card
		Assert.False(hovered.Shown);
		Assert.True(hovered.Brightened);

		Assert.True(hovered.KeyboardElsewhere(false)); // and leaves the cards
		Assert.True(hovered.Shown);
		Assert.False(hovered.Brightened);

		hovered.PointerExited();
		Assert.False(hovered.Shown);
		Assert.False(hovered.Brightened);
	}

	/// <summary>
	/// No look sticks (the standing rule: a hover look clears once the keyboard takes over, and the other way round), in both
	/// orders: mouse first then keyboard, keyboard first then mouse, on two cards and on one.
	/// </summary>
	[Fact]
	public void Mouse_first_then_keyboard_leaves_no_hover_look_behind()
	{
		var (a, b) = (new CardActions(), new CardActions());
		a.PointerEntered(); // the pointer rests on A
		Assert.True(a.Shown);

		b.GotFocus(byPointer: false); // the keyboard lands on B
		a.KeyboardElsewhere(true);
		Assert.True(b.Shown);
		Assert.False(a.Shown); // A no longer lifts or shows actions
		Assert.True(a.Brightened);

		a.PointerExited(); // the pointer leaves A: nothing of A stays
		Assert.False(a.Shown);
		Assert.False(a.Brightened);
		Assert.True(b.Shown);
	}

	[Fact]
	public void Keyboard_first_then_mouse_leaves_no_focus_look_behind()
	{
		var (a, b) = (new CardActions(), new CardActions());
		a.GotFocus(byPointer: false); // the keyboard is on A
		b.KeyboardElsewhere(true);
		b.PointerEntered(); // the pointer rests on B: the brighten alone
		Assert.True(a.Shown);
		Assert.False(b.Shown);
		Assert.True(b.Brightened);

		b.GotFocus(byPointer: true); // a click on B takes the focus from A
		Assert.True(a.LostFocus(() => false));
		Assert.False(a.Shown); // A's ring and actions go
		Assert.False(a.HasKeyboard);
		b.KeyboardElsewhere(false); // CardFocus: A released
		Assert.True(b.Shown); // B is the pointer's card now
		Assert.False(b.Brightened);

		b.PointerExited();
		Assert.False(b.Shown);
		Assert.False(b.Brightened);
	}

	[Fact]
	public void The_same_card_hovered_then_focused_then_left_shows_nothing()
	{
		var card = new CardActions();
		card.PointerEntered();
		card.GotFocus(byPointer: false);
		card.PointerExited();
		Assert.True(card.Shown); // the keyboard's
		card.LostFocus(() => false);
		Assert.False(card.Shown);
		Assert.False(card.Brightened);
	}

	[Fact]
	public void The_keyboards_card_keeps_its_actions_whatever_the_pointer_does()
	{
		var card = new CardActions();
		card.GotFocus(byPointer: false);
		card.KeyboardElsewhere(false);
		card.PointerEntered();
		Assert.True(card.Shown);
		Assert.False(card.Brightened);
		Assert.True(card.HasKeyboard);
	}

	[Fact]
	public void CardFocus_names_one_keyboard_card_and_says_when_it_changes()
	{
		var (a, b) = (new object(), new object());
		var changes = 0;
		void Count(object? s, EventArgs e) => changes++;
		CardFocus.Changed += Count;
		try
		{
			CardFocus.Take(a);
			Assert.True(CardFocus.IsElsewhere(b));
			Assert.False(CardFocus.IsElsewhere(a));
			CardFocus.Take(a); // no change
			CardFocus.Take(b);
			Assert.True(CardFocus.IsElsewhere(a));
			CardFocus.Release(a); // not the owner: nothing
			Assert.True(CardFocus.IsElsewhere(a));
			CardFocus.Release(b);
			Assert.False(CardFocus.IsElsewhere(a));
			Assert.Equal(3, changes);
		}
		finally
		{
			CardFocus.Changed -= Count;
			CardFocus.Release(a);
			CardFocus.Release(b);
		}
	}

	/// <summary>DI-m6: the art grows inside the room the card's 0.9 rest scale keeps; the words never scale.</summary>
	[Fact]
	public void The_cards_art_grows_inside_its_reserved_room()
	{
		Assert.Equal(1.05f, CardLift.ArtScale(lifted: true, motionAllowed: true));
		Assert.Equal(1f, CardLift.ArtScale(lifted: true, motionAllowed: false));
		Assert.Equal(1f, CardLift.ArtScale(lifted: false, motionAllowed: true));
		Assert.True(CardLift.GrowthPerSide(motionAllowed: true) <= CardLift.RoomPerSide);
		Assert.Equal(0, CardLift.GrowthPerSide(motionAllowed: false));
	}

	/// <summary>
	/// C-M7: hover and keyboard focus both zoomed a still and showed Play, and the keyboard's Play glyph promised what Enter does
	/// not do (Enter opens the details).
	/// </summary>
	[Theory]
	//          pointer, focused, keyboard, elsewhere, enterPlays => zoom, play, sheen, rim
	[InlineData(false, true, true, false, false, true, false, true, true)]    // the keyboard's still: zoom, rim, no Play (Enter opens)
	[InlineData(false, true, true, false, true, true, true, true, true)]      // an episode's: Enter plays, so Play shows
	[InlineData(true, false, false, false, false, true, true, true, false)]   // a free pointer: zoom and Play
	[InlineData(true, false, false, true, false, false, false, true, false)]  // the keyboard is elsewhere: the sheen alone
	[InlineData(true, true, false, false, false, true, true, true, false)]    // clicked (pointer focus): the pointer's look, no rim
	[InlineData(false, false, false, true, false, false, false, false, false)]
	public void A_still_has_one_look_per_meaning(bool pointer, bool focused, bool keyboard, bool elsewhere, bool enterPlays,
		bool zoom, bool play, bool sheen, bool rim)
	{
		var look = CinematicStill.Look(pointer, focused, keyboard, elsewhere, enterPlays);
		Assert.Equal(new StillLook(zoom, play, sheen, rim), look);
	}

	/// <summary>E-M8: an open poster drew exactly the keyboard's ring, so two posters looked focused at once.</summary>
	[Theory]
	//          pointer, keyboard, elsewhere, open => ring, open, lift, brighten
	[InlineData(false, false, false, true, false, true, false, false)]   // open, the focus moved on: the rim alone
	[InlineData(false, true, false, true, true, true, true, false)]      // open and focused: both marks, distinct
	[InlineData(false, true, false, false, true, false, true, false)]    // the keyboard's poster
	[InlineData(true, false, false, false, false, false, true, false)]   // a free pointer: the lift
	[InlineData(true, false, true, false, false, false, false, true)]    // the keyboard is elsewhere: the brighten alone
	[InlineData(true, false, false, true, false, true, false, true)]     // pointer over the open one: no lift, the brighten
	public void A_poster_has_one_look_per_meaning(bool pointer, bool keyboard, bool elsewhere, bool open,
		bool ring, bool openMark, bool lift, bool brighten)
	{
		Assert.Equal(new PosterLook(ring, openMark, lift, brighten), EditorialPoster.Look(pointer, keyboard, elsewhere, open));
	}
}
