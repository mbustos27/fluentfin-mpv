using FluentFin.Controls;
using FluentFin.Views;

namespace FluentFin.Tests;

/// <summary>
/// 174-sessions (the basics sweep of 1.7.3, BS-m4): a real pointer's click on a button inside a card, an episode row, the
/// hero or a library tile can't be made off-screen (posted clicks open nothing on a desktop nobody sees, and UI Automation
/// invokes the button itself), so what the click does is checked here, on the guards that decide it. Each walks up from
/// where the click landed to its card, row or hero (the visual tree in the app, a tree of the test's own here): a button
/// on the way does its own thing, and only a click on nothing else opens or plays.
/// </summary>
public sealed class ClickGuardTests
{
	/// <summary>A node of a visual tree: its name, its parent, and whether it is a button (a ButtonBase in the app).</summary>
	private sealed record Node(string Name, Node? Parent = null, bool Button = false)
	{
		public Node Child(string name, bool button = false) => new(name, this, button);

		public override string ToString() => Name;
	}

	private static Node? ParentOf(Node node) => node.Parent;

	private static bool IsButton(Node node) => node.Button;

	// ---- a card (BaseItemDtoCard.OnAButton): its artwork and title open the item, its buttons don't

	[Fact]
	public void ACard_OpensOnItsArtworkAndTitle_NotOnItsButtons()
	{
		var page = new Node("page");
		var row = page.Child("row", button: false);
		var card = row.Child("card");
		var art = card.Child("artwork grid").Child("image");
		var title = card.Child("title");
		var favourite = card.Child("actions").Child("favourite", button: true);
		var more = card.Child("actions").Child("more", button: true);

		Assert.False(BaseItemDtoCard.OnAButton(art, card, ParentOf, IsButton));
		Assert.False(BaseItemDtoCard.OnAButton(title, card, ParentOf, IsButton));
		Assert.False(BaseItemDtoCard.OnAButton(card, card, ParentOf, IsButton));     // on the card itself
		Assert.False(BaseItemDtoCard.OnAButton(null, card, ParentOf, IsButton));     // nothing under the pointer
		Assert.True(BaseItemDtoCard.OnAButton(favourite.Child("glyph"), card, ParentOf, IsButton)); // the button's glyph
		Assert.True(BaseItemDtoCard.OnAButton(more, card, ParentOf, IsButton));
	}

	/// <summary>The walk stops at the card: a button the card sits in (a list's item container) is not one of the card's.</summary>
	[Fact]
	public void ACard_InsideAButton_StillOpensOnItsArtwork()
	{
		var container = new Node("item container", Button: true);
		var card = container.Child("card");

		Assert.False(BaseItemDtoCard.OnAButton(card.Child("image"), card, ParentOf, IsButton));
	}

	// ---- an episode row (RowClick.For): a click plays, except on its title (the link to the page) or a button

	[Fact]
	public void AnEpisodeRow_PlaysOnItsArtwork_NotOnItsTitleOrButtons()
	{
		var row = new Node("row");
		var title = row.Child("text").Child("title");
		var still = row.Child("still").Child("image");
		var more = row.Child("more", button: true);
		var watched = row.Child("badges").Child("watched", button: true);

		Assert.Equal(RowClickAction.Play, RowClick.For(still, row, title, ParentOf, IsButton));
		Assert.Equal(RowClickAction.Play, RowClick.For(row.Child("text").Child("overview"), row, title, ParentOf, IsButton));
		Assert.Equal(RowClickAction.Play, RowClick.For(null, row, title, ParentOf, IsButton));
		Assert.Equal(RowClickAction.None, RowClick.For(title, row, title, ParentOf, IsButton));
		Assert.Equal(RowClickAction.None, RowClick.For(title.Child("run"), row, title, ParentOf, IsButton)); // a word of the title
		Assert.Equal(RowClickAction.None, RowClick.For(more.Child("glyph"), row, title, ParentOf, IsButton));
		Assert.Equal(RowClickAction.None, RowClick.For(watched, row, title, ParentOf, IsButton));
	}

	// ---- the hero (HeroClick.OnAControl): the artwork and words open the slide's item; Play, More info, the dots don't

	[Fact]
	public void TheHero_OpensOnItsArtwork_NotOnItsControls()
	{
		var hero = new Node("hero");
		var backdrop = hero.Child("backdrop").Child("image");
		var words = hero.Child("words").Child("overview");
		var play = hero.Child("buttons").Child("Play", button: true);
		var dot = hero.Child("dots").Child("dot 3", button: true);
		var arrow = hero.Child("next", button: true);

		Assert.False(HeroClick.OnAControl(backdrop, hero, ParentOf, IsButton));
		Assert.False(HeroClick.OnAControl(words, hero, ParentOf, IsButton));
		Assert.False(HeroClick.OnAControl(null, hero, ParentOf, IsButton));
		Assert.True(HeroClick.OnAControl(play.Child("label"), hero, ParentOf, IsButton));
		Assert.True(HeroClick.OnAControl(dot, hero, ParentOf, IsButton));
		Assert.True(HeroClick.OnAControl(arrow, hero, ParentOf, IsButton));
	}

	// ---- a library tile's star (LibrariesLandingPage): the tile's invoke on the star's press is the star's

	[Fact]
	public void ALibraryTile_IgnoresTheInvokeOfAPressOnItsStar_WhicheverComesFirst()
	{
		var pressed = 50_000L;

		// the press's own invoke (ItemsView invokes on the release, a moment after the press)
		Assert.True(LibrariesLandingPage.StarTookTheClick(pressed, pressed + 120));
		// the next click on the tile, after the star's: it opens
		Assert.False(LibrariesLandingPage.StarTookTheClick(pressed, pressed + LibrariesLandingPage.StarClickMs + 1));
		// no star pressed since the app started: every invoke opens
		Assert.False(LibrariesLandingPage.StarTookTheClick(0, LibrariesLandingPage.StarClickMs));

		// the press is heard even though the star's button handles it (handledEventsToo), so the order doesn't matter
		var code = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Views", "LibrariesLandingPage.xaml.cs"));
		Assert.Contains("star.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _starPressedAt = Environment.TickCount64), true);", code);
	}

	/// <summary>Each guard is the one the control calls: the app's own walk goes through it (the visual tree, its buttons).</summary>
	[Fact]
	public void TheControls_UseTheseGuards()
	{
		string Code(params string[] path) => File.ReadAllText(Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. path]));

		Assert.Contains("private bool OnAButton(DependencyObject? source) => OnAButton(source, this, VisualTreeHelper.GetParent, node => node is ButtonBase);", Code("Controls", "BaseItemDtoCard.xaml.cs"));
		Assert.Contains("For(source, row, title, VisualTreeHelper.GetParent, node => node is ButtonBase);", Code("Controls", "EpisodeRow.xaml.cs"));
		Assert.Contains("OnAControl(source, hero, VisualTreeHelper.GetParent, node => node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase);", Code("Controls", "HomeHero.xaml.cs"));
		Assert.Contains("if (Model is null || OnAButton(e.OriginalSource as DependencyObject))", Code("Controls", "BaseItemDtoCard.xaml.cs"));
		Assert.Contains("HeroClick.OnAControl(e.OriginalSource as DependencyObject, Root)", Code("Controls", "HomeHero.xaml.cs"));
		Assert.Contains("RowClick.For(e.OriginalSource as DependencyObject, Body, TitleLink) == RowClickAction.Play", Code("Controls", "EpisodeRow.xaml.cs"));
	}
}
