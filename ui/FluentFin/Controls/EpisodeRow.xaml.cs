using FluentFin.Converters;
using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>Seaglass: one episode as an opaque card (see EpisodeRow.xaml); the item is <see cref="Dto"/>. One keyboard stop.</summary>
public sealed partial class EpisodeRow : UserControl, IUpNextMarked
{
	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(EpisodeRow), new PropertyMetadata(null, (d, _) =>
		{
			var row = (EpisodeRow)d;
			row.Apply();
			row.UpdateUpNext();
		}));

	/// <summary>The row's menu (a right click, the menu key, Shift+F10, a long press), made when it opens so it says what is true now.</summary>
	private readonly MenuBarItemFlyout _menu = new();

	public EpisodeRow()
	{
		InitializeComponent();
		_menu.Opening += (_, _) => FillMenu();
		ContextFlyout = _menu;
		AddHandler(KeyDownEvent, new KeyEventHandler(Row_KeyDown), false);
		GotFocus += (_, _) => UpdateFocusRing();
		LostFocus += (_, _) => UpdateFocusRing();
		Loaded += (_, _) =>
		{
			UpdatePlace();
			UpdateUpNext();
			ItemUserData.Changed += OnUserDataChanged;
		};
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;
		EffectiveViewportChanged += (_, e) =>
		{
			_viewport = e.EffectiveViewport;
			LoadThumbWhenNear();
		};
		Apply();
	}

	public BaseItemDto? Dto
	{
		get => GetValue(DtoProperty) as BaseItemDto;
		set => SetValue(DtoProperty, value);
	}

	/// <summary>Nixie 1.4.0: this is the episode the page's Play starts (UpNextMark): it says "Up next" beside its title.</summary>
	internal bool IsUpNext { get; private set; }

	public void UpdateUpNext()
	{
		IsUpNext = UpNextMark.IsUpNext(this, Dto?.Id);
		UpNextBadge.Visibility = IsUpNext ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>What Enter (and Narrator's Invoke) does: play the episode.</summary>
	internal void Play()
	{
		if (Dto is { } dto)
		{
			App.Commands.PlayDtoCommand.Execute(dto);
		}
	}

	/// <summary>What a click on the row does, and the menu's first item: the episode's page.</summary>
	internal void OpenDetails()
	{
		if (Dto is { } dto)
		{
			App.Commands.DisplayDto(dto);
		}
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new EpisodeRowAutomationPeer(this);

	private void Apply()
	{
		var dto = Dto;
		// 1.2.0 (items review, item 6): the thumbnail comes when the row nears the screen, not for every row of a long season at once
		Thumb.Source = null;
		_thumbFor = dto;
		LoadThumbWhenNear();
		TitleText.Text = ItemRowConverters.EpisodeTitle(dto);
		// 1.2.0: the runtime on the title's line: "S1 E1 · The Archive Problem · 10m"
		var runtime = ItemRowConverters.Runtime(dto);
		RuntimeText.Text = runtime.Length > 0 ? "· " + runtime : "";
		RuntimeText.Visibility = runtime.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		OverviewText.Text = dto?.Overview ?? "";
		Label(MoreButton, ItemLabels.EpisodeAction("More options", dto));
		ApplyUserData();
		UpdatePlace();
	}

	/// <summary>
	/// 1.2.0: the progress and the watched mark, as the server has them (<see cref="ItemUserData"/>): one mark for
	/// watched (the thumbnail's), not a second check button at the row's end; "Mark as watched" is in the row's menu.
	/// </summary>
	private BaseItemDto? _thumbFor;
	private Windows.Foundation.Rect? _viewport;

	private void LoadThumbWhenNear()
	{
		if (_thumbFor is { } dto && EpisodeThumbs.IsNear(_viewport, ActualHeight))
		{
			_thumbFor = null;
			Thumb.Source = ItemArtworkConverters.Thumb(dto, 352);
		}
	}

	private void ApplyUserData()
	{
		var dto = Dto;
		Progress.Value = ItemRowConverters.Progress(dto);
		Progress.Visibility = ItemRowConverters.InProgress(dto);
		WatchedMark.Visibility = ItemRowConverters.Watched(dto);
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (Dto is not { Id: { } id } dto || id != e.ItemId)
		{
			return;
		}

		dto.UserData = e.UserData;
		ApplyUserData();
	}

	/// <summary>1.2.0: the row's "…": the same menu as a right click or the menu key (the episode's page, watched, favourite, the rest).</summary>
	private void MoreButton_Click(object sender, RoutedEventArgs e) => _menu.ShowAt(MoreButton);

	private static void Label(FrameworkElement button, string name)
	{
		AutomationProperties.SetName(button, name);
		ToolTipService.SetToolTip(button, name);
	}

	/// <summary>Narrator's "4 of 8": the row's place in its episode list.</summary>
	private void UpdatePlace()
	{
		if (EpisodeList.Of(this) is not { } list)
		{
			return;
		}

		var (position, size) = list.PlaceOf(this);
		if (position > 0)
		{
			AutomationProperties.SetPositionInSet(this, position);
			AutomationProperties.SetSizeOfSet(this, size);
		}
	}

	/// <summary>The episode's page first, then the More menu as a card's (favourite, the rest): its items are the converter's.</summary>
	private void FillMenu()
	{
		_menu.Items.Clear();
		if (Dto is not { } dto)
		{
			return;
		}

		_menu.Items.Add(new MenuFlyoutItem
		{
			Text = "Episode details",
			Icon = new FontIcon { Glyph = "" },
			Command = App.Commands.DisplayDtoCommand,
			CommandParameter = dto,
		});

		if (new JellyfinFlyoutConverter().Convert(dto, typeof(object), null!, "") is MenuFlyout more && more.Items.Count > 0)
		{
			_menu.Items.Add(new MenuFlyoutSeparator());
			foreach (var item in more.Items.ToList())
			{
				more.Items.Remove(item);
				_menu.Items.Add(item);
			}
		}
	}

	private void Row_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		// the row's own keys, when the row (not one of its buttons) has the focus
		if (!ReferenceEquals(e.OriginalSource, this) || e.Key is not (VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA))
		{
			return;
		}

		if (!e.KeyStatus.WasKeyDown)
		{
			Play();
		}

		e.Handled = true;
	}

	private bool _pointerOver;

	private void UpdateFocusRing()
	{
		FocusRing.Visibility = FocusState is FocusState.Keyboard or FocusState.Programmatic ? Visibility.Visible : Visibility.Collapsed;
		UpdatePlayDisc();
	}

	// 1.2.0: the play disc on the thumbnail says what a click or Enter does: shown while the pointer is on the row or the
	// keyboard on it; never collapsed, so Narrator and UI Automation always find Play (its opacity hides it)
	private void UpdatePlayDisc() =>
		PlayDisc.Opacity = _pointerOver || FocusRing.Visibility == Visibility.Visible ? 1 : 0;

	private void Body_PointerEntered(object sender, PointerRoutedEventArgs e)
	{
		_pointerOver = true;
		HoverOverlay.Visibility = Visibility.Visible;
		UpdatePlayDisc();
	}

	private void Body_PointerExited(object sender, PointerRoutedEventArgs e)
	{
		_pointerOver = false;
		HoverOverlay.Visibility = Visibility.Collapsed;
		UpdatePlayDisc();
	}

	/// <summary>
	/// 1.2.0 (1.1.3 UX audit, interaction item 6): a click on the row plays the episode, as Enter does; Play and the "…"
	/// do their own thing, and the title's link opens the episode's page (<see cref="TitleLink_Click"/>).
	/// </summary>
	private void Body_Tapped(object sender, TappedRoutedEventArgs e)
	{
		if (RowClick.For(e.OriginalSource as DependencyObject, Body, TitleLink) == RowClickAction.Play)
		{
			Play();
			e.Handled = true;
		}
	}

	private void TitleLink_Click(object sender, RoutedEventArgs e) => OpenDetails();

	private void Title_PointerEntered(object sender, PointerRoutedEventArgs e) => TitleText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;

	private void Title_PointerExited(object sender, PointerRoutedEventArgs e) => TitleText.TextDecorations = Windows.UI.Text.TextDecorations.None;
}

/// <summary>
/// 1.2.0 (items review, item 6): an episode row asks for its thumbnail when it is within a screen of the page's view, so a
/// season of hundreds of episodes does not download and decode hundreds of pictures at once. The rows themselves stay (one
/// Tab stop each, Home/End, Narrator's "n of m" and Back's focus need them; EpisodeList).
/// </summary>
internal static class EpisodeThumbs
{
	/// <summary>How far outside the view a row already asks for its picture.</summary>
	public const double Margin = 1000;

	/// <param name="viewport">The view's rectangle in the row's own coordinates (EffectiveViewportChanged): null before the
	/// first one (the row is not laid out yet: it waits); empty or endless outside any scroller (then it is shown: it asks).</param>
	public static bool IsNear(Windows.Foundation.Rect? viewport, double rowHeight)
	{
		if (viewport is not { } view)
		{
			return false;
		}

		if (view.IsEmpty || double.IsInfinity(view.Width) || double.IsInfinity(view.Height))
		{
			return true;
		}

		return view.Bottom >= -Margin && view.Top <= Math.Max(0, rowHeight) + Margin;
	}
}

/// <summary>1.2.0: what a click on an episode row does.</summary>
internal enum RowClickAction
{
	/// <summary>The row's own action: play the episode (Enter's).</summary>
	Play,

	/// <summary>The title (its page) or a button (its own action): the row does nothing more.</summary>
	None,
}

/// <summary>1.2.0: a click on the row plays, unless it was on one of the row's buttons or on the title (the link to the page).</summary>
internal static class RowClick
{
	public static RowClickAction For(DependencyObject? source, DependencyObject row, DependencyObject title)
	{
		for (var node = source; node is not null && !ReferenceEquals(node, row); node = VisualTreeHelper.GetParent(node))
		{
			if (node is ButtonBase || ReferenceEquals(node, title))
			{
				return RowClickAction.None;
			}
		}

		return RowClickAction.Play;
	}
}

/// <summary>
/// Seaglass scrolling: Narrator reads an episode row as a list item named "S1 E4 Title, watched" (<see cref="ItemLabels.EpisodeRowName"/>),
/// "4 of 8" from its place; Invoke plays it, as Enter does. The "…" is under it (the Play disc is decorative). A row on its own (the episode
/// page's "Up next") is a button: a list item with no list around it would say "1 of 1" or nothing at all.
/// </summary>
internal sealed partial class EpisodeRowAutomationPeer(EpisodeRow owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
	internal static AutomationControlType ControlTypeFor(bool inList) => inList ? AutomationControlType.ListItem : AutomationControlType.Button;

	protected override AutomationControlType GetAutomationControlTypeCore() => ControlTypeFor(EpisodeList.Of(owner) is not null);

	protected override string GetClassNameCore() => nameof(EpisodeRow);

	protected override string GetNameCore()
	{
		var name = AutomationProperties.GetName(owner);
		return string.IsNullOrEmpty(name) ? UpNextMark.Spoken(ItemLabels.EpisodeRowName(owner.Dto), owner.IsUpNext) : name;
	}

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

	public void Invoke() => owner.Play();
}
