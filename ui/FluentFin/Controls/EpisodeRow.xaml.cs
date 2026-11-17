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
		Loaded += (_, _) => UpdatePlace();
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
		Progress.Value = ItemRowConverters.Progress(dto);
		Progress.Visibility = ItemRowConverters.InProgress(dto);
		WatchedMark.Visibility = ItemRowConverters.Watched(dto);

		foreach (var button in new ButtonBase[] { PlayButton, WatchedButton })
		{
			button.CommandParameter = dto;
		}

		AutomationProperties.SetName(PlayButton, ItemLabels.EpisodeAction("Play", dto));
		AutomationProperties.SetName(WatchedButton, ItemLabels.EpisodeAction("Watched", dto));
		WatchedButton.IsChecked = dto?.UserData?.Played;
		UpdatePlace();
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

	private void UpdateFocusRing() =>
		FocusRing.Visibility = FocusState is FocusState.Keyboard or FocusState.Programmatic ? Visibility.Visible : Visibility.Collapsed;

	private void Body_PointerEntered(object sender, PointerRoutedEventArgs e) => HoverOverlay.Visibility = Visibility.Visible;

	private void Body_PointerExited(object sender, PointerRoutedEventArgs e) => HoverOverlay.Visibility = Visibility.Collapsed;

	private void Body_Tapped(object sender, TappedRoutedEventArgs e)
	{
		// Play and Watched do their own thing
		for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, Body); node = VisualTreeHelper.GetParent(node))
		{
			if (node is ButtonBase)
			{
				return;
			}
		}

		OpenDetails();
		e.Handled = true;
	}
}

/// <summary>
/// Seaglass scrolling: Narrator reads an episode row as a list item named "S1E4 Title, watched" (<see cref="ItemLabels.EpisodeRowName"/>),
/// "4 of 8" from its place; Invoke plays it, as Enter does. Play and Watched are under it.
/// </summary>
internal sealed partial class EpisodeRowAutomationPeer(EpisodeRow owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

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
