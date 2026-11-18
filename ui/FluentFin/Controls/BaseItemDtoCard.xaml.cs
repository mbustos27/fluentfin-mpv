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
		Loaded += (_, _) => ItemUserData.Changed += OnUserDataChanged;
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;

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

	/// <summary>fluentfin-mpv: the action buttons Narrator lists under the card while they show.</summary>
	internal IEnumerable<UIElement> VisibleActions()
	{
		if (!_actions.Shown)
		{
			yield break;
		}

		yield return PlayButton;
		yield return MarkWatchedButton;
		yield return AddToFavoriteButton;
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
		ShowActions(_actions.GotFocus(byPointer: e.OriginalSource is Control { FocusState: FocusState.Pointer }));
	}

	/// <summary>Whether focus given this way shows the card's ring (and its buttons and lift: <see cref="CardActions"/>): not for a click.</summary>
	internal static bool ShowsFocusRing(FocusState state) => state is FocusState.Keyboard or FocusState.Programmatic;

	private void Card_LostFocus(object sender, RoutedEventArgs e)
	{
		if (FocusState == FocusState.Unfocused)
		{
			FocusRing.Visibility = Visibility.Collapsed;
		}
		ShowActions(_actions.LostFocus(FocusStaysWithCard));
	}

	/// <summary>
	/// Seaglass: the lift. In GlassMode Full a spring takes the card to 1.05 of its size and deepens its shadow; otherwise
	/// (Reduced, Solid, animations off) it keeps its size and only the shadow changes, so nothing moves.
	/// </summary>
	private void Lift(bool lifted)
	{
		var visual = ElementCompositionPreview.GetElementVisual(RootGrid);
		var scale = lifted && SafeMotion.Allowed ? RestScale * LiftScale : RestScale;
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

		CardShadow.BlurRadius = lifted ? 24 : 8;
		CardShadow.Opacity = lifted ? 0.5 : 0.25;
		CardShadow.Offset = lifted ? "0,10" : "0,3";
	}

	/// <summary>A card sits at 0.9 of its slot (room for the lift and the shadow); the lift is 1.05 of that.</summary>
	private const float RestScale = 0.9f;
	private const float LiftScale = 1.05f;

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

		var show = _actions.Shown;
		PlayButton.Visibility = ActionButtons.Visibility = HoverScrim.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
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
/// fluentfin-mpv: when a card shows its buttons, apart from WinUI so it can be tested: while the pointer is over its image or
/// the keyboard focus is in the card (ux-review A1). Each event returns whether <see cref="Shown"/> changed.
/// </summary>
internal sealed class CardActions
{
	private bool _pointerOver;
	private bool _focusWithin;

	public bool Shown { get; private set; }

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
		var show = _pointerOver || _focusWithin;
		if (show == Shown)
		{
			return false;
		}

		Shown = show;
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
