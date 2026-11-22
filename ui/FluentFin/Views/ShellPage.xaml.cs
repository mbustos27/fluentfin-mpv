using FluentFin.Contracts.Services;
using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

public sealed partial class ShellPage : Page
{
	public ShellViewModel ViewModel
	{
		get;
	}

	// fluentfin-mpv: no navigation pane while a player page is open (docs/research/ux-review.md V9)
	public PlayerChrome Chrome { get; } = App.GetService<PlayerChrome>();

	// Seaglass: the opt-in update notice (Core/Updates, Services/UpdateNoticeService; Nixie 1.5: Controls/UpdateNotice)
	private readonly UpdateNoticeService _updates = App.GetService<UpdateNoticeService>();

	public ShellPage()
	{
		ViewModel = App.GetService<ShellViewModel>();
		InitializeComponent();

		ViewModel.NavigationService.Frame = NavigationFrame;

		// 1.2.0 (the top bar): the sections are tabs (TopNavigation, shown by TitleBarControl's TopTabStrip), not a rail: the
		// plugins add theirs (Diary) there, the pins are read back, the libraries asked for
		_top.Reset();
		App.GetService<FluentFin.UI.Core.IPluginManager>().AddNavigationItems(_top);
		_ = LoadLibrariesAsync();
		// Nixie 1.4.0 (the trail rule): an item page reached from another section names its library (TrailEyebrow), from the
		// item's folders on the server (asked once per item)
		_top.LibraryOf = LibraryOfAsync;
		// 1.7 upnav: an item page's series and season crumbs, from the item opened; one that does not carry its ids gets them from
		// the item the page's view model fetches, not a second request (TopNavigation.Trail, TrailItems)
		_top.ItemOf = FluentFin.Core.ViewModels.TrailItems.FromShownPage(() => FluentFin.Helpers.FrameExtensions.GetPageViewModel(NavigationFrame), _client.GetItem);
		_top.SeasonsOf = async (series, cancel) => (await _client.GetItems(new Jellyfin.Sdk.Generated.Models.BaseItemDto { Id = series, Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Series }).WaitAsync(cancel))?.Items;
		// 1.6 topbar: the Collections destination opens through GlobalCommands.OpenCollections (1.6 integration: the Collections page)
		_top.OpenCollections = App.Commands.OpenCollections;
		// Nixie 1.4.0 (the trail rule): the lit destination chosen again on its own page goes back to its top
		_top.ReturnToTopRequested += OnReturnToTop;
		Unloaded += (_, _) => _top.ReturnToTopRequested -= OnReturnToTop;
		_titleBar.PropertyChanged += OnTitleBarChanged;
		Unloaded += (_, _) => _titleBar.PropertyChanged -= OnTitleBarChanged;

		// review: the library tabs follow the server: read again whenever the client drops its list (a library added, removed
		// or renamed, here or on the server: LibraryChanged); a fifth library moves them into the Libraries menu
		_client.UserViewsChanged += OnUserViewsChanged;
		Unloaded += (_, _) => _client.UserViewsChanged -= OnUserViewsChanged;
		// 1.6.1 (the 1.6 review): a star pressed while the bar has no libraries (their read failed) asks for them again
		_top.LibrariesWanted += OnLibrariesWanted;
		Unloaded += (_, _) =>
		{
			_top.LibrariesWanted -= OnLibrariesWanted;
			_gone = true;
			_librariesRetry?.Stop();
		};

		// Seaglass 1.1.4 (UX review, prioritised fix 5): the title bar's Back and Account follow the page this frame shows, by
		// its type, as soon as the frame shows it (the navigation service's own Navigated waits for the page's view model:
		// the player's waits for the playback to start). The shell going (Log out, Switch user) shows them again.
		NavigationFrame.Navigated += OnFrameNavigated;
		Unloaded += (_, _) => _titleBar.OnPageShown(null);

		// Seaglass 1.1.4 (interaction audit, item 16): a page a tab opens is a switch of section (PageTransition's refresh);
		// anything else opened is a drill in, Back a drill out
		PageTransition.IsTopLevel = IsTabPage;
	}

	// 1.2.0: the top bar's tabs
	private readonly TopNavigation _top = App.GetService<TopNavigation>();

	private readonly FluentFin.Core.Contracts.Services.IJellyfinClient _client = App.GetService<FluentFin.Core.Contracts.Services.IJellyfinClient>();

	private void OnUserViewsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => _ = LoadLibrariesAsync());

	private void OnLibrariesWanted(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => _ = LoadLibrariesAsync());

	/// <summary>1.6.1: when the bar reads the libraries again after a failed read (seconds), then no more.</summary>
	internal static readonly int[] LibrariesRetrySeconds = [3, 10, 30, 90];

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _librariesRetry;
	private int _librariesTries;

	/// <summary>Review of 1.6.1 (F3): the shell went (signed out, switched): no more reads of the libraries.</summary>
	private bool _gone;

	/// <summary>
	/// Review of 1.6.1 (F3): when the bar reads the libraries again after a failed read (null: no more). Not tied to the page
	/// being loaded: the first read starts in the constructor, and one that failed before Loaded scheduled none.
	/// </summary>
	internal static TimeSpan? NextLibrariesRetry(int tries, bool gone) =>
		gone || tries < 0 || tries >= LibrariesRetrySeconds.Length ? null : TimeSpan.FromSeconds(LibrariesRetrySeconds[tries]);

	private async Task LoadLibrariesAsync()
	{
		try
		{
			// 1.6.1 (the 1.6 review): a failed read (null) left the bar empty for the session, and the stars said nothing: it is
			// read again a few times, and a good read anywhere else fills the bar too (UserViewsChanged)
			var libraries = await _client.GetUserViewsOrNull();
			_top.SetLibraries(libraries ?? []);
			if (libraries is not null)
			{
				_librariesTries = 0;
				_librariesRetry?.Stop();
				_ = ReadBoxSetsSoonAsync();
				return;
			}

			if (NextLibrariesRetry(_librariesTries, _gone) is { } wait)
			{
				_librariesTries++;
				_librariesRetry ??= DispatcherQueue.CreateTimer();
				_librariesRetry.IsRepeating = false;
				_librariesRetry.Interval = wait;
				_librariesRetry.Tick -= OnLibrariesRetry;
				_librariesRetry.Tick += OnLibrariesRetry;
				_librariesRetry.Start();
			}
		}
		catch (Exception)
		{
			// no list: one "Libraries" tab, whose page says why (its own load state)
		}
	}

	private void OnLibrariesRetry(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
	{
		if (!_gone)
		{
			_ = LoadLibrariesAsync();
		}
	}

	/// <summary>Review of 1.6.1 (F2): how long after the libraries the shell reads the box sets, when nothing else has.</summary>
	internal static readonly TimeSpan BoxSetsAfter = TimeSpan.FromSeconds(8);

	private bool _boxSetsAsked;

	/// <summary>
	/// Review of 1.6.1 (F2): until this user's box sets are read, a collection name that would be tidied keeps the server's
	/// (CollectionNames.Display). Home's Made by Nixie rows and the Collections page read them; with neither (the rows turned
	/// off, a film opened at once), the shell reads them once, a few seconds after sign-in.
	/// </summary>
	private async Task ReadBoxSetsSoonAsync()
	{
		if (_boxSetsAsked)
		{
			return;
		}

		_boxSetsAsked = true;
		try
		{
			await Task.Delay(BoxSetsAfter);
			if (CollectionNames.Tidy && !CollectionNames.BoxSetsKnown)
			{
				await _client.GetBoxSets();
			}
		}
		catch (Exception)
		{
			// the names stay the server's until a page reads the list
		}
	}

	/// <summary>The view model's key of a page type shown in this frame ("" for one the page service does not know).</summary>
	private static string KeyOf(Type? pageType)
	{
		if (pageType is null)
		{
			return "";
		}

		try
		{
			return App.GetService<FluentFin.UI.Core.Contracts.Services.IPageService>().GetViewModelType(pageType).FullName ?? "";
		}
		catch (ArgumentException)
		{
			return "";
		}
	}

	/// <summary>1.2.0: a page one of the tabs (or the Libraries tab's menu) opens.</summary>
	private bool IsTabPage(Type pageType)
	{
		var key = KeyOf(pageType);
		// 1.6 topbar: the account menu's sections (Diary) are sections too
		return key.Length > 0 && (_top.AllTabs.SelectMany(t => t.Menu.Prepend(t)).Any(t => t.Key == key) || _top.Sections.Any(t => t.Key == key));
	}

	private readonly Dictionary<Guid, Guid?> _libraryOf = [];

	/// <summary>Nixie 1.4.0: the library an item is in: the first of its folders that is one of the user's views.</summary>
	private async Task<Guid?> LibraryOfAsync(Jellyfin.Sdk.Generated.Models.BaseItemDto item)
	{
		if (item.Id is not { } id)
		{
			return null;
		}

		if (_libraryOf.TryGetValue(id, out var known))
		{
			return known;
		}

		var libraries = TopTabs.AllLibraryTabs(_top.AllTabs).Select(t => t.LibraryId).ToHashSet(); // 1.6 topbar: every library, not only the bar's
		var library = (await _client.GetAncestors(id)).FirstOrDefault(a => libraries.Contains(a.Id))?.Id;
		if (library is not null)
		{
			_libraryOf[id] = library;
		}

		return library;
	}

	/// <summary>
	/// Nixie 1.4.0 (the trail rule, the review's DI-p7 and C-m13: choosing the library already on screen did nothing): the page
	/// shown again, fresh, at its top (a refresh's reload in place: the history and the bar as they were).
	/// </summary>
	private void OnReturnToTop(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
	{
		if (ViewModel.NavigationService is NavigationService navigation)
		{
			// Nixie 1.4.0 (integration): back to its top, not to the place R keeps
			navigation.ReloadForRefresh(_ => true, keepPlace: false);
		}
	});

	/// <summary>
	/// 1.2.0: where the page starts: below the bar, or at the window's top for a page that extends under it (a hero) and while
	/// the bar is hidden (full screen); the bar shows its scrim over such a page.
	/// </summary>
	private void UpdateInsets()
	{
		var extendsUnder = NavigationFrame.Content is DependencyObject page && TopBar.GetExtendsUnder(page);
		var top = TopBar.TopInset(_titleBar.IsVisible, extendsUnder);
		ContentRoot.Padding = new Thickness(0, top, 0, 0);
		UpdateBar.Margin = UpdateBarTop(UpdateBarOpen, _titleBar.IsVisible, extendsUnder) is { } barTop ? new Thickness(16, barTop, 16, 0) : new Thickness(0);
		_titleBar.IsContentUnderBar = _titleBar.IsVisible && extendsUnder;
	}

	/// <summary>
	/// The update bar's top margin: 8 below the top bar, or below where the bar sits over a page that runs under it; none while
	/// it is closed. 1.3.0: a closed bar kept its margin, so every page meant to run under the top bar (the heroes) started
	/// that much lower and never reached under the bar.
	/// </summary>
	internal static double? UpdateBarTop(bool open, bool barVisible, bool extendsUnder) =>
		open ? 8 + (barVisible && extendsUnder ? TopBar.Height : 0) : null;

	private void OnTitleBarChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(TitleBarViewModel.IsVisible))
		{
			DispatcherQueue.TryEnqueue(UpdateInsets);
		}
	}

	private bool _lookChecked;

	// Seaglass 1.1.4: the title bar (TitleBarViewModel.OnPageShown)
	private readonly TitleBarViewModel _titleBar = (TitleBarViewModel)App.GetService<ITitleBarViewModel>();

	private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		// 1.5 updater: Settings' Updates group says what the notice says, with the same action: the notice steps aside there
		UpdateBar.HiddenHere = e.SourcePageType == typeof(JellyfinSettings.SettingsPage);

		// Nixie 1.3.0: a theme switch showing the same page in the new theme's view: the bar keeps its search text and its
		// tab; only where the page starts can change (the new view may run under the bar)
		// Nixie 1.3.1: a refresh (R, F5) too: the same page loaded again
		if (ViewModel.NavigationService is NavigationService { LastNavigationWasThemeReload: true } or NavigationService { LastNavigationWasRefresh: true })
		{
			UpdateInsets();
			return;
		}

		_titleBar.OnPageShown(e.SourcePageType, e.Parameter);

		// Nixie 1.3.0: the one-time "Choose a look" step, once the first page after the sign-in is up
		if (!_lookChecked)
		{
			_lookChecked = true;
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () => await ChooseLookStep.OfferAsync(XamlRoot));
			// 1.5 updater: after "Restart to update", back to the page Nixie was on (once)
			if (_updates.TakeResume() is { } resume)
			{
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () => await UpdateActions.ResumeAsync(resume));
			}
		}

		// 1.2.0: the tab this page marks, and where it starts
		// review: with the page's place in the history, so Back gives a page back the tab it had
		_top.OnPageShown(KeyOf(e.SourcePageType), e.Parameter, NavigationFrame.BackStackDepth,
			returning: e.NavigationMode is Microsoft.UI.Xaml.Navigation.NavigationMode.Back or Microsoft.UI.Xaml.Navigation.NavigationMode.Forward);
		UpdateInsets();
	}

	// Seaglass: Alt+Left, the Back and Forward keys and Mouse 4 / 5 are the window's (WindowShortcuts), on every page
	// Seaglass 1.1.4: a shell shown again (back in the window) tells the title bar what its frame shows
	private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		_titleBar.OnPageShown(NavigationFrame.Content?.GetType());
		UpdateInsets();
	}

	/// <summary>Whether the update notice shows (1.3.0: its room comes and goes with it, UpdateBarTop).</summary>
	private bool UpdateBarOpen => UpdateBar.IsOpen;

	// Nixie 1.5: the notice (Controls\UpdateNotice) came or went: the page below it moves
	private void OnUpdateBarOpenChanged(object? sender, EventArgs e) => UpdateInsets();
}
