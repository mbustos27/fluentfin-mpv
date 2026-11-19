using CommunityToolkit.WinUI;
using FluentFin.Controls;
using FluentFin.Converters;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>Cinematic's 16:9 still (see CinematicStill.xaml).</summary>
public sealed partial class CinematicStill : UserControl
{
	[GeneratedDependencyProperty]
	public partial BaseItemViewModel? Model { get; set; }

	[GeneratedDependencyProperty]
	public partial ImageSource? ImageSource { get; set; }

	[GeneratedDependencyProperty]
	public partial WriteableBitmap? BlurHashImageSource { get; set; }

	/// <summary>How wide the picture is decoded (the drawn width at the zoom, about 1.6x the art at 100 %).</summary>
	[GeneratedDependencyProperty(DefaultValue = 640)]
	public partial int DecodeWidth { get; set; }

	[GeneratedDependencyProperty(DefaultValue = 384.0)]
	public partial double ArtWidth { get; set; }

	[GeneratedDependencyProperty(DefaultValue = 216.0)]
	public partial double ArtHeight { get; set; }

	[GeneratedDependencyProperty(DefaultValue = 24.0)]
	public partial double TitleArtSize { get; set; }

	[GeneratedDependencyProperty(DefaultValue = 56.0)]
	public partial double TitleArtLogoHeight { get; set; }

	[GeneratedDependencyProperty(DefaultValue = 16.0)]
	public partial double TitleSize { get; set; }

	[GeneratedDependencyProperty(DefaultValue = true)]
	public partial bool ShowTitleArt { get; set; }

	/// <summary>What Enter, a click and Narrator's Invoke do: open the item's page (a shelf) or play it (an episode shelf).</summary>
	[GeneratedDependencyProperty(DefaultValue = StillKind.Item)]
	public partial StillKind Kind { get; set; }

	/// <summary>The line under the title (null: the shared card's subtitle).</summary>
	[GeneratedDependencyProperty]
	public partial string? Line { get; set; }

	/// <summary>The title under the art (null: the shared card's title).</summary>
	[GeneratedDependencyProperty]
	public partial string? Title { get; set; }

	/// <summary>Shown under the line while the still has the focus (an episode's synopsis).</summary>
	[GeneratedDependencyProperty]
	public partial string? Synopsis { get; set; }

	private bool _pointerOver;
	private bool _focused;
	private bool _keyboard;

	public CinematicStill()
	{
		CinematicResources.Ensure();
		InitializeComponent();
		UndoToast.Listen();
		Loaded += (_, _) =>
		{
			ItemUserData.Changed += OnUserDataChanged;
			CardFocus.Changed += OnCardFocusChanged;
			_keyboardElsewhere = CardFocus.IsElsewhere(this);
		};
		Unloaded += (_, _) =>
		{
			ItemUserData.Changed -= OnUserDataChanged;
			CardFocus.Changed -= OnCardFocusChanged;
			CardFocus.Release(this);
		};
		AddHandler(KeyDownEvent, new KeyEventHandler(Still_KeyDown), false);
		GotFocus += Still_GotFocus;
		LostFocus += Still_LostFocus;
		PointerEntered += (_, _) => { _pointerOver = true; Show(); };
		PointerExited += (_, _) => { _pointerOver = false; Show(); };
		PointerCanceled += (_, _) => { _pointerOver = false; Show(); };
		Tapped += Still_Tapped;
		// the menu (a right click, the menu key, Shift+F10), made as it opens so it says what is true now
		_menu.Opening += (_, _) => FillMenu();
		ContextFlyout = _menu;
		RegisterPropertyChangedCallback(ModelProperty, (_, _) => Apply());
		RegisterPropertyChangedCallback(LineProperty, (_, _) => Apply());
		RegisterPropertyChangedCallback(TitleProperty, (_, _) => Apply());
		RegisterPropertyChangedCallback(SynopsisProperty, (_, _) => Apply());
		RegisterPropertyChangedCallback(KindProperty, (_, _) => Apply());
		RegisterPropertyChangedCallback(DecodeWidthProperty, (_, _) => Apply());
		ArtZoom.SizeChanged += (_, _) => { if (CurrentLook.Zoom) { FocusZoom.Apply(ArtZoom, true); } };
		// in a list's template the item is the DataContext (a {Binding} to the typed Model never arrived)
		DataContextChanged += (_, e) => TakeDataContext(e.NewValue);
		Loaded += (_, _) => TakeDataContext(DataContext);
	}


	private void TakeDataContext(object? data)
	{
		if (UseDataContext && data is BaseItemViewModel item && !ReferenceEquals(Model, item))
		{
			Model = item;
		}
	}

	/// <summary>Takes <see cref="Model"/> from the DataContext (the theme's templates for shelves and grids).</summary>
	[GeneratedDependencyProperty(DefaultValue = false)]
	public partial bool UseDataContext { get; set; }

	public BaseItemDto? Dto => Model?.Dto;

	public double RimWidth(double size, double grow) => size + grow;

	private bool _keyboardElsewhere;

	private StillLook CurrentLook => Look(_pointerOver, _focused, _keyboard, _keyboardElsewhere, enterPlays: Kind == StillKind.Episode);

	/// <summary>
	/// Nixie 1.4.0 (the library UX review, C-M7): one highlight per meaning. The keyboard's still zooms (the art only) and gets
	/// the rim; it shows Play only where Enter plays (an episode). A free pointer zooms the still and shows Play (its one
	/// click); a pointer resting on a still while the keyboard is on another card only lights the sheen: no zoom, no Play.
	/// </summary>
	internal static StillLook Look(bool pointerOver, bool focused, bool keyboard, bool keyboardElsewhere, bool enterPlays)
	{
		var byKeyboard = focused && keyboard;
		var freePointer = pointerOver && !keyboardElsewhere;
		return new StillLook(
			Zoom: byKeyboard || freePointer,
			Play: freePointer || (byKeyboard && enterPlays),
			Sheen: byKeyboard || pointerOver,
			Rim: byKeyboard);
	}

	private void OnCardFocusChanged(object? sender, EventArgs e)
	{
		var elsewhere = CardFocus.IsElsewhere(this);
		if (elsewhere != _keyboardElsewhere)
		{
			_keyboardElsewhere = elsewhere;
			Show();
		}
	}

	/// <summary>The item's page (a shelf's still) or its playback (an episode's).</summary>
	internal void Invoke()
	{
		if (Model?.Dto is not { } dto)
		{
			return;
		}

		if (Kind == StillKind.Episode)
		{
			_ = App.Commands.PlayDto(dto);
		}
		else
		{
			App.Commands.DisplayDto(dto);
		}
	}

	internal UIElement? VisiblePlay => PlayButton.Visibility == Visibility.Visible ? PlayButton : null;

	/// <summary>Whether the still is an item of a list (a shelf's or a grid's repeater).</summary>
	internal bool InList
	{
		get
		{
			for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
			{
				if (parent is ItemsRepeater)
				{
					return true;
				}
			}

			return false;
		}
	}

	private string? _picture;

	private void Apply()
	{
		var dto = Model?.Dto;
		// the still's DataContext is its item, whatever set it: the theme switch keeps the place by the item an element's
		// DataContext names (Helpers\ThemeAnchors), and a list here does not give its elements their item themselves
		if (Model is { } model && !ReferenceEquals(DataContext, model))
		{
			DataContext = model;
		}

		// the picture: only when the item (or its artwork) changed, not for a change of its watched state
		var picture = CinematicArtwork.StillPath(dto, DecodeWidth, episodeOwnFirst: Kind == StillKind.Episode);
		if (picture != _picture)
		{
			_picture = picture;
			ImageSource = Kind == StillKind.Episode ? CinematicArt.EpisodeStill(dto, DecodeWidth) : CinematicArt.Still(dto, DecodeWidth);
		}

		if (!ReferenceEquals(TitleArt.Dto, dto))
		{
			TitleArt.Dto = dto;
		}

		TitleText.Text = Title ?? (Kind == StillKind.Episode ? CinematicLabels.EpisodeTitle(dto) : dto.GetCardTitle());
		LineText.Text = Line ?? (Kind == StillKind.Episode ? CinematicLabels.EpisodeMeta(dto) : dto.GetCardSubtitle());
		LineText.Visibility = string.IsNullOrEmpty(LineText.Text) ? Visibility.Collapsed : Visibility.Visible;
		SynopsisText.Text = Synopsis ?? (Kind == StillKind.Episode ? dto?.Overview?.Trim() : null) ?? "";

		var progress = CinematicLabels.Progress(dto);
		var played = dto?.UserData?.Played == true;
		ProgressTrack.Visibility = progress > 0 && !played ? Visibility.Visible : Visibility.Collapsed;
		UpdateProgress(progress);

		var badge = Kind == StillKind.Episode ? CinematicLabels.EpisodeBadge(dto) : "";
		BadgeText.Text = badge;
		BadgePlate.Visibility = badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		// 1.4.0 (X10, C-m6): watched a dim check when nothing is left; "N to watch" a count chip (a series, a season, a collection)
		WatchedMark.Visibility = (Kind == StillKind.Episode ? played : ItemBadges.Watched(dto)) ? Visibility.Visible : Visibility.Collapsed;
		var toWatch = Kind == StillKind.Episode ? "" : ItemBadges.ToWatchText(dto);
		CountText.Text = toWatch;
		CountChip.Visibility = toWatch.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		var play = Kind == StillKind.Episode ? ItemLabels.EpisodeAction("Play", dto) : ItemLabels.PlayAction(dto);
		AutomationProperties.SetName(PlayButton, play);
		ToolTipService.SetToolTip(PlayButton, play);
		Show();
	}

	private void UpdateProgress(double progress)
	{
		void Size() => ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * progress / 100);
		Size();
		ProgressTrack.SizeChanged -= OnTrackSized;
		ProgressTrack.SizeChanged += OnTrackSized;
	}

	private void OnTrackSized(object sender, SizeChangedEventArgs e) =>
		ProgressFill.Width = Math.Max(0, e.NewSize.Width * CinematicLabels.Progress(Model?.Dto) / 100);

	/// <summary>The zoom (the art only), sheen, Play and (keyboard only) the rim: as the pointer and the focus say (<see cref="Look"/>).</summary>
	private void Show()
	{
		var look = CurrentLook;
		FocusZoom.Apply(ArtZoom, look.Zoom);
		FocusZoom.Fade(Sheen, look.Sheen ? 1f : 0f);
		if (look.Sheen)
		{
			FocusZoom.Sweep(Sheen, ArtWidth);
		}

		PlayButton.Visibility = look.Play ? Visibility.Visible : Visibility.Collapsed;
		RimInner.Visibility = RimOuter.Visibility = look.Rim ? Visibility.Visible : Visibility.Collapsed;

		SynopsisText.Visibility = _focused && !string.IsNullOrEmpty(SynopsisText.Text) ? Visibility.Visible : Visibility.Collapsed;
		// the zoomed still is drawn over its neighbours: the element its list holds (this, or the container around it)
		Canvas.SetZIndex(ListChild(), look.Zoom ? 1 : 0);
		if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaiseAutomationEvent(AutomationEvents.StructureChanged);
		}
	}

	private UIElement ListChild()
	{
		UIElement node = this;
		for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
		{
			if (parent is ItemsRepeater or StackPanel)
			{
				return node;
			}

			if (parent is UIElement element)
			{
				node = element;
			}
		}

		return this;
	}

	private void Still_GotFocus(object sender, RoutedEventArgs e)
	{
		if (!ReferenceEquals(e.OriginalSource, this))
		{
			return;
		}

		_focused = true;
		_keyboard = BaseItemDtoCard.ShowsFocusRing(FocusState);
		if (_keyboard)
		{
			CardFocus.Take(this);
		}
		else
		{
			CardFocus.Release(this);
		}

		Show();
	}

	private void Still_LostFocus(object sender, RoutedEventArgs e)
	{
		if (FocusState != FocusState.Unfocused)
		{
			return;
		}

		_focused = false;
		_keyboard = false;
		CardFocus.Release(this);
		Show();
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (Model is not { Dto.Id: { } id } model || id != e.ItemId)
		{
			return;
		}

		model.Dto.UserData = e.UserData;
		model.UserData = UserItemDataViewModel.FromDto(e.UserData);
		Apply();
	}

	private void Still_Tapped(object sender, TappedRoutedEventArgs e)
	{
		if (Model is null || OnAButton(e.OriginalSource as DependencyObject))
		{
			return;
		}

		Invoke();
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

	private void PlayButton_Click(object sender, RoutedEventArgs e)
	{
		if (Model?.Dto is { } dto)
		{
			_ = App.Commands.PlayDto(dto);
		}
	}

	private void Still_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA && ReferenceEquals(e.OriginalSource, this))
		{
			if (!e.KeyStatus.WasKeyDown)
			{
				Invoke();
			}

			e.Handled = true;
		}
	}


	private readonly MenuBarItemFlyout _menu = new();

	/// <summary>
	/// The still's menu: an episode's starts with "Episode details" (Enter plays it, as the shared episode row), then the
	/// shared card menu (Play, Watched, Favourite, the rest; JellyfinFlyoutConverter).
	/// </summary>
	private void FillMenu()
	{
		_menu.Items.Clear();
		if (Model?.Dto is not { } dto)
		{
			return;
		}

		if (Kind == StillKind.Episode)
		{
			_menu.Items.Add(new MenuFlyoutItem
			{
				Text = "Episode details",
				Icon = new FontIcon { Glyph = "" },
				Command = App.Commands.DisplayDtoCommand,
				CommandParameter = dto,
			});
		}

		if (new JellyfinFlyoutConverter { IncludePlay = Kind != StillKind.Episode }.Convert(dto, typeof(object), null!, "") is MenuFlyout more && more.Items.Count > 0)
		{
			if (_menu.Items.Count > 0)
			{
				_menu.Items.Add(new MenuFlyoutSeparator());
			}

			foreach (var item in more.Items.ToList())
			{
				more.Items.Remove(item);
				_menu.Items.Add(item);
			}
		}
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new CinematicStillAutomationPeer(this);
}

/// <summary>Nixie 1.4.0: what a still draws for the pointer and the focus (<see cref="CinematicStill.Look"/>).</summary>
internal readonly record struct StillLook(bool Zoom, bool Play, bool Sheen, bool Rim);

/// <summary>What a still does when it is invoked.</summary>
public enum StillKind
{
	/// <summary>Opens the item's page (Home's shelves, the library).</summary>
	Item,

	/// <summary>Plays the episode (the series page's episode shelf), as the shared episode row does.</summary>
	Episode,
}

/// <summary>
/// Narrator reads a still as the shared card is read: one list item named after the item ("The Iron Circus, S1 E3, Sawdust,
/// 45% watched"; an episode as the shared episode row, "S1E3 Sawdust"), that its Invoke opens (or plays, for an episode),
/// with its Play button under it while it shows.
/// </summary>
internal sealed partial class CinematicStillAutomationPeer(CinematicStill owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
	// a still in a shelf or a grid is an item of its list ("3 of 12"); one on its own (an episode's Next up) is a button,
	// as the shared episode row (EpisodeRowAutomationPeer.ControlTypeFor)
	protected override AutomationControlType GetAutomationControlTypeCore() => ControlTypeFor(owner.InList);

	internal static AutomationControlType ControlTypeFor(bool inList) => inList ? AutomationControlType.ListItem : AutomationControlType.Button;

	// the shared card's class name: a still is the card of this theme, and the tools that find cards (the off-screen harness's
	// FindCard, its card counts) find it as they find the shared one; Narrator reads the name, never the class
	protected override string GetClassNameCore() => "BaseItemDtoCard";

	protected override string GetNameCore()
	{
		var name = AutomationProperties.GetName(owner);
		if (!string.IsNullOrEmpty(name))
		{
			return name;
		}

		return owner.Kind == StillKind.Episode ? ItemLabels.EpisodeRowName(owner.Model?.Dto) : ItemLabels.CardName(owner.Model);
	}

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

	// as the shared card: the Play button under it while it shows
	protected override IList<AutomationPeer> GetChildrenCore() =>
		owner.VisiblePlay is { } play && FrameworkElementAutomationPeer.CreatePeerForElement(play) is { } peer ? [peer] : [];

	public void Invoke() => owner.Invoke();
}
