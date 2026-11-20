using FluentFin.Converters;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>Editorial's details in place (see InlineDetailsRow.xaml).</summary>
public sealed partial class InlineDetailsRow : UserControl
{
	public static readonly DependencyProperty ItemProperty =
		DependencyProperty.Register(nameof(Item), typeof(BaseItemViewModel), typeof(InlineDetailsRow), new PropertyMetadata(null, (d, _) => ((InlineDetailsRow)d).OnItemChanged()));

	private int _load;

	public InlineDetailsRow()
	{
		InitializeComponent();
		foreach (var toggle in new[] { WatchedButton, FavoriteButton })
		{
			toggle.Checked += (_, _) => UpdateNames();
			toggle.Unchecked += (_, _) => UpdateNames();
		}

		Loaded += (_, _) => ItemUserData.Changed += OnUserDataChanged;
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;
		AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
	}

	/// <summary>The item shown (null: nothing).</summary>
	public BaseItemViewModel? Item
	{
		get => GetValue(ItemProperty) as BaseItemViewModel;
		set => SetValue(ItemProperty, value);
	}

	/// <summary>Close (the button, Esc): the host takes the panel away and gives the poster the focus back.</summary>
	public event EventHandler? CloseRequested;

	/// <summary>Up from the buttons: back to the poster.</summary>
	public event EventHandler? UpRequested;

	/// <summary>The keyboard to Play (an opening by the keyboard lands there, as the concept shows).</summary>
	public bool FocusPlay(FocusState state = FocusState.Keyboard) => PlayButton.Focus(state);

	private async void OnItemChanged()
	{
		var item = Item;
		Show(item?.Dto);
		if (item?.Dto.Id is not { } id)
		{
			return;
		}

		// the row's record has no cast, streams or trailer: the whole item, once, while the panel shows what it has
		var ticket = ++_load;
		BaseItemDto? full = null;
		try
		{
			full = await App.GetService<IJellyfinClient>().GetItem(id);
		}
		catch (Exception)
		{
			// the panel keeps what the row knew
		}

		if (ticket == _load && full is not null && ReferenceEquals(Item, item))
		{
			full.UserData ??= item.Dto.UserData;
			Show(full);
		}
	}

	private BaseItemDto? _shown;

	private void Show(BaseItemDto? dto)
	{
		_shown = dto;
		TitleText.Text = dto?.Type == BaseItemDto_Type.Episode ? ItemLabels.ActionTarget(dto) : dto?.Name?.Trim() ?? "";
		MetaText.Text = EditorialHome.HeroMeta(dto, genres: 2);
		Badges.ItemsSource = EditorialCards.Badges(dto);
		OverviewText.Text = dto?.Overview?.Trim() ?? "";
		OverviewText.Visibility = OverviewText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		PeopleText.Text = EditorialCards.People(dto);
		PeopleText.Visibility = PeopleText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		Backdrop.ImageSource = ItemArtworkConverters.Backdrop(dto, 1280);
		TrailerButton.Visibility = Services.TrailerPlayback.ButtonVisibility(dto);
		PlayText.Text = ItemLabels.ResumePosition(dto) is not null && dto?.Type is not (BaseItemDto_Type.Series or BaseItemDto_Type.Season) ? "Resume" : "Play";
		AutomationProperties.SetName(PlayButton, ItemLabels.PlayAction(dto));
		ToolTipService.SetToolTip(PlayButton, ItemLabels.PlayAction(dto));
		AutomationProperties.SetName(FullDetailsButton, $"Full details of {ItemLabels.ActionTarget(dto)}");
		AutomationProperties.SetName(CloseButton, $"Close the details of {ItemLabels.ActionTarget(dto)}");
		AutomationProperties.SetName(this, $"Details of {ItemLabels.ActionTarget(dto)}");
		MoreButton.Flyout = dto is null ? null : new JellyfinFlyoutConverter { IncludeToggles = false }.Convert(dto, typeof(object), null!, "") as FlyoutBase;
		ApplyUserData();
	}

	private void ApplyUserData()
	{
		WatchedButton.IsChecked = _shown?.UserData?.Played == true;
		FavoriteButton.IsChecked = _shown?.UserData?.IsFavorite == true;
		UpdateNames();
	}

	private void UpdateNames()
	{
		Label(WatchedButton, ItemLabels.WatchedAction(_shown, WatchedButton.IsChecked));
		Label(FavoriteButton, ItemLabels.FavoriteAction(_shown, FavoriteButton.IsChecked));
		FavoriteGlyph.Glyph = ItemRowConverters.FavoriteGlyph(FavoriteButton.IsChecked);
		Label(MoreButton, ItemLabels.MoreOptions(_shown));
	}

	private static void Label(FrameworkElement button, string name)
	{
		AutomationProperties.SetName(button, name);
		ToolTipService.SetToolTip(button, name);
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (_shown?.Id is not { } id || id != e.ItemId)
		{
			return;
		}

		_shown.UserData = e.UserData;
		ApplyUserData();
	}

	private void PlayButton_Click(object sender, RoutedEventArgs e)
	{
		if (_shown is { } dto)
		{
			App.Commands.PlayDtoCommand.Execute(dto);
		}
	}

	// Nixie 1.3.1: the trailer plays in Nixie (a local one in the player, YouTube in the theatre window)
	private async void TrailerButton_Click(object sender, RoutedEventArgs e) => await Services.TrailerPlayback.Play(_shown, TrailerButton);

	private void FullDetailsButton_Click(object sender, RoutedEventArgs e)
	{
		if (_shown is { } dto)
		{
			App.Commands.DisplayDto(dto);
		}
	}

	private async void WatchedButton_Click(object sender, RoutedEventArgs e)
	{
		if (_shown is { } dto)
		{
			await App.Commands.SetWatched(dto, WatchedButton.IsChecked == true);
		}
	}

	private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		if (_shown is { } dto)
		{
			await App.Commands.SetFavorite(dto, FavoriteButton.IsChecked == true);
		}
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

	private void OnKeyDown(object sender, KeyRoutedEventArgs e)
	{
		switch (e.Key)
		{
			case VirtualKey.Escape or VirtualKey.GamepadB:
				e.Handled = true;
				CloseRequested?.Invoke(this, EventArgs.Empty);
				break;
			case VirtualKey.Up or VirtualKey.GamepadDPadUp when !e.Handled:
				e.Handled = true;
				UpRequested?.Invoke(this, EventArgs.Empty);
				break;
			case VirtualKey.Left or VirtualKey.Right when !e.Handled && e.OriginalSource is DependencyObject source && IsIn(source, Buttons):
				// along the buttons, as a toolbar
				e.Handled = FocusManager.TryMoveFocus(e.Key == VirtualKey.Left ? FocusNavigationDirection.Left : FocusNavigationDirection.Right,
					new FindNextElementOptions { SearchRoot = Buttons });
				break;
		}
	}

	private static bool IsIn(DependencyObject element, DependencyObject ancestor)
	{
		for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, ancestor))
			{
				return true;
			}
		}

		return false;
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new InlineDetailsRowAutomationPeer(this);
}

/// <summary>Narrator: a group named "Details of Glass Canyon", its buttons inside.</summary>
internal sealed partial class InlineDetailsRowAutomationPeer(InlineDetailsRow owner) : FrameworkElementAutomationPeer(owner)
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

	protected override string GetClassNameCore() => nameof(InlineDetailsRow);

	protected override bool IsControlElementCore() => true;
}
