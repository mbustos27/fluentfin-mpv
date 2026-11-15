using CommunityToolkit.WinUI;
using FluentFin.Core;
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
	private bool _pointerOver;
	private bool _focusWithin;
	private bool _actionsShown;

	public BaseItemDtoCard()
	{
		InitializeComponent();

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
		if (!_actionsShown)
		{
			yield break;
		}

		yield return PlayButton;
		yield return MarkWatchedButton;
		yield return AddToFavoriteButton;
		yield return MoreButton;
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new BaseItemDtoCardAutomationPeer(this);

	private void ImageCanvas_Tapped(object sender, TappedRoutedEventArgs e)
	{
		if (e.OriginalSource is not (Grid or Image))
		{
			return;
		}

		if (Model is null)
		{
			return;
		}

		App.Commands.DisplayDto(Model.Dto);

		e.Handled = true;
	}

	private void ImageCanvas_PointerEntered(object sender, PointerRoutedEventArgs e)
	{
		_pointerOver = true;
		UpdateActions();
	}

	private void ImageCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
	{
		_pointerOver = false;
		UpdateActions();
	}

	private void Card_GotFocus(object sender, RoutedEventArgs e)
	{
		// keyboard (or programmatic) focus shows the buttons; the focus a click gives leaves them to the pointer, as before
		_focusWithin = e.OriginalSource is not Control { FocusState: FocusState.Pointer };
		UpdateActions();
	}

	private void Card_LostFocus(object sender, RoutedEventArgs e)
	{
		// focus moving between the card and its own buttons, or into a menu or dialog opened from them, keeps them shown
		_focusWithin = _focusWithin && FocusStaysWithCard();
		UpdateActions();
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
			case VirtualKey.Left:
				e.Handled = MoveFocusInRow(FocusNavigationDirection.Left);
				break;
			case VirtualKey.Right:
				e.Handled = MoveFocusInRow(FocusNavigationDirection.Right);
				break;
			case VirtualKey.Up:
				e.Handled = MoveFocusInRow(FocusNavigationDirection.Up);
				break;
			case VirtualKey.Down:
				e.Handled = MoveFocusInRow(FocusNavigationDirection.Down);
				break;
		}
	}

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

		DependencyObject? last = null;
		for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
		{
			if (ReferenceEquals(element, this))
			{
				return true;
			}

			last = element;
		}

		// not under the window's content: a flyout or dialog, most likely opened from this card's More button or menu
		// (focus comes back to the card when it closes)
		return !ReferenceEquals(last, XamlRoot.Content) && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0;
	}

	private void UpdateActions()
	{
		var show = _pointerOver || _focusWithin;
		if (show == _actionsShown)
		{
			return;
		}

		_actionsShown = show;
		PlayButton.Visibility = ActionButtons.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		(show ? Select : UnSelect).Start();
		(show ? BlurAnimation : UnBlurAnimation).Start();

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

	private async void MarkWatchedButton_Clicked(object sender, RoutedEventArgs e)
	{
		if (Model is null || JellyfinClient is null)
		{
			return;
		}

		await JellyfinClient.SetPlayed(Model.Dto, !(Model.UserData?.Played ?? false));
	}

	private async void AddToFavoriteButton_Clicked(object sender, RoutedEventArgs e)
	{
		if (Model is null || JellyfinClient is null)
		{
			return;
		}

		await JellyfinClient.SetIsFavorite(Model.Dto, !(Model.UserData?.IsFavorite ?? false));
	}
}

/// <summary>
/// fluentfin-mpv: Narrator reads a card as one button named after its item ("Inception, 2010, watched", <see cref="ItemLabels.CardName"/>)
/// that opens the item's page (Invoke), with the card's buttons under it while they show. An AutomationProperties.Name set on
/// the card wins over the generated name.
/// </summary>
internal sealed partial class BaseItemDtoCardAutomationPeer(BaseItemDtoCard owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

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
