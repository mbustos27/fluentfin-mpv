using FluentFin.Converters;
using FluentFin.Core;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass: the top of the item pages (see ItemHero.xaml). The page gives it the item (<see cref="Dto"/>), its own play buttons
/// (<see cref="PrimaryActions"/>) and the overview; the hero draws the backdrop and logo, the chips and the shared buttons, and
/// sets the window's ambient backdrop while the page is open.
/// </summary>
public sealed partial class ItemHero : UserControl
{
	// 1920 covers a full-HD window; a 4K one scales it up, which behind a scrim is not visible
	private const int BackdropWidth = 1920;

	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(ItemHero), new PropertyMetadata(null, (d, _) => ((ItemHero)d).Apply()));

	public static readonly DependencyProperty OverviewProperty =
		DependencyProperty.Register(nameof(Overview), typeof(string), typeof(ItemHero), new PropertyMetadata("", (d, e) => ((ItemHero)d).OverviewText.Text = e.NewValue as string ?? ""));

	/// <summary>1.2.0: the share of the window's height the hero takes (HeroLayout; the series and season pages give less).</summary>
	public static readonly DependencyProperty HeightShareProperty =
		DependencyProperty.Register(nameof(HeightShare), typeof(double), typeof(ItemHero), new PropertyMetadata(HeroLayout.ItemShare, (d, _) => ((ItemHero)d).Resize()));

	public static readonly DependencyProperty PrimaryActionsProperty =
		DependencyProperty.Register(nameof(PrimaryActions), typeof(object), typeof(ItemHero), new PropertyMetadata(null, (d, e) => ((ItemHero)d).PrimaryPresenter.Content = e.NewValue));

	private Uri? _ambient;
	private string? _ambientHash;

	public ItemHero()
	{
		InitializeComponent();
		UndoToast.Listen(); // 1.2.0: the Undo after a whole series or season is marked
		// 1.2.0: the toggles' names follow what they show (a click flips them before the server hears it)
		foreach (var toggle in new[] { WatchedButton, FavoriteButton })
		{
			toggle.Checked += (_, _) => UpdateActionNames();
			toggle.Unchecked += (_, _) => UpdateActionNames();
			toggle.Indeterminate += (_, _) => UpdateActionNames();
		}
		Words.SizeChanged += (_, _) => Fit(); // Nixie 1.7.5: a longer title or the logo coming grows the hero to hold them
		Loaded += (_, _) =>
		{
			AmbientBackdrop.Set(_ambient, _ambientHash);
			ItemUserData.Changed += OnUserDataChanged;
			if (XamlRoot is { } root)
			{
				root.Changed += OnRootChanged;
			}

			Resize();
		};
		Unloaded += (_, _) =>
		{
			ItemUserData.Changed -= OnUserDataChanged;
			if (XamlRoot is { } root)
			{
				root.Changed -= OnRootChanged;
			}
		};
		// a page that opens before this one is closed has already set its own; only take down what is ours
		Unloaded += (_, _) =>
		{
			if (_ambient is not null && AmbientBackdrop.Current == _ambient)
			{
				AmbientBackdrop.Set(null);
			}
		};
		Apply();
	}

	public BaseItemDto? Dto
	{
		get => GetValue(DtoProperty) as BaseItemDto;
		set => SetValue(DtoProperty, value);
	}

	public string Overview
	{
		get => (string)GetValue(OverviewProperty);
		set => SetValue(OverviewProperty, value);
	}

	public double HeightShare
	{
		get => (double)GetValue(HeightShareProperty);
		set => SetValue(HeightShareProperty, value);
	}

	private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();

	private double _shareHeight = HeroLayout.Max;

	/// <summary>
	/// Nixie 1.7.5: never shorter than its words, with the top bar's height clear above them (HeroLayout.Fit): at 1280 x 720
	/// an episode's way up and its series' logo went under the bar.
	/// </summary>
	private void Fit() =>
		HeroGrid.Height = HeroLayout.Fit(_shareHeight, Words.ActualHeight + Words.Margin.Top + Words.Margin.Bottom, TopBar.Height);

	/// <summary>
	/// 1.2.0 (1.1.3 UX audit, layout item 8): the hero is a share of the window's height (HeroLayout), so the page's
	/// first content shows on a short window; a short hero is compact: a smaller logo and two lines of overview.
	/// </summary>
	private void Resize()
	{
		var height = HeroLayout.Height(XamlRoot?.Size.Height ?? 0, HeightShare);
		_shareHeight = height;
		Fit();
		var compact = HeroLayout.IsCompact(height);
		var episode = Dto?.Type == BaseItemDto_Type.Episode;
		// an episode's logo is its series': small, under the episode's own title in importance (layout audit item 21)
		LogoImage.MaxHeight = episode ? (compact ? 64 : 88) : compact ? 96 : 160;
		OverviewText.CollapsedLines = compact ? 2 : 4;
		Words.Spacing = compact ? 10 : 14;
	}

	public object? PrimaryActions
	{
		get => GetValue(PrimaryActionsProperty);
		set => SetValue(PrimaryActionsProperty, value);
	}

	/// <summary>
	/// The page's top heading: the logo (named for the item) when there is one, else the title as text. 1.2.0 (the states
	/// stream's Narrator pass): with no title (a page that failed to load, the item still unknown) there is no heading at all,
	/// not an empty one; the page's load message says what happened.
	/// </summary>
	internal static Visibility HeadingVisibility(bool hasLogo, string title, bool forLogo) =>
		!string.IsNullOrWhiteSpace(title) && hasLogo == forLogo ? Visibility.Visible : Visibility.Collapsed;

	private void Apply()
	{
		var dto = Dto;
		BackdropImage.Source = ItemArtworkConverters.Keep(BackdropImage.Source, ItemArtworkConverters.Backdrop(dto, BackdropWidth));

		var logo = ItemArtworkConverters.Logo(dto, 480);
		LogoImage.Source = ItemArtworkConverters.Keep(LogoImage.Source, logo);
		var title = ItemMeta.HeroTitle(dto);
		LogoImage.Visibility = HeadingVisibility(hasLogo: logo is not null, title, forLogo: true);
		TitleText.Text = title;
		// the logo is the title for the eye; the heading stays for Narrator
		TitleText.Visibility = HeadingVisibility(hasLogo: logo is not null, title, forLogo: false);
		AutomationProperties.SetName(LogoImage, title);

		var subtitle = ItemMeta.HeroSubtitle(dto);
		SubtitleText.Text = subtitle;
		SubtitleText.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		// 1.2.0: one line of plain text, not chips; an episode's number leads it
		MetaText.Text = ItemChips.MetaLine(dto);
		AutomationProperties.SetName(MetaText, ItemChips.MetaName(dto));
		MetaText.Visibility = MetaText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		Resize();

		TrailerButton.Visibility = Services.TrailerPlayback.ButtonVisibility(dto);
		ApplyUserData();
		RatingPanel.Item = dto;

		_ambient = ItemArtworkConverters.AmbientBackdrop(dto);
		_ambientHash = ItemArtwork.BackdropBlurHash(dto);
		if (IsLoaded)
		{
			AmbientBackdrop.Set(_ambient, _ambientHash);
		}
	}

	/// <summary>
	/// 1.2.0: the toggles and the More menu (built from the item, so its "Mark as watched" text is the item's state)
	/// show the item's user data as the server has it: on load and after every change (<see cref="ItemUserData"/>).
	/// </summary>
	private void ApplyUserData()
	{
		var dto = Dto;
		WatchedButton.IsChecked = dto?.UserData?.Played;
		FavoriteButton.IsChecked = dto?.UserData?.IsFavorite;
		// 1.2.0 (Nixie button review, I26): Watched and Favourite are buttons beside it, so the "…" leaves them out
		// 1.7.5: shown only with something in it (a series' or a season's had nothing for a user who isn't an administrator)
		JellyfinFlyoutConverter.ApplyPageMenu(MoreButton, dto);
		UpdateActionNames();
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

	// 1.2.0: a toggle sends the state it shows after the click (ItemUserData; a failure springs it back)
	private async void WatchedButton_Click(object sender, RoutedEventArgs e)
	{
		if (Dto is { } dto)
		{
			await App.Commands.SetWatched(dto, WatchedButton.IsChecked == true);
		}
	}

	private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		if (Dto is { } dto)
		{
			await App.Commands.SetFavorite(dto, FavoriteButton.IsChecked == true);
		}
	}

	/// <summary>
	/// 1.2.0: "Mark Inception as watched" / "... as unwatched", "Add Inception to favorites" / "Remove ...", "More options
	/// for Inception": named for the item and for what a click does now, the tooltip the same words (Seaglass UX review, fix 4).
	/// </summary>
	private void UpdateActionNames()
	{
		var dto = Dto;
		Label(WatchedButton, ItemLabels.WatchedAction(dto, WatchedButton.IsChecked));
		Label(FavoriteButton, ItemLabels.FavoriteAction(dto, FavoriteButton.IsChecked));
		FavoriteGlyph.Glyph = Converters.ItemRowConverters.FavoriteGlyph(FavoriteButton.IsChecked);
		Label(MoreButton, ItemLabels.MoreOptions(dto));
	}

	private static void Label(FrameworkElement button, string name)
	{
		AutomationProperties.SetName(button, name);
		ToolTipService.SetToolTip(button, name);
	}

	// Nixie 1.3.1: the trailer plays in Nixie (a local one in the player, YouTube in the theatre window)
	private async void TrailerButton_Click(object sender, RoutedEventArgs e) => await Services.TrailerPlayback.Play(Dto, TrailerButton);
}
