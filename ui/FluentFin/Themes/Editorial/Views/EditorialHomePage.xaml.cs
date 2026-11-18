using FluentFin.Controls;
using FluentFin.Converters;
using FluentFin.Core;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.Themes.Editorial.Controls;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Editorial.Views;

/// <summary>The Editorial theme's Home (see EditorialHomePage.xaml), over the shared <see cref="HomeViewModel"/>.</summary>
public sealed partial class EditorialHomePage : Page
{
	public HomeViewModel ViewModel { get; } = App.GetService<HomeViewModel>();

	public static readonly DependencyProperty IsNarrowProperty =
		DependencyProperty.Register(nameof(IsNarrow), typeof(bool), typeof(EditorialHomePage), new PropertyMetadata(false));

	/// <summary>Under 1400 px: the narrow layout (the concept's 1100 px Home).</summary>
	public bool IsNarrow
	{
		get => (bool)GetValue(IsNarrowProperty);
		set => SetValue(IsNarrowProperty, value);
	}

	private BaseItemViewModel? _hero;
	private bool _heroIsResume;
	private IReadOnlyList<EditorialUpNextEntry> _upNext = [];
	private bool _genresAsked;

	private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _noticeTimer;
	private bool _noticePointer;
	private bool _noticeFocus;
	private long _noticeShownAt;

	public EditorialHomePage()
	{
		InitializeComponent();
		ShowNotice(ViewModel.Notice);
		_noticeTimer = DispatcherQueue.CreateTimer();
		_noticeTimer.IsRepeating = false;
		_noticeTimer.Tick += (_, _) =>
		{
			if (ViewModel.Notice is not { } notice)
			{
				return;
			}

			var held = _noticePointer || _noticeFocus;
			if (NoticeTiming.Closes(NoticeShownFor, held))
			{
				ViewModel.DismissNotice(notice);
			}
			else
			{
				StartNoticeTimer(held, afterHold: false);
			}
		};

		ViewModel.PropertyChanged += OnViewModelChanged;
		ViewModel.ContinueItems.CollectionChanged += OnSectionChanged;
		ViewModel.NextUpItems.CollectionChanged += OnSectionChanged;
		ViewModel.EditorialGenres.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(EditorialGenreStrip.Genres) or nameof(EditorialGenreStrip.Selected))
			{
				UpdateGenres();
			}
		};
		UpNextRepeater.ElementPrepared += (_, e) =>
		{
			AutomationProperties.SetPositionInSet(e.Element, e.Index + 1);
			AutomationProperties.SetSizeOfSet(e.Element, UpNextRepeater.ItemsSourceView?.Count ?? 0);
		};
		HeroDetails.CloseRequested += (_, _) => CloseHeroDetails(returnFocus: true);
		HeroDetails.UpRequested += (_, _) => HeroDetailsButton.Focus(FocusState.Keyboard);
		SizeChanged += (_, e) => Layout(e.NewSize.Width);
		Loaded += (_, _) =>
		{
			AmbientBackdrop.Set(null);
			Layout(ActualWidth);
			Refresh();
			RestorePlace();
		};
		Unloaded += (_, _) => _noticeTimer.Stop();
	}

	private TimeSpan NoticeShownFor => System.Diagnostics.Stopwatch.GetElapsedTime(_noticeShownAt);

	// ---- the view model's sections -------------------------------------------------------------------------------------

	private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(HomeViewModel.ContinueItems):
				ViewModel.ContinueItems.CollectionChanged -= OnSectionChanged;
				ViewModel.ContinueItems.CollectionChanged += OnSectionChanged;
				Refresh();
				break;
			case nameof(HomeViewModel.NextUpItems):
				ViewModel.NextUpItems.CollectionChanged -= OnSectionChanged;
				ViewModel.NextUpItems.CollectionChanged += OnSectionChanged;
				Refresh();
				break;
			case nameof(HomeViewModel.Hero):
			case nameof(HomeViewModel.IsContinueLoading):
			case nameof(HomeViewModel.IsNextUpLoading):
				Refresh();
				break;
			case nameof(HomeViewModel.IsLoading) when !ViewModel.IsLoading:
				Refresh();
				LoadGenres();
				break;
			case nameof(HomeViewModel.Notice):
				OnNoticeChanged();
				break;
		}
	}

	private void OnSectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Refresh();

	/// <summary>The hero tile and Up next, from Continue Watching, the shared hero's slides and Next Up.</summary>
	private void Refresh()
	{
		if (!IsLoaded)
		{
			return;
		}

		var hero = EditorialHome.HeroItem([.. ViewModel.ContinueItems], ViewModel.Hero, [.. ViewModel.NextUpItems]);
		_heroIsResume = hero is not null && ViewModel.ContinueItems.Contains(hero);
		if (!ReferenceEquals(hero?.Dto, _hero?.Dto) || hero?.Dto.Id != _hero?.Dto.Id)
		{
			_hero = hero;
			ShowHero();
		}

		_upNext = EditorialHome.UpNext(ViewModel.ContinueItems, ViewModel.NextUpItems, _heroIsResume ? hero : null);
		var shown = _upNext.Take(IsNarrow ? 3 : EditorialHome.UpNextLines).ToList();
		UpNextRepeater.ItemsSource = shown;
		UpNextCount.Text = $"Continue watching · {_upNext.Count + (_heroIsResume ? 1 : 0)}";
		UpNextPanel.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		UpNextSeeAll.Visibility = _upNext.Count > shown.Count ? Visibility.Visible : Visibility.Collapsed;
		Layout(ActualWidth);
		if (!ViewModel.IsLoading)
		{
			LoadGenres();
		}
	}

	private void ShowHero()
	{
		var dto = _hero?.Dto;
		HeroTile.Visibility = dto is null ? Visibility.Collapsed : Visibility.Visible;
		if (dto is null)
		{
			CloseHeroDetails(returnFocus: false);
			return;
		}

		var nextUp = !_heroIsResume && ViewModel.NextUpItems.Any(x => x.Dto.Id == dto.Id);
		TonightText.Text = _heroIsResume ? "TONIGHT" : nextUp ? "UP NEXT" : "NEW";
		HeroKicker.Text = EditorialHome.Kicker(dto, _heroIsResume, nextUp).ToUpperInvariant();
		HeroTitle.Text = EditorialCards.Title(dto);
		HeroMetaRun.Text = dto.Type == BaseItemDto_Type.Episode
			? string.Join(" · ", new[] { ItemLabels.EpisodeTitle(dto), EditorialHome.HeroMeta(new BaseItemDto { OfficialRating = dto.OfficialRating, RunTimeTicks = dto.RunTimeTicks }) }.Where(x => x.Length > 0))
			: EditorialHome.HeroMeta(dto);
		var left = _heroIsResume ? EditorialHome.TimeLeft(dto) : "";
		HeroLeftDot.Text = left.Length > 0 && HeroMetaRun.Text.Length > 0 ? "·" : "";
		HeroLeftDot.Visibility = HeroLeftDot.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		HeroLeftRun.Text = left;
		HeroOverview.Text = dto.Overview?.Trim() ?? "";
		HeroOverview.Visibility = HeroOverview.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		HeroImage.Source = ItemArtworkConverters.Backdrop(dto, 1280);
		var resume = ItemLabels.ResumePosition(dto) is not null && dto.Type is not (BaseItemDto_Type.Series or BaseItemDto_Type.Season);
		HeroPlayText.Text = resume ? "Resume" : "Play";
		AutomationProperties.SetName(HeroPlayButton, ItemLabels.PlayAction(dto));
		ToolTipService.SetToolTip(HeroPlayButton, ItemLabels.PlayAction(dto));
		AutomationProperties.SetName(HeroDetailsButton, $"Details of {ItemLabels.ActionTarget(dto)}");
		HeroFavoriteButton.IsChecked = dto.UserData?.IsFavorite == true;
		UpdateHeroFavorite();
		if (HeroDetails.Visibility == Visibility.Visible)
		{
			HeroDetails.Item = _hero;
		}
	}

	private void UpdateHeroFavorite()
	{
		var name = ItemLabels.FavoriteAction(_hero?.Dto, HeroFavoriteButton.IsChecked);
		AutomationProperties.SetName(HeroFavoriteButton, name);
		ToolTipService.SetToolTip(HeroFavoriteButton, name);
		HeroFavoriteGlyph.Glyph = ItemRowConverters.FavoriteGlyph(HeroFavoriteButton.IsChecked);
	}

	private void HeroPlayButton_Click(object sender, RoutedEventArgs e)
	{
		if (_hero?.Dto is { } dto)
		{
			App.Commands.PlayDtoCommand.Execute(dto);
		}
	}

	private async void HeroFavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		UpdateHeroFavorite();
		if (_hero?.Dto is { } dto)
		{
			await App.Commands.SetFavorite(dto, HeroFavoriteButton.IsChecked == true);
		}
	}

	private void HeroDetailsButton_Click(object sender, RoutedEventArgs e)
	{
		if (HeroDetails.Visibility == Visibility.Visible)
		{
			CloseHeroDetails(returnFocus: false);
			return;
		}

		HeroDetails.Item = _hero;
		HeroDetails.Visibility = Visibility.Visible;
		HeroDetailsChevron.Glyph = "";
		AutomationProperties.SetName(HeroDetailsButton, $"Hide the details of {ItemLabels.ActionTarget(_hero?.Dto)}");
		HeroDetails.UpdateLayout();
		HeroDetails.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = SafeMotion.Allowed, VerticalAlignmentRatio = 0.85 });
	}

	private void CloseHeroDetails(bool returnFocus)
	{
		HeroDetails.Visibility = Visibility.Collapsed;
		HeroDetails.Item = null;
		HeroDetailsChevron.Glyph = "";
		AutomationProperties.SetName(HeroDetailsButton, $"Details of {ItemLabels.ActionTarget(_hero?.Dto)}");
		if (returnFocus)
		{
			HeroDetailsButton.Focus(FocusState.Keyboard);
		}
	}

	// ---- the genre strip -----------------------------------------------------------------------------------------------

	private async void LoadGenres()
	{
		if (_genresAsked || ViewModel.RecentItems.Count == 0)
		{
			return;
		}

		_genresAsked = true;
		try
		{
			await ViewModel.EditorialLoadGenres();
		}
		catch (Exception)
		{
			// no strip: the shelves below are still there
		}
	}

	private readonly List<EditorialTabButton> _genreButtons = [];

	/// <summary>The strip's tabs: as many genres as fit, most titles first, the picked one always among them; every genre in "All genres".</summary>
	private void UpdateGenres()
	{
		var genres = ViewModel.EditorialGenres.Genres;
		GenreBar.Visibility = genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		if (_genreButtons.Count != genres.Count || _genreButtons.Zip(genres).Any(x => (string)x.First.Tag != x.Second.Name))
		{
			GenreStack.Children.Clear();
			_genreButtons.Clear();
			AllGenresMenu.Items.Clear();
			foreach (var genre in genres)
			{
				var button = new EditorialTabButton
				{
					Content = genre.Name,
					Tag = genre.Name,
					Style = (Style)Application.Current.Resources["EditorialGenreTabStyle"],
				};
				AutomationProperties.SetName(button, genre.AutomationName);
				button.Click += (_, _) => PickGenre(genre.Name);
				_genreButtons.Add(button);
				GenreStack.Children.Add(button);

				var item = new RadioMenuFlyoutItem { Text = $"{genre.Name}  ({genre.Count})", GroupName = "EditorialGenres", Tag = genre.Name };
				AutomationProperties.SetName(item, genre.AutomationName);
				item.Click += (_, _) => PickGenre(genre.Name);
				AllGenresMenu.Items.Add(item);
			}
		}

		foreach (var button in _genreButtons)
		{
			button.IsSelected = (string)button.Tag == ViewModel.EditorialGenres.Selected;
		}

		foreach (var item in AllGenresMenu.Items.OfType<RadioMenuFlyoutItem>())
		{
			item.IsChecked = (string)item.Tag == ViewModel.EditorialGenres.Selected;
		}

		FitGenres();
	}

	private void AllGenresButton_Click(object sender, RoutedEventArgs e) => FlyoutBase.ShowAttachedFlyout(AllGenresButton);

	private void PickGenre(string genre)
	{
		ViewModel.EditorialPickGenre(genre);
		FitGenres();
	}

	/// <summary>Shows the tabs that fit the strip's width (the picked one replaces the last that fits when it would not).</summary>
	private void FitGenres()
	{
		var width = GenreTabs.ActualWidth;
		if (width <= 0 || _genreButtons.Count == 0)
		{
			return;
		}

		var spacing = GenreStack.Spacing;
		var widths = _genreButtons.Select(b =>
		{
			b.Visibility = Visibility.Visible;
			b.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
			return b.DesiredSize.Width;
		}).ToList();
		var selected = _genreButtons.FindIndex(b => b.IsSelected);
		var shown = EditorialGenreFit.Fit(widths, spacing, width, selected);
		for (var i = 0; i < _genreButtons.Count; i++)
		{
			_genreButtons[i].Visibility = shown.Contains(i) ? Visibility.Visible : Visibility.Collapsed;
		}

		var count = shown.Count;
		var at = 0;
		foreach (var i in shown)
		{
			AutomationProperties.SetPositionInSet(_genreButtons[i], ++at);
			AutomationProperties.SetSizeOfSet(_genreButtons[i], count);
		}
	}

	// ---- layout: 1920 and 1100 -----------------------------------------------------------------------------------------

	private void Layout(double width)
	{
		if (width <= 0)
		{
			return;
		}

		var narrow = width < 1400;
		var margin = narrow ? 40 : 56;
		Body.Padding = new Thickness(margin, narrow ? 20 : 24, margin, 56);
		if (narrow != IsNarrow || UpNextRepeater.ItemTemplate is null)
		{
			IsNarrow = narrow;
			UpNextRepeater.ItemTemplate = (DataTemplate)Resources[narrow ? "NarrowUpNextTemplate" : "WideUpNextTemplate"];
			UpNextRepeater.Layout = narrow
				? new UniformGridLayout { MaximumRowsOrColumns = 3, ItemsStretch = UniformGridLayoutItemsStretch.Fill, MinColumnSpacing = 8, MinItemWidth = 200, MinItemHeight = 62 }
				: new StackLayout();
			Grid.SetColumn(UpNextPanel, narrow ? 0 : 1);
			Grid.SetRow(UpNextPanel, narrow ? 1 : 0);
			UpNextHeader.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
			HeroTile.Height = narrow ? 300 : 330;
			HeroTitle.FontSize = narrow ? 44 : 58;
			HeroTitle.LineHeight = narrow ? 48 : 60;
			TonightText.FontSize = narrow ? 26 : 30;
			HeroWords.Margin = narrow ? new Thickness(28, 24, 28, 24) : new Thickness(40, 30, 40, 28);
			HeroButtons.Margin = new Thickness(0, narrow ? 14 : 20, 0, 0);
			GenreStack.Spacing = narrow ? 22 : 30;
			foreach (var button in _genreButtons)
			{
				button.FontSize = narrow ? 21 : 25;
			}

			if (UpNextRepeater.ItemsSource is not null)
			{
				Refresh();
				return;
			}
		}

		// Up next beside the tile: about a third of the width (670 at 1920)
		UpNextColumn.Width = narrow ? new GridLength(0) : new GridLength(Math.Clamp((width - 2 * margin) * 0.37, 520, 700));
		TopGrid.ColumnSpacing = narrow || UpNextPanel.Visibility != Visibility.Visible ? 0 : 28;
		if (!narrow && UpNextPanel.Visibility != Visibility.Visible)
		{
			UpNextColumn.Width = new GridLength(0);
		}

		DispatcherQueue.TryEnqueue(FitGenres);
	}

	// ---- keys ----------------------------------------------------------------------------------------------------------

	/// <summary>N: the keyboard to Up next's first next episode (never playing: a single letter must not start a video).</summary>
	private void NextUpInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (TextInput.IsTextInput(FocusManager.GetFocusedElement(XamlRoot)))
		{
			return;
		}

		args.Handled = FocusUpNext(e => !e.IsResume);
	}

	/// <summary>C: the keyboard to what is in progress: the hero's Resume, else Up next's first.</summary>
	private void ContinueInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (TextInput.IsTextInput(FocusManager.GetFocusedElement(XamlRoot)))
		{
			return;
		}

		args.Handled = _heroIsResume && HeroTile.Visibility == Visibility.Visible
			? HeroPlayButton.Focus(FocusState.Keyboard)
			: FocusUpNext(e => e.IsResume);
	}

	private bool FocusUpNext(Func<EditorialUpNextEntry, bool> which)
	{
		if (UpNextRepeater.ItemsSource is not IReadOnlyList<EditorialUpNextEntry> shown || shown.Select((e, i) => (e, i)).FirstOrDefault(x => which(x.e)) is not { e: not null } found)
		{
			return false;
		}

		return UpNextRepeater.GetOrCreateElement(found.i) is Control line && line.Focus(FocusState.Keyboard);
	}

	// ---- see all -------------------------------------------------------------------------------------------------------

	private void UpNextSeeAll_Click(object sender, RoutedEventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Up next", [.. ViewModel.ContinueItems, .. ViewModel.NextUpItems]));

	private void RecentSeeAll(object sender, EventArgs e)
	{
		if (sender is EditorialShelf { Tag: { } library })
		{
			App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<LibraryViewModel>(library);
		}
	}

	// ---- Back to Home: where the user was ------------------------------------------------------------------------------

	private HomePlace? _restore;

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_restore = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward ? HomePlace.Recall(ViewModel.JellyfinClient.UserId) : null;
		base.OnNavigatedTo(e);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		var row = ShelfRow.LastUsed is { } used && IsMine(used) ? used : null;
		var item = row?.LastItem as BaseItemViewModel;
		HomePlace.Remember(ViewModel.JellyfinClient.UserId, new HomePlace(PageScroller.VerticalOffset, item is null ? null : row!.Header, item?.Id));
		base.OnNavigatedFrom(e);
	}

	private bool IsMine(DependencyObject element)
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

	private void RestorePlace()
	{
		if (_restore is not { } place)
		{
			return;
		}

		_restore = null;
		var started = System.Diagnostics.Stopwatch.GetTimestamp();
		var timer = DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(50);
		timer.Tick += (t, _) =>
		{
			if (XamlRoot is null)
			{
				t.Stop();
				return;
			}

			if (PageScroller.ScrollableHeight < place.Offset - 1 && ViewModel.IsLoading && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4))
			{
				return;
			}

			t.Stop();
			PageScroller.ChangeView(null, Math.Min(place.Offset, PageScroller.ScrollableHeight), null, disableAnimation: true);
			PageScroller.UpdateLayout();
			if (place.Row is { } header && place.ItemId is { } id)
			{
				ShelfRow.Shown.FirstOrDefault(r => r.Header == header && IsMine(r))?
					.FocusItem(x => x is BaseItemViewModel vm && vm.Id == id, FocusState.Programmatic);
			}
		};
		timer.Start();
	}

	// ---- the notice after a removal, with Undo (the shared Home's) ------------------------------------------------------

	private void OnNoticeChanged()
	{
		_noticeTimer.Stop();
		ShowNotice(ViewModel.Notice);
		if (ViewModel.Notice is null)
		{
			return;
		}

		_noticeShownAt = System.Diagnostics.Stopwatch.GetTimestamp();
		StartNoticeTimer(_noticePointer || _noticeFocus, afterHold: false);
		DispatcherQueue.TryEnqueue(() =>
		{
			if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is null && ViewModel.Notice?.CanUndo == true)
			{
				_ = FocusUpNext(_ => true) || HeroPlayButton.Focus(FocusState.Keyboard);
			}
		});
	}

	private void StartNoticeTimer(bool held, bool afterHold)
	{
		_noticeTimer.Stop();
		_noticeTimer.Interval = NoticeTiming.Remaining(NoticeShownFor, held, afterHold);
		_noticeTimer.Start();
	}

	private void ShowNotice(HomeNotice? notice)
	{
		NoticeText.Text = notice?.Message ?? "";
		UndoButton.Visibility = notice?.CanUndo == true ? Visibility.Visible : Visibility.Collapsed;
		NoticePanel.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
	}

	private void HoldNotice(bool pointer, bool focus)
	{
		_noticePointer = pointer;
		_noticeFocus = focus;
		if (ViewModel.Notice is not null)
		{
			StartNoticeTimer(pointer || focus, afterHold: !(pointer || focus));
		}
	}

	private void Notice_PointerEntered(object sender, PointerRoutedEventArgs e) => HoldNotice(true, _noticeFocus);

	private void Notice_PointerExited(object sender, PointerRoutedEventArgs e) => HoldNotice(false, _noticeFocus);

	private void Notice_GotFocus(object sender, RoutedEventArgs e) => HoldNotice(_noticePointer, true);

	private void Notice_LostFocus(object sender, RoutedEventArgs e) => HoldNotice(_noticePointer, false);

	private async void UndoButton_Click(object sender, RoutedEventArgs e) => await ViewModel.UndoRemoval();

	private void NoticeCloseButton_Click(object sender, RoutedEventArgs e)
	{
		if (ViewModel.Notice is { } notice)
		{
			ViewModel.DismissNotice(notice);
		}
	}

	private async void UndoInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (ViewModel.Notice?.CanUndo != true || TextInput.IsTextInput(FocusManager.GetFocusedElement(XamlRoot)))
		{
			return;
		}

		args.Handled = true;
		await ViewModel.UndoRemoval();
	}

	// ---- x:Bind helpers ------------------------------------------------------------------------------------------------

	public static string Text(string? value) => value ?? "";

	public static Visibility Shown(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

	public static Visibility Hidden(bool loading) => loading ? Visibility.Collapsed : Visibility.Visible;

	public static double ProgressWidth(double percent, double width) => Math.Max(3, width * Math.Clamp(percent, 0, 100) / 100);

	public static string PlayName(BaseItemViewModel item) => ItemLabels.PlayAction(item.Dto);

	/// <summary>The narrow tiles' one line: what is left, or "Next episode".</summary>
	public static string ShortDetail(BaseItemViewModel item, bool resume) =>
		resume ? EditorialHome.TimeLeft(item.Dto) is { Length: > 0 } left ? left : "In progress" : "Next episode";

	public static string RecentCaption(IReadOnlyList<BaseItemViewModel>? items) =>
		items is { Count: > 0 } ? EditorialGenreStrip.CaptionFor([.. items.Select(i => i.Dto)]) : "";

	/// <summary>An Up next line's picture: an episode's still, a film's backdrop (its poster would be cropped to a sliver).</summary>
	public static BitmapImage? ThumbFor(BaseItemViewModel item, int width) =>
		item.Dto.Type == BaseItemDto_Type.Movie && ItemArtworkConverters.Backdrop(item.Dto, width) is { } backdrop ? backdrop : ItemArtworkConverters.Thumb(item.Dto, width);
}
