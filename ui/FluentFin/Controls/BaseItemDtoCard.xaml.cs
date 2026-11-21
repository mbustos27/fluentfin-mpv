using CommunityToolkit.WinUI;
using FluentFin.Core;
using Microsoft.UI.Xaml.Controls.Primitives;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;
using FluentFin.Services;
using Microsoft.UI.Xaml.Hosting;
using Windows.System;


namespace FluentFin.Controls;

public sealed partial class BaseItemDtoCard : UserControl
{
	[GeneratedDependencyProperty]
	public partial ImageSource? ImageSource { get; set; }

	[GeneratedDependencyProperty]
	public partial BaseItemViewModel? Model { get; set; }

	[GeneratedDependencyProperty]
	public partial WriteableBitmap? BlurHashImageSource { get; set; }

	[GeneratedDependencyProperty(DefaultValue = "\uE8B9")]
	public partial string Glyph { get; set; }

	[GeneratedDependencyProperty]
	public partial IJellyfinClient? JellyfinClient { get; set; }

	// fluentfin-mpv: the buttons show while the pointer is over the image or the keyboard focus is in the card (ux-review A1)
	private readonly CardActions _actions = new();

	public BaseItemDtoCard()
	{
		InitializeComponent();
		UndoToast.Listen(); // 1.2.0: the Undo after a whole series or season is marked
		// 1.2.0: the card shows its item's watched and favourite state as the server has it, whichever surface changed it
		Loaded += (_, _) =>
		{
			ItemUserData.Changed += OnUserDataChanged;
			CardFocus.Changed += OnCardFocusChanged;
			ShowActions(_actions.KeyboardElsewhere(CardFocus.IsElsewhere(this)));
		};
		Unloaded += (_, _) =>
		{
			ItemUserData.Changed -= OnUserDataChanged;
			CardFocus.Changed -= OnCardFocusChanged;
			CardFocus.Release(this);
		};

		// fluentfin-mpv: keyboard access (ux-review A1); only keys nothing inside the card has handled
		AddHandler(KeyDownEvent, new KeyEventHandler(Card_KeyDown), false);
		GotFocus += Card_GotFocus;
		LostFocus += Card_LostFocus;
		ContextRequested += Card_ContextRequested;
	}

	/// <summary>fluentfin-mpv: what a click on the image does: the item's page.</summary>
	internal void Open()
	{
		if (Model is null)
		{
			return;
		}

		App.Commands.DisplayDto(Model.Dto);
	}

	// 1.5 search: a person's card (search's People, "With <name>")
	private bool IsPerson => Model?.Dto?.Type == Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Person;

	/// <summary>fluentfin-mpv: the action buttons Narrator lists under the card while they show.</summary>
	internal IEnumerable<UIElement> VisibleActions()
	{
		if (!_actions.Shown)
		{
			yield break;
		}

		// 1.5 search: a person has no Play, Watched or Favourite on its card
		if (!IsPerson)
		{
			yield return PlayButton;
			yield return MarkWatchedButton;
			yield return AddToFavoriteButton;
		}

		// Seaglass 1.1.3: not when the menu is empty (the button is hidden then: CardMenuVisibility)
		if (MoreButton.Visibility == Visibility.Visible)
		{
			yield return MoreButton;
		}
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new BaseItemDtoCardAutomationPeer(this);

	/// <summary>
	/// 1.2.0: a click anywhere on the card opens its item (the artwork and, now, the title under it), except on one of
	/// its buttons, which do their own thing.
	/// </summary>
	private void Card_Tapped(object sender, TappedRoutedEventArgs e)
	{
		if (Model is null || OnAButton(e.OriginalSource as DependencyObject))
		{
			return;
		}

		App.Commands.DisplayDto(Model.Dto);
		e.Handled = true;
	}

	private bool OnAButton(DependencyObject? source)
	{
		for (var node = source; node is not null && !ReferenceEquals(node, this); node = VisualTreeHelper.GetParent(node))
		{
			if (node is ButtonBase)
			{
				return true;
			}
		}

		return false;
	}

	private void Card_PointerEntered(object sender, PointerRoutedEventArgs e) => ShowActions(_actions.PointerEntered());

	private void Card_PointerExited(object sender, PointerRoutedEventArgs e) => ShowActions(_actions.PointerExited());

	/// <summary>
	/// 1.2.0: the item's user data changed (a toggle here or anywhere else, or a request that failed): the card takes
	/// the server's state and re-reads every binding, so the toggles, the watched badge, the progress bar, the names and the
	/// menus (built from the item) say what the server has.
	/// </summary>
	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (Model is not { Dto.Id: { } id } model || id != e.ItemId)
		{
			return;
		}

		model.Dto.UserData = e.UserData;
		model.UserData = UserItemDataViewModel.FromDto(e.UserData);
		Bindings.Update();
	}

	private void Card_GotFocus(object sender, RoutedEventArgs e)
	{
		// the ring is for the keyboard (and a gamepad), never for a click
		FocusRing.Visibility = ReferenceEquals(e.OriginalSource, this) && ShowsFocusRing(FocusState)
			? Visibility.Visible
			: Visibility.Collapsed;
		var byPointer = e.OriginalSource is Control { FocusState: FocusState.Pointer };
		ShowActions(_actions.GotFocus(byPointer));
		// 1.4.0: the keyboard's card: a pointer resting on another one only brightens it (CardFocus)
		if (byPointer)
		{
			CardFocus.Release(this);
		}
		else
		{
			CardFocus.Take(this);
		}
	}

	private void OnCardFocusChanged(object? sender, EventArgs e) => ShowActions(_actions.KeyboardElsewhere(CardFocus.IsElsewhere(this)));

	/// <summary>Whether focus given this way shows the card's ring (and its buttons and lift: <see cref="CardActions"/>): not for a click.</summary>
	internal static bool ShowsFocusRing(FocusState state) => state is FocusState.Keyboard or FocusState.Programmatic;

	private void Card_LostFocus(object sender, RoutedEventArgs e)
	{
		if (FocusState == FocusState.Unfocused)
		{
			FocusRing.Visibility = Visibility.Collapsed;
		}
		ShowActions(_actions.LostFocus(FocusStaysWithCard));
		if (!_actions.HasKeyboard)
		{
			CardFocus.Release(this);
		}
	}

	/// <summary>
	/// Seaglass: the lift. In GlassMode Full a spring takes the ART to 1.05 of its size about its centre and deepens its shadow;
	/// otherwise (Reduced, Solid, animations off) it keeps its size and only the shadow changes, so nothing moves. 1.4.0 (the
	/// library UX review, DI-m6): the art alone grows, inside the room the card's 0.9 rest scale reserves, so the title and the
	/// line under it never move or grow (the whole card grew 188 x 334 to 197 x 351 and dropped its caption 8 px).
	/// </summary>
	private void Lift(bool lifted)
	{
		var scale = CardLift.ArtScale(lifted, SafeMotion.Allowed);
		foreach (var element in new UIElement[] { ImageCanvas, ShadowHost })
		{
			var visual = ElementCompositionPreview.GetElementVisual(element);
			if (element is FrameworkElement fe)
			{
				visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight / 2, 0);
			}

			if (SafeMotion.Allowed)
			{
				var spring = visual.Compositor.CreateSpringVector3Animation();
				spring.DampingRatio = 0.7f;
				spring.Period = TimeSpan.FromMilliseconds(50);
				spring.FinalValue = new Vector3(scale, scale, 1);
				visual.StartAnimation("Scale", spring);
			}
			else
			{
				visual.StopAnimation("Scale");
				visual.Scale = new Vector3(scale, scale, 1);
			}
		}

		CardShadow.BlurRadius = lifted ? 24 : 8;
		CardShadow.Opacity = lifted ? 0.5 : 0.25;
		CardShadow.Offset = lifted ? "0,10" : "0,3";
	}

	private void Card_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		switch (e.Key)
		{
			case VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA when ReferenceEquals(e.OriginalSource, this):
				if (!e.KeyStatus.WasKeyDown)
				{
					Open();
				}
				e.Handled = true;
				break;
			case VirtualKey.Left when !InShelfRow:
				e.Handled = MoveFocusInRow(FocusNavigationDirection.Left);
				break;
			case VirtualKey.Right when !InShelfRow:
				e.Handled = MoveFocusInRow(FocusNavigationDirection.Right);
				break;
			case VirtualKey.Up when !InShelfRow:
				e.Handled = MoveFocusInRow(FocusNavigationDirection.Up);
				break;
			case VirtualKey.Down when !InShelfRow:
				e.Handled = MoveFocusInRow(FocusNavigationDirection.Down);
				break;
		}
	}

	/// <summary>Seaglass scrolling: in a <see cref="ShelfRow"/> the row owns the arrow keys (Left/Right/Home/End, Up/Down between rows).</summary>
	private bool InShelfRow => FindAncestor<ShelfRow>(this) is not null;

	private void Card_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
	{
		// the menu key or Shift+F10 on the focused card: the menu a right click on its image opens
		if (!ReferenceEquals(args.OriginalSource, this) || RootGrid.ContextFlyout is not { } flyout)
		{
			return;
		}

		flyout.ShowAt(this);
		args.Handled = true;
	}

	/// <summary>
	/// Arrow keys move to the next card in the row (or grid) the card is in, as in a list; at its end the key is left to the
	/// page (its scroll viewer). The row's scroll viewer brings the card that gets focus into view.
	/// </summary>
	private bool MoveFocusInRow(FocusNavigationDirection direction)
	{
		if (FindAncestor<ItemsRepeater>(this) is not { } row)
		{
			return false;
		}

		return FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = row });
	}

	private bool FocusStaysWithCard()
	{
		if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
		{
			return false;
		}

		static IEnumerable<object> AndAncestors(DependencyObject element)
		{
			for (DependencyObject? e = element; e is not null; e = VisualTreeHelper.GetParent(e))
			{
				yield return e;
			}
		}

		return CardActions.FocusStaysWithCard(AndAncestors(focused), this, XamlRoot.Content,
			VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Select(p => (object?)p.Child));
	}

	/// <param name="changed">Whether the buttons are to show or hide now (<see cref="CardActions"/>).</param>
	private void ShowActions(bool changed)
	{
		if (!changed)
		{
			return;
		}

		// 1.5 search (review m9): a person (search's People) has nothing to play, watch or mark: no Play, Watched or
		// Favourite on its card, and no strip at all when its menu is empty too
		var person = IsPerson;
		PlayButton.Visibility = MarkWatchedButton.Visibility = AddToFavoriteButton.Visibility = person ? Visibility.Collapsed : Visibility.Visible;
		var show = _actions.Shown && (!person || MoreButton.Visibility == Visibility.Visible);
		ActionStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		HoverBrighten.Visibility = _actions.Brightened ? Visibility.Visible : Visibility.Collapsed;
		Lift(show);

		if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaiseAutomationEvent(AutomationEvents.StructureChanged);
		}
	}

	private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
	{
		for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
		{
			if (parent is T found)
			{
				return found;
			}
		}

		return null;
	}

	// 1.2.0: a toggle sets the state it now shows, which its name says (ItemLabels.WatchedAction), through the one
	// path every surface uses (ItemUserData: the server's answer comes back to this card and every other one of the item; a
	// failure springs the toggle back). The model's copy, which only Home refreshed, made a second click send the first again.
	private async void MarkWatchedButton_Clicked(object sender, RoutedEventArgs e)
	{
		if (Model is null)
		{
			return;
		}

		await App.Commands.SetWatched(Model.Dto, MarkWatchedButton.IsChecked == true);
	}

	private async void AddToFavoriteButton_Clicked(object sender, RoutedEventArgs e)
	{
		if (Model is null)
		{
			return;
		}

		await App.Commands.SetFavorite(Model.Dto, AddToFavoriteButton.IsChecked == true);
	}
}

/// <summary>
/// Nixie 1.4.0 (the library UX review, DI-m6, C-m1): how much a card's art grows when it lifts. The card rests at 0.9 of its
/// slot (the room the growth takes); only the art grows, 1.05 about its centre, so the words never move. Without motion
/// (Reduced, Solid, animations off) nothing grows.
/// </summary>
internal static class CardLift
{
	public const float RestScale = 0.9f;
	public const float LiftScale = 1.05f;

	/// <summary>The art's own scale (inside the card's rest scale): <see cref="LiftScale"/> while lifted and motion is allowed, else 1.</summary>
	public static float ArtScale(bool lifted, bool motionAllowed) => lifted && motionAllowed ? LiftScale : 1f;

	/// <summary>The art's growth on each side, as a share of its slot: half of what the lifted art adds, inside the rest scale's room.</summary>
	public static double GrowthPerSide(bool motionAllowed) => RestScale * (ArtScale(true, motionAllowed) - 1) / 2;

	/// <summary>The room the rest scale leaves on each side, as a share of the slot.</summary>
	public static double RoomPerSide => (1 - RestScale) / 2;
}

/// <summary>
/// fluentfin-mpv: when a card shows its buttons, apart from WinUI so it can be tested: while the pointer is over its image or
/// the keyboard focus is in the card (ux-review A1). Each event returns whether <see cref="Shown"/> or <see cref="Brightened"/>
/// changed. Nixie 1.4.0 (X9): one highlight per meaning. The keyboard's card shows its actions (and its ring); a pointer resting
/// on another card while the keyboard is on a card only brightens it (<see cref="Brightened"/>): no actions, no lift.
/// </summary>
internal sealed class CardActions
{
	private bool _pointerOver;
	private bool _focusWithin;
	private bool _keyboardElsewhere;

	/// <summary>The actions and the art's lift.</summary>
	public bool Shown { get; private set; }

	/// <summary>The hover's brighten alone: the pointer is over the card while the keyboard is on another one.</summary>
	public bool Brightened { get; private set; }

	/// <summary>Whether the keyboard's focus is in this card (on it or one of its buttons).</summary>
	public bool HasKeyboard => _focusWithin;

	/// <param name="elsewhere">Whether another card has the keyboard's focus (<see cref="CardFocus"/>).</param>
	public bool KeyboardElsewhere(bool elsewhere)
	{
		_keyboardElsewhere = elsewhere;
		return Update();
	}

	public bool PointerEntered()
	{
		_pointerOver = true;
		return Update();
	}

	public bool PointerExited()
	{
		_pointerOver = false;
		return Update();
	}

	/// <param name="byPointer">The focus a click gives: it leaves the buttons to the pointer, as before.</param>
	public bool GotFocus(bool byPointer)
	{
		_focusWithin = !byPointer;
		return Update();
	}

	/// <param name="focusStaysWithCard">Asked only while keyboard focus was in the card (<see cref="FocusStaysWithCard"/>).</param>
	public bool LostFocus(Func<bool> focusStaysWithCard)
	{
		_focusWithin = _focusWithin && focusStaysWithCard();
		return Update();
	}

	/// <summary>
	/// Whether the focus that left a card element still belongs to the card: on the card or one of its buttons, or in a menu or
	/// dialog (most likely opened from the card's More button or menu; focus comes back to the card when it closes).
	/// </summary>
	/// <param name="focusedAndAncestors">The focused element, then its visual parents. The walk goes on past the window's content (in
	/// WinUI 3 up to the window's own scroll viewer), so reaching the content is what says "in the page, not in the card".</param>
	/// <param name="openPopupChildren">What the open popups show. A tooltip does not count: one opens on any button the
	/// keyboard rests on for a moment.</param>
	public static bool FocusStaysWithCard(IEnumerable<object> focusedAndAncestors, object card, object? windowContent, IEnumerable<object?> openPopupChildren)
	{
		foreach (var element in focusedAndAncestors)
		{
			if (ReferenceEquals(element, card))
			{
				return true;
			}

			if (ReferenceEquals(element, windowContent))
			{
				return false;
			}
		}

		return openPopupChildren.Any(child => child is not ToolTip);
	}

	private bool Update()
	{
		var show = _focusWithin || (_pointerOver && !_keyboardElsewhere);
		var brighten = _pointerOver && !show;
		if (show == Shown && brighten == Brightened)
		{
			return false;
		}

		Shown = show;
		Brightened = brighten;
		return true;
	}
}

/// <summary>
/// fluentfin-mpv: Narrator reads a card as one button named after its item ("Inception, 2010, watched", <see cref="ItemLabels.CardName"/>)
/// that opens the item's page (Invoke), with the card's buttons under it while they show. An AutomationProperties.Name set on
/// the card wins over the generated name.
/// </summary>
internal sealed partial class BaseItemDtoCardAutomationPeer(BaseItemDtoCard owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
	// Seaglass scrolling: a card is an item of its row's list ("3 of 12"); it still opens its page (Invoke)
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

	protected override string GetClassNameCore() => nameof(BaseItemDtoCard);

	protected override string GetNameCore()
	{
		var name = AutomationProperties.GetName(owner);
		return string.IsNullOrEmpty(name) ? ItemLabels.CardName(owner.Model) : name;
	}

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

	protected override IList<AutomationPeer> GetChildrenCore() =>
		[.. owner.VisibleActions().Select(FrameworkElementAutomationPeer.CreatePeerForElement).Where(x => x is not null)];

	public void Invoke() => owner.Open();
}
