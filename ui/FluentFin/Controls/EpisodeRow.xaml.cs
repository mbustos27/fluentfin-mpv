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
public sealed partial class EpisodeRow : UserControl
{
	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(EpisodeRow), new PropertyMetadata(null, (d, _) => ((EpisodeRow)d).Apply()));

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
			ItemUserData.Changed += OnUserDataChanged;
		};
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;
		// Seaglass 1.1.4: the toggle's name follows what it shows (a click flips it before the server hears it)
		WatchedButton.Checked += (_, _) => UpdateWatchedName();
		WatchedButton.Unchecked += (_, _) => UpdateWatchedName();
		WatchedButton.Indeterminate += (_, _) => UpdateWatchedName();
		Apply();
	}

	public BaseItemDto? Dto
	{
		get => GetValue(DtoProperty) as BaseItemDto;
		set => SetValue(DtoProperty, value);
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
		Thumb.Source = ItemArtworkConverters.Thumb(dto, 352);
		TitleText.Text = ItemRowConverters.EpisodeTitle(dto);
		var runtime = ItemRowConverters.Runtime(dto);
		RuntimeText.Text = runtime;
		RuntimeText.Visibility = runtime.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		OverviewText.Text = dto?.Overview ?? "";
		PlayButton.CommandParameter = dto;
		Label(PlayButton, ItemLabels.EpisodeAction("Play", dto));
		ApplyUserData();
		UpdatePlace();
	}

	/// <summary>Seaglass 1.1.4: the progress, the watched mark and the Watched toggle, as the server has them (<see cref="ItemUserData"/>).</summary>
	private void ApplyUserData()
	{
		var dto = Dto;
		Progress.Value = ItemRowConverters.Progress(dto);
		Progress.Visibility = ItemRowConverters.InProgress(dto);
		WatchedMark.Visibility = ItemRowConverters.Watched(dto);
		WatchedButton.IsChecked = dto?.UserData?.Played;
		UpdateWatchedName();
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

	// Seaglass 1.1.4: the toggle sends the state it shows after the click (ItemUserData; a failure springs it back)
	private async void WatchedButton_Click(object sender, RoutedEventArgs e)
	{
		if (Dto is { } dto)
		{
			await App.Commands.SetWatched(dto, WatchedButton.IsChecked == true);
		}
	}

	/// <summary>
	/// Seaglass 1.1.4: "Mark as watched, episode 3, Pilot", or "Mark as unwatched, ..." while it shows watched: what a click does
	/// now, the tooltip the same words (Seaglass UX review, fix 4; the name was "Watched" and the tooltip "Mark watched").
	/// </summary>
	private void UpdateWatchedName() => Label(WatchedButton, ItemLabels.EpisodeWatchedAction(Dto, WatchedButton.IsChecked));

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

	// Seaglass 1.1.4: the play disc says what a click or Enter does: shown while the pointer is on the row or the keyboard on it
	private void UpdatePlayDisc() =>
		PlayDisc.Visibility = _pointerOver || FocusRing.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

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
	/// Seaglass 1.1.4 (1.1.3 UX audit, interaction item 6): a click on the row plays the episode, as Enter does; Play and Watched
	/// do their own thing, and the title opens the episode's page (<see cref="Title_Tapped"/>).
	/// </summary>
	private void Body_Tapped(object sender, TappedRoutedEventArgs e)
	{
		if (RowClick.For(e.OriginalSource as DependencyObject, Body, TitleText) == RowClickAction.Play)
		{
			Play();
			e.Handled = true;
		}
	}

	private void Title_Tapped(object sender, TappedRoutedEventArgs e)
	{
		OpenDetails();
		e.Handled = true;
	}

	private void Title_PointerEntered(object sender, PointerRoutedEventArgs e) => TitleText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;

	private void Title_PointerExited(object sender, PointerRoutedEventArgs e) => TitleText.TextDecorations = Windows.UI.Text.TextDecorations.None;
}

/// <summary>Seaglass 1.1.4: what a click on an episode row does.</summary>
internal enum RowClickAction
{
	/// <summary>The row's own action: play the episode (Enter's).</summary>
	Play,

	/// <summary>The title (its page) or a button (its own action): the row does nothing more.</summary>
	None,
}

/// <summary>Seaglass 1.1.4: a click on the row plays, unless it was on one of the row's buttons or on the title (the link to the page).</summary>
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
/// Seaglass scrolling: Narrator reads an episode row as a list item named "S1E4 Title, watched" (<see cref="ItemLabels.EpisodeRowName"/>),
/// "4 of 8" from its place; Invoke plays it, as Enter does. Play and Watched are under it. A row on its own (the episode
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
		return string.IsNullOrEmpty(name) ? ItemLabels.EpisodeRowName(owner.Dto) : name;
	}

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

	public void Invoke() => owner.Play();
}
