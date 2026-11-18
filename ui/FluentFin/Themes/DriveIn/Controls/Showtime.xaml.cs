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
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>Drive-in: one episode on the SHOWTIMES board (see Showtime.xaml). One keyboard stop; Enter plays.</summary>
public sealed partial class Showtime : UserControl
{
	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(Showtime), new PropertyMetadata(null, (d, _) => ((Showtime)d).Apply()));

	private readonly MenuBarItemFlyout _menu = new();
	private BaseItemDto? _thumbFor;
	private Windows.Foundation.Rect? _viewport;
	private bool _pointerOver;

	public Showtime()
	{
		InitializeComponent();
		_menu.Opening += (_, _) => FillMenu();
		ContextFlyout = _menu;
		AddHandler(KeyDownEvent, new KeyEventHandler(Row_KeyDown), false);
		GotFocus += (_, _) => UpdateFocus();
		LostFocus += (_, _) => UpdateFocus();
		Loaded += (_, _) =>
		{
			UpdatePlace();
			UpdateNext();
			ItemUserData.Changed += OnUserDataChanged;
		};
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;
		EffectiveViewportChanged += (_, e) =>
		{
			_viewport = e.EffectiveViewport;
			LoadThumbWhenNear();
		};
		ProgressTrack.SizeChanged += (_, _) => ApplyUserData();
		Apply();
	}

	public BaseItemDto? Dto { get => GetValue(DtoProperty) as BaseItemDto; set => SetValue(DtoProperty, value); }

	internal void Play()
	{
		if (Dto is { } dto)
		{
			App.Commands.PlayDtoCommand.Execute(dto);
		}
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new ShowtimeAutomationPeer(this);

	private void Apply()
	{
		var dto = Dto;
		Thumb.Source = null;
		_thumbFor = dto;
		LoadThumbWhenNear();
		Number.Text = DriveInText.EpisodeNumber(dto?.IndexNumber);
		Number.Visibility = Number.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		TitleText.Text = dto?.Name?.Trim() ?? "";
		FactsText.Text = DriveInItems.ShowtimeFacts(dto);
		FactsText.Visibility = FactsText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		OverviewText.Text = dto?.Overview ?? "";
		OverviewText.Visibility = OverviewText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		ApplyUserData();
		UpdatePlace();
		UpdateNext();
	}

	private void LoadThumbWhenNear()
	{
		if (_thumbFor is { } dto && EpisodeThumbs.IsNear(_viewport, ActualHeight))
		{
			_thumbFor = null;
			Thumb.Source = ItemArtworkConverters.Thumb(dto, 336);
		}
	}

	private void ApplyUserData()
	{
		var dto = Dto;
		var progress = ItemRowConverters.Progress(dto);
		ProgressTrack.Visibility = ItemRowConverters.InProgress(dto);
		ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * Math.Clamp(progress, 0, 100) / 100);
		WatchedMark.Visibility = ItemRowConverters.Watched(dto);
		UpdateNext();
	}

	/// <summary>NEXT on the row Play starts; the watched mark otherwise when watched.</summary>
	internal void UpdateNext()
	{
		var next = Dto?.Id is { } id && ShowtimesList.Of(this)?.Next?.Id == id;
		NextText.Visibility = next ? Visibility.Visible : Visibility.Collapsed;
		if (next)
		{
			WatchedMark.Visibility = Visibility.Collapsed;
		}
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (Dto is { Id: { } id } dto && id == e.ItemId)
		{
			dto.UserData = e.UserData;
			ApplyUserData();
		}
	}

	private void UpdatePlace()
	{
		if (ShowtimesList.Of(this) is not { } list)
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

	private void UpdateFocus()
	{
		var keyboard = FocusState is FocusState.Keyboard or FocusState.Programmatic;
		FocusRing.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
		FocusGlow.Visibility = keyboard && !BulbFrame.HighContrast() ? Visibility.Visible : Visibility.Collapsed;
		UpdatePlayMark();
	}

	private void UpdatePlayMark() => PlayMark.Opacity = _pointerOver || FocusRing.Visibility == Visibility.Visible ? 1 : 0;

	private void Body_PointerEntered(object sender, PointerRoutedEventArgs e)
	{
		_pointerOver = true;
		HoverOverlay.Visibility = Visibility.Visible;
		UpdatePlayMark();
	}

	private void Body_PointerExited(object sender, PointerRoutedEventArgs e)
	{
		_pointerOver = false;
		HoverOverlay.Visibility = Visibility.Collapsed;
		UpdatePlayMark();
	}

	private void Body_Tapped(object sender, TappedRoutedEventArgs e)
	{
		Play();
		e.Handled = true;
	}
}

/// <summary>A showtime is a list item "S1 E3 Sawdust, 22% watched" ("3 of 8" from its place); Invoke plays it, as Enter does.</summary>
internal sealed partial class ShowtimeAutomationPeer(Showtime owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() =>
		ShowtimesList.Of(owner) is null ? AutomationControlType.Button : AutomationControlType.ListItem;

	protected override string GetClassNameCore() => nameof(Showtime);

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
