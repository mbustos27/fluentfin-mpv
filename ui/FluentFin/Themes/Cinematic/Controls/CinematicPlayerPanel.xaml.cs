using System.ComponentModel;
using System.Globalization;
using FluentFin.Controls;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>Cinematic's player tabs and their panel (see CinematicPlayerPanel.xaml).</summary>
public sealed partial class CinematicPlayerPanel : UserControl
{
	/// <summary>The panels, in the tabs' order.</summary>
	public enum Part
	{
		Info,
		Episodes,
		Chapters,
		Tracks,
	}

	/// <summary>The subtitle sizes the panel offers (the shared menu's slider has every step from 50 to 200 %).</summary>
	public static readonly IReadOnlyList<(string Label, int Percent)> Sizes = [("Small", 80), ("Medium", SubtitleSizes.Default), ("Large", 130)];

	private MpvControlsViewModel? _controls;
	private MpvPlayerControls? _player;
	private Part? _open;

	/// <summary>Nixie 1.4.0: where the keyboard was when the bar's Audio and subtitles button opened the panel; it goes back there.</summary>
	private WeakReference<Control>? _returnTo;
	private bool _menuHeld;
	private BaseItemDto? _item;
	private string? _itemFor;

	public CinematicPlayerPanel()
	{
		CinematicResources.Ensure();
		InitializeComponent();
		// Nixie 1.3.1: the tabs and the panel are a list to the player's key router: with the keyboard in them, the arrows, Home,
		// End, Space and Enter are theirs; every other player key still works (docs/SHORTCUTS.md)
		MpvPlayerKeys.SetOwnsArrows(this, true);
		Loaded += (_, _) => { Attach(); ShowTabs(); };
		Unloaded += (_, _) =>
		{
			Close(returnFocus: false);
			Detach();
		};
		ShowTabs();
	}

	/// <summary>The panel open now, or null.</summary>
	public Part? Open => _open;

	private void Attach()
	{
		Detach();
		for (var node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is MpvPlayerControls player)
			{
				_controls = player.Controls;
				_player = player;
				break;
			}
		}

		// Nixie 1.4.0 (the UX review's C-m9): the bar's Audio and subtitles button opens this panel's part, not a second UI
		_player?.UseThemeTracksPanel(OpenTracksFromBar);

		if (_controls is not null)
		{
			_controls.PropertyChanged += OnControlsChanged;
			_controls.ClosePanelsRequested += OnClosePanelsRequested;
		}

		ShowEnds();
	}

	private void Detach()
	{
		_player?.UseThemeTracksPanel(null);
		_player = null;
		if (_controls is not null)
		{
			_controls.PropertyChanged -= OnControlsChanged;
			_controls.ClosePanelsRequested -= OnClosePanelsRequested;
			_controls = null;
		}
	}

	/// <summary>
	/// Nixie 1.3.1: Escape reached the page with the panel open (the keyboard on the video: a click on a tab leaves it there): the
	/// panel closes, as a menu would. The keyboard goes back to the tabs only if it was in the panel.
	/// </summary>
	private void OnClosePanelsRequested(object? sender, System.ComponentModel.HandledEventArgs e)
	{
		if (_open is null || e.Handled)
		{
			return;
		}

		var inside = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsInside(focused);
		Close(returnFocus: inside);
		e.Handled = true;
	}

	private bool IsInside(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, this))
			{
				return true;
			}
		}

		return false;
	}

	private void OnControlsChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(() =>
	{
		switch (e.PropertyName)
		{
			case nameof(MpvControlsViewModel.PositionTicks) or nameof(MpvControlsViewModel.DurationTicks) or nameof(MpvControlsViewModel.Speed):
				ShowEnds();
				break;
			case nameof(MpvControlsViewModel.AudioChoices) or nameof(MpvControlsViewModel.SubtitleChoices) or nameof(MpvControlsViewModel.SubtitleSize) or nameof(MpvControlsViewModel.SubtitleDelay):
				if (_open == Part.Tracks)
				{
					FillTracks();
				}

				break;
			case nameof(MpvControlsViewModel.Title) or nameof(MpvControlsViewModel.Subtitle):
				_itemFor = null;
				if (_open is { } open)
				{
					_ = Fill(open);
				}

				break;
		}
	});

	/// <summary>"Ends at 10:42 PM": now plus what is left at the speed playing.</summary>
	private void ShowEnds()
	{
		if (_controls is not { DurationTicks: > 0 } controls)
		{
			EndsText.Text = "";
			return;
		}

		var left = TimeSpan.FromTicks(Math.Max(0, controls.DurationTicks - controls.PositionTicks) / (long)Math.Max(1, Math.Round(controls.Speed * 100)) * 100);
		EndsText.Text = "Ends at " + DateTime.Now.Add(left).ToString("t", CultureInfo.CurrentCulture);
	}

	private void ShowTabs() => Tabs.SetTabs(
	[
		new CinematicTab("Info", "Info", _open == Part.Info, Part.Info),
		new CinematicTab("Episodes", "Episodes", _open == Part.Episodes, Part.Episodes),
		new CinematicTab("Chapters", "Chapters", _open == Part.Chapters, Part.Chapters),
		new CinematicTab("Audio & subtitles", "Audio and subtitles", _open == Part.Tracks, Part.Tracks),
	]);

	private void Tabs_TabInvoked(object? sender, CinematicTab tab)
	{
		if (tab.Payload is not Part part)
		{
			return;
		}

		if (_open == part)
		{
			Close(returnFocus: true);
		}
		else
		{
			_ = Show(part);
		}
	}

	/// <summary>
	/// Nixie 1.4.0: the bar's Audio and subtitles button: the Audio and subtitles part opens (or closes again) with the keyboard
	/// in it, as the shared flyout takes it; closing gives it back to where it was (the player, or the button).
	/// </summary>
	internal void OpenTracksFromBar()
	{
		if (_open == Part.Tracks)
		{
			Close(returnFocus: true);
			return;
		}

		var returnTo = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Control focused ? focused : null;
		_ = ShowFromBar(returnTo);
	}

	private async Task ShowFromBar(Control? returnTo)
	{
		await Show(Part.Tracks);
		_returnTo = returnTo is null ? null : new WeakReference<Control>(returnTo);
		// the keyboard into the part once its choices are laid out, and after the player's after-click step (which runs at
		// low priority once the click is done and would take it back): queued twice (seen in the keys matrix: Down went to
		// the volume)
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusFirstChoice));
	}

	private void FocusFirstChoice()
	{
		if (_open == Part.Tracks && (AudioList.Children.OfType<Button>().FirstOrDefault() ?? SubtitleList.Children.OfType<Button>().FirstOrDefault()) is { } first)
		{
			first.Focus(FocusState.Programmatic);
		}
	}

	private async Task Show(Part part)
	{
		_returnTo = null;
		_open = part;
		if (!_menuHeld)
		{
			_menuHeld = true;
			_controls?.MenuOpened(true);
		}

		Panel.Visibility = Visibility.Visible;
		ShowTabs();
		await Fill(part);
	}

	private void Close(bool returnFocus)
	{
		if (_open is null)
		{
			return;
		}

		_open = null;
		Panel.Visibility = Visibility.Collapsed;
		if (_menuHeld)
		{
			_menuHeld = false;
			_controls?.MenuOpened(false);
		}

		ShowTabs();
		var back = _returnTo;
		_returnTo = null;
		if (returnFocus && back?.TryGetTarget(out var target) == true && target.IsLoaded)
		{
			// opened by the bar's button: the keyboard goes back where it was, as after the shared flyout
			target.Focus(FocusState.Programmatic);
		}
		else if (returnFocus)
		{
			Tabs.FocusSelected(FocusState.Keyboard);
		}
	}

	private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Escape && _open is not null)
		{
			Close(returnFocus: true);
			e.Handled = true;
		}
	}

	// ---- the parts -----------------------------------------------------------------------------------------------------

	private async Task Fill(Part part)
	{
		InfoPart.Visibility = part == Part.Info ? Visibility.Visible : Visibility.Collapsed;
		EpisodesPart.Visibility = part == Part.Episodes ? Visibility.Visible : Visibility.Collapsed;
		ChaptersPart.Visibility = part == Part.Chapters ? Visibility.Visible : Visibility.Collapsed;
		TracksPart.Visibility = part == Part.Tracks ? Visibility.Visible : Visibility.Collapsed;
		EmptyText.Visibility = Visibility.Collapsed;

		if (part == Part.Tracks)
		{
			FillTracks();
			return;
		}

		var item = await Item();
		if (_open != part)
		{
			return;
		}

		switch (part)
		{
			case Part.Info:
				InfoKicker.Text = CinematicLabels.ItemKicker(item).ToUpper(CultureInfo.CurrentCulture);
				InfoTitle.Text = _controls?.Title is { Length: > 0 } title ? title : CinematicLabels.TitleArtText(item);
				InfoMeta.Text = string.Join(" · ", new[] { _controls?.Subtitle ?? "", CinematicLabels.MetaFacts(item) }.Where(s => s.Length > 0));
				InfoOverview.Text = item?.Overview?.Trim() ?? "";
				break;
			case Part.Episodes:
				await FillEpisodes(item);
				break;
			case Part.Chapters:
				FillChapters(item);
				break;
		}
	}

	/// <summary>The playing item, asked for once per item (its chapters, overview and season).</summary>
	private async Task<BaseItemDto?> Item()
	{
		var id = _controls?.ItemId;
		if (id is null || !Guid.TryParse(id, out var guid))
		{
			return null;
		}

		if (_itemFor != id)
		{
			_itemFor = id;
			_item = await App.GetService<IJellyfinClient>().GetItem(guid);
		}

		return _item;
	}

	private void Empty(string text)
	{
		EmptyText.Text = text;
		EmptyText.Visibility = Visibility.Visible;
	}

	private async Task FillEpisodes(BaseItemDto? item)
	{
		EpisodesList.Children.Clear();
		if (item is not { Type: BaseItemDto_Type.Episode, SeasonId: { } season })
		{
			Empty("Not an episode of a series.");
			return;
		}

		var response = await App.GetService<IJellyfinClient>().GetItems(new BaseItemDto { Id = season, Type = BaseItemDto_Type.Season, SeriesId = item.SeriesId });
		if (_open != Part.Episodes)
		{
			return;
		}

		var episodes = response?.Items ?? [];
		if (episodes.Count == 0)
		{
			Empty("Couldn't load this season's episodes.");
			return;
		}

		for (var i = 0; i < episodes.Count; i++)
		{
			var episode = episodes[i];
			var playing = episode.Id == item.Id;
			var picture = new Image { Width = 192, Height = 108, Stretch = Stretch.UniformToFill, Source = CinematicArt.EpisodeStill(episode, 384) };
			var art = new Grid { Width = 192, Height = 108, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2A, 0x0F, 0x19)) };
			art.Children.Add(picture);
			var words = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
			words.Children.Add(new TextBlock { Text = CinematicLabels.EpisodeTitle(episode), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 192 });
			words.Children.Add(new TextBlock { Text = playing ? "Playing" : CinematicLabels.EpisodeMeta(episode), FontSize = 13, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 192 });
			var content = new StackPanel();
			content.Children.Add(art);
			content.Children.Add(words);
			var button = new Button { Content = content, Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(playing ? 2 : 0), CornerRadius = new CornerRadius(8) };
			if (playing && Application.Current.Resources.TryGetValue("NixieAccentBrush", out var accent) && accent is Brush brush)
			{
				button.BorderBrush = brush;
			}

			var name = ItemLabels.EpisodeAction("Play", episode);
			AutomationProperties.SetName(button, playing ? $"{ItemLabels.EpisodeRowName(episode)}, playing" : name);
			AutomationProperties.SetPositionInSet(button, i + 1);
			AutomationProperties.SetSizeOfSet(button, episodes.Count);
			ToolTipService.SetToolTip(button, playing ? "Playing" : name);
			button.Click += (_, _) =>
			{
				if (!playing)
				{
					Close(returnFocus: false);
					_ = App.Commands.PlayDto(episode);
				}
			};
			EpisodesList.Children.Add(button);
		}
	}

	private void FillChapters(BaseItemDto? item)
	{
		ChaptersList.Children.Clear();
		var chapters = item?.Chapters?.Where(c => c.StartPositionTicks is not null).OrderBy(c => c.StartPositionTicks).ToList() ?? [];
		if (chapters.Count == 0)
		{
			Empty("This has no chapters.");
			return;
		}

		var position = _controls?.PositionTicks ?? 0;
		var current = chapters.FindLastIndex(c => c.StartPositionTicks <= position);
		for (var i = 0; i < chapters.Count; i++)
		{
			var chapter = chapters[i];
			var ticks = chapter.StartPositionTicks ?? 0;
			var time = MpvControlsViewModel.FormatTime(TimeSpan.FromTicks(ticks));
			var name = string.IsNullOrWhiteSpace(chapter.Name) ? $"Chapter {i + 1}" : chapter.Name.Trim();
			var row = Option($"{time}   {name}", i == current, $"Go to {name}, {time}", () =>
			{
				_ = _controls?.SeekTo(ticks);
				Close(returnFocus: true);
			});
			AutomationProperties.SetPositionInSet(row, i + 1);
			AutomationProperties.SetSizeOfSet(row, chapters.Count);
			ChaptersList.Children.Add(row);
		}
	}

	private void FillTracks()
	{
		AudioList.Children.Clear();
		SubtitleList.Children.Clear();
		SizeList.Children.Clear();
		if (_controls is not { } controls)
		{
			Empty("The player isn't ready.");
			return;
		}

		foreach (var choice in controls.AudioChoices)
		{
			AudioList.Children.Add(Option(choice.Label, choice.IsSelected, $"Audio: {choice.Label}", () => _ = controls.SetAudio(choice.Index)));
		}

		if (controls.AudioChoices.Count == 0)
		{
			AudioList.Children.Add(new TextBlock { Text = "One track", Opacity = 0.8, FontSize = 14 });
		}

		foreach (var choice in controls.SubtitleChoices)
		{
			SubtitleList.Children.Add(Option(choice.Label, choice.IsSelected, $"Subtitles: {choice.Label}", () => _ = controls.SetSubtitle(choice.Index)));
		}

		if (controls.SubtitleChoices.Count == 0)
		{
			SubtitleList.Children.Add(new TextBlock { Text = "None", Opacity = 0.8, FontSize = 14 });
		}

		SubtitleTools.Visibility = controls.HasSubtitleChoice ? Visibility.Visible : Visibility.Collapsed;
		foreach (var (label, percent) in Sizes)
		{
			var on = controls.SubtitleSize == percent;
			var button = new Button { Content = label };
			if (Application.Current.Resources.TryGetValue("NixieCompactButtonStyle", out var style) && style is Style s)
			{
				button.Style = s;
			}

			if (on && Application.Current.Resources.TryGetValue("NixieToggleOnBorderBrush", out var rim) && rim is Brush brush)
			{
				button.BorderBrush = brush;
				button.BorderThickness = new Thickness(1.5);
			}

			AutomationProperties.SetName(button, on ? $"Subtitle size {label}, chosen" : $"Subtitle size {label}");
			button.Click += (_, _) => _ = controls.SetSubtitleSize(percent);
			SizeList.Children.Add(button);
		}

		DelayText.Text = controls.SubtitleDelayText;
		AutomationProperties.SetName(DelayText, $"Subtitle timing {controls.SubtitleDelayText}");
	}

	/// <summary>One choice of a list: its words, a check when it is the one in use, the accent fill behind it then.</summary>
	private static Button Option(string label, bool selected, string name, Action pick)
	{
		var row = new Grid { ColumnSpacing = 10 };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		var check = new FontIcon { Glyph = "", FontSize = 12, Visibility = selected ? Visibility.Visible : Visibility.Collapsed };
		if (Application.Current.Resources.TryGetValue("NixieToggleOnForegroundBrush", out var on) && on is Brush brush)
		{
			check.Foreground = brush;
		}

		row.Children.Add(check);
		var text = new TextBlock { Text = label, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
		Grid.SetColumn(text, 1);
		row.Children.Add(text);
		var button = new Button
		{
			Content = row,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			MinHeight = 40,
			Padding = new Thickness(10, 6, 10, 6),
			BorderThickness = new Thickness(0),
			CornerRadius = new CornerRadius(6),
			Background = selected && Application.Current.Resources.TryGetValue("GlassSelectedOverlayBrush", out var fill) && fill is Brush b ? b : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
		};
		AutomationProperties.SetName(button, selected ? $"{name}, in use" : name);
		button.Click += (_, _) => pick();
		return button;
	}

	private void EarlierButton_Click(object sender, RoutedEventArgs e) => _ = _controls?.StepSubtitleDelay(-1, announce: true);

	private void LaterButton_Click(object sender, RoutedEventArgs e) => _ = _controls?.StepSubtitleDelay(1, announce: true);
}

/// <summary>
/// The theme's PlayerTopBarStyle sets <c>cc:CinematicPlayer.TopTabs</c> on the shared overlay's top bar: the bar then holds a
/// <see cref="CinematicPlayerPanel"/>; another theme's style (which does not set it) takes it out again.
/// </summary>
public static class CinematicPlayer
{
	public static readonly DependencyProperty TopTabsProperty =
		DependencyProperty.RegisterAttached("TopTabs", typeof(bool), typeof(CinematicPlayer), new PropertyMetadata(false, OnTopTabsChanged));

	public static bool GetTopTabs(DependencyObject element) => (bool)element.GetValue(TopTabsProperty);

	public static void SetTopTabs(DependencyObject element, bool value) => element.SetValue(TopTabsProperty, value);

	private static void OnTopTabsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not Panel bar)
		{
			return;
		}

		var existing = bar.Children.OfType<CinematicPlayerPanel>().FirstOrDefault();
		// the bar's own row (Back and the title) stays at the top while the panel drops under the tabs
		var own = bar.Children.OfType<FrameworkElement>().FirstOrDefault(c => c is not CinematicPlayerPanel);
		if (e.NewValue is true && existing is null)
		{
			var panel = new CinematicPlayerPanel();
			if (bar is Grid)
			{
				Grid.SetColumnSpan(panel, 99);
				Grid.SetRowSpan(panel, 99);
			}

			bar.Children.Add(panel);
			own?.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
		}
		else if (e.NewValue is not true && existing is not null)
		{
			bar.Children.Remove(existing);
			own?.ClearValue(FrameworkElement.VerticalAlignmentProperty);
		}
	}
}
