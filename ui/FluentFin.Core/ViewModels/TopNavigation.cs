using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.ViewModels;

/// <summary>What a top-bar tab is.</summary>
public enum TopTabKind
{
	/// <summary>Home.</summary>
	Home,

	/// <summary>One library (every view is a tab of its own while the bar has room: TopTabs.Fold).</summary>
	Library,

	/// <summary>
	/// "Libraries": the Libraries page (all of them); once the bar is out of room, "Libraries ▾", a menu of All libraries and the
	/// libraries folded into it (TopTabs.Fold).
	/// </summary>
	Libraries,

	/// <summary>A section a plugin adds (Diary, Reporting).</summary>
	Section,
}

/// <summary>
/// Nixie 1.4.0 (the trail rule): a destination of the top bar: a tab's page (its key, and the library for a library's tab),
/// the search box or Settings.
/// </summary>
public readonly record struct TopMark(string Key, Guid? Library = null)
{
	/// <summary>The search box: its results, and an item opened from it.</summary>
	public static TopMark Search { get; } = new(typeof(SearchResultsViewModel).FullName!);

	/// <summary>Settings (the account menu's, Ctrl+,): its own pages after it (the server's administration).</summary>
	public static TopMark Settings { get; } = new(typeof(SettingsViewModel).FullName!);
}

/// <summary>
/// 1.2.0 (the top bar): one tab of the top bar: its text, the page it opens (a view model's key, with a parameter), and for
/// the folded "Libraries ▾" the menu it opens instead.
/// </summary>
public sealed record TopTab(string Title, string Key, object? Parameter = null, TopTabKind Kind = TopTabKind.Section)
{
	/// <summary>
	/// Nixie 1.4.0: the folded "Libraries ▾" tab's menu: All libraries, then the libraries folded into it. Just All libraries
	/// (the tab opens the Libraries page) while every library is a tab. Empty for any other tab.
	/// </summary>
	public IReadOnlyList<TopTab> Menu { get; init; } = [];

	/// <summary>The library this tab opens, if it opens one.</summary>
	public Guid? LibraryId => Parameter switch
	{
		BaseItemDto dto => dto.Id,
		Guid id => id,
		_ => null,
	};

	/// <summary>The folded "Libraries ▾": a menu, not a page (one entry, All libraries, is the page itself).</summary>
	public bool HasMenu => Kind == TopTabKind.Libraries && Menu.Count > 1;

	/// <summary>The destination this tab is (TopNavigation.Lit).</summary>
	public TopMark Mark => new(Key, Kind == TopTabKind.Library ? LibraryId : null);
}

/// <summary>1.2.0: the top bar's tabs, and which one a page marks.</summary>
public static class TopTabs
{
	public static string HomeKey { get; } = typeof(HomeViewModel).FullName!;

	public static string LibraryKey { get; } = typeof(LibraryViewModel).FullName!;

	public static string LibrariesKey { get; } = typeof(LibrariesLandingPageViewModel).FullName!;

	/// <summary>The menu's first entry (and the plain tab's page while nothing is folded).</summary>
	public const string AllLibraries = "All libraries";

	public const string LibrariesTitle = "Libraries";

	/// <summary>
	/// Nixie 1.4.0 (the review's X6: the fold by count, MaxLibraryTabs, is gone, and the pins with it): Home; every view the
	/// server lists, in its order, with Collections and Playlists last; "Libraries" (the Libraries page, and the menu the bar
	/// folds libraries into when it runs out of room: <see cref="Fold"/>); then the sections the plugins add, in their order.
	/// </summary>
	public static IReadOnlyList<TopTab> Build(IReadOnlyList<BaseItemDto> libraries, IReadOnlyList<CustomNavigationViewItem> sections)
	{
		List<TopTab> tabs = [new("Home", HomeKey, Kind: TopTabKind.Home)];
		tabs.AddRange(Ordered(libraries).Select(Library));
		tabs.Add(new TopTab(LibrariesTitle, LibrariesKey, Kind: TopTabKind.Libraries) { Menu = [AllLibrariesEntry] });

		foreach (var section in sections.Where(x => !IsLibraryPin(x)))
		{
			if (!tabs.Any(t => t.Key == section.Key && Equals(t.Parameter, section.Parameter)))
			{
				tabs.Add(new TopTab(section.Name, section.Key, section.Parameter));
			}
		}

		return tabs;
	}

	private static TopTab AllLibrariesEntry { get; } = new(AllLibraries, LibrariesKey, Kind: TopTabKind.Libraries);

	private static TopTab Library(BaseItemDto library) => new(library.Name ?? "Library", LibraryKey, library, TopTabKind.Library);

	/// <summary>The server's views in its order, Collections (box sets) and Playlists after the media libraries.</summary>
	public static IReadOnlyList<BaseItemDto> Ordered(IReadOnlyList<BaseItemDto> libraries) =>
		[.. libraries.Where(x => !IsCollectionView(x)), .. libraries.Where(IsCollectionView)];

	/// <summary>A view of collections or playlists rather than of media.</summary>
	public static bool IsCollectionView(BaseItemDto view) =>
		view.CollectionType is BaseItemDto_CollectionType.Boxsets or BaseItemDto_CollectionType.Playlists;

	/// <summary>How many library tabs <paramref name="tabs"/> (a <see cref="Build"/> row) has.</summary>
	public static int LibraryCount(IReadOnlyList<TopTab> tabs) => tabs.Count(t => t.Kind == TopTabKind.Library);

	/// <summary>
	/// Nixie 1.4.0 (the review's X6 and X7): the row the bar shows with room for <paramref name="shown"/> library tabs: the
	/// libraries after them move, in order, into "Libraries ▾", whose menu is All libraries and then them. While the lit
	/// destination (<paramref name="lit"/>) is a folded library, the folded tab is named after it ("Movies"), so the bar still
	/// says where you are. With every library shown, "Libraries" is a plain tab to the Libraries page.
	/// </summary>
	public static IReadOnlyList<TopTab> Fold(IReadOnlyList<TopTab> tabs, int shown, TopMark? lit)
	{
		var libraries = tabs.Where(t => t.Kind == TopTabKind.Library).ToList();
		shown = Math.Clamp(shown, 0, libraries.Count);
		var folded = libraries.Skip(shown).ToList();
		var current = folded.FirstOrDefault(t => lit is { } m && t.Mark == m);
		List<TopTab> row = [];
		foreach (var tab in tabs)
		{
			if (tab.Kind == TopTabKind.Library && folded.Contains(tab))
			{
				continue;
			}

			row.Add(tab.Kind == TopTabKind.Libraries
				? tab with { Title = current?.Title ?? LibrariesTitle, Menu = [AllLibrariesEntry, .. folded] }
				: tab);
		}

		return row;
	}

	private static bool IsLibraryPin(CustomNavigationViewItem item) => item.Key == LibraryKey && item.Parameter is not null;

	/// <summary>The pages that are a destination of their own though no tab opens them.</summary>
	private static readonly Dictionary<string, TopMark> OwnDestinations = new()
	{
		[TopMark.Search.Key] = TopMark.Search,
		[TopMark.Settings.Key] = TopMark.Settings,
	};

	/// <summary>
	/// Nixie 1.4.0, the trail rule (the review's X7): the destination lit while a page shows. A destination the user chose
	/// directly lights itself: Home, a library (a tab, the Libraries menu, the Libraries page's tiles), the Libraries page, a
	/// plugin's section, the search results, Settings. Any other page (an item, a season, a collection, the player, the
	/// server's administration) is drilled into from the lit one and keeps it (<paramref name="current"/>); one opened from the
	/// search box (<paramref name="fromSearch"/>) lights the search box. <paramref name="tabs"/> is the whole row
	/// (<see cref="Build"/>), folded or not.
	/// </summary>
	public static TopMark? MarkFor(IReadOnlyList<TopTab> tabs, string pageKey, object? parameter, TopMark? current, bool fromSearch = false)
	{
		if (pageKey == LibraryKey)
		{
			var id = parameter switch { BaseItemDto dto => dto.Id, Guid g => g, _ => null };
			// a library page for something that is not a library (a collection: GlobalCommands.DisplayDto) is drilled into
			return tabs.FirstOrDefault(t => t.Kind == TopTabKind.Library && id is not null && t.LibraryId == id) is { } library
				? library.Mark
				: fromSearch ? TopMark.Search : current;
		}

		if (tabs.FirstOrDefault(t => t.Key == pageKey && t.Kind != TopTabKind.Library) is { } own)
		{
			return own.Mark;
		}

		if (OwnDestinations.TryGetValue(pageKey, out var destination))
		{
			return destination;
		}

		return fromSearch ? TopMark.Search : current;
	}

	/// <summary>
	/// Nixie 1.4.0: the eyebrow over an item page: the tab of the item's <paramref name="library"/>, unless that library is the
	/// lit destination (the bar says it already); null for none.
	/// </summary>
	public static TopTab? EyebrowFor(IReadOnlyList<TopTab> tabs, Guid? library, TopMark? lit) =>
		library is null ? null
		: tabs.FirstOrDefault(t => t.Kind == TopTabKind.Library && t.LibraryId == library) is { } tab && tab.Mark != lit ? tab
		: null;

	/// <summary>
	/// The tab that shows <paramref name="lit"/> in <paramref name="row"/> (a <see cref="Fold"/> row): its own, or "Libraries ▾"
	/// for a folded library; -1 for none (the search box, Settings, a destination that went).
	/// </summary>
	public static int IndexOf(IReadOnlyList<TopTab> row, TopMark? lit)
	{
		if (lit is not { } mark)
		{
			return -1;
		}

		for (var i = 0; i < row.Count; i++)
		{
			if (row[i].Mark == mark)
			{
				return i;
			}
		}

		if (mark.Key == LibraryKey)
		{
			for (var i = 0; i < row.Count; i++)
			{
				if (row[i].Kind == TopTabKind.Libraries && row[i].Menu.Any(t => t.Mark == mark))
				{
					return i;
				}
			}
		}

		return -1;
	}
}

/// <summary>Nixie 1.4.0: the entries of the bar's menus, null for a separator.</summary>
public static class TopMenus
{
	/// <summary>"Libraries ▾": All libraries, a separator, then the libraries folded into it.</summary>
	public static IReadOnlyList<TopTab?> Libraries(TopTab folded) =>
		folded.Menu.Count == 0 ? [] : [folded.Menu[0], .. folded.Menu.Count > 1 ? [null, .. folded.Menu.Skip(1)] : Array.Empty<TopTab?>()];

	/// <summary>
	/// The narrow window's Sections menu: Home, every library, a separator, All libraries, then the plugins' sections (a flat
	/// list: one press to any library, as wide).
	/// </summary>
	public static IReadOnlyList<TopTab?> Sections(IReadOnlyList<TopTab> all)
	{
		List<TopTab?> entries = [];
		foreach (var tab in all)
		{
			if (tab.Kind == TopTabKind.Libraries)
			{
				entries.Add(null);
				entries.Add(tab.Menu.Count > 0 ? tab.Menu[0] : tab);
				continue;
			}

			entries.Add(tab);
		}

		return entries;
	}
}

/// <summary>
/// 1.2.0 (the top bar, replacing the left rail): the shell's tabs, which destination is lit, and opening one. It is the
/// shell's navigation-item service too: the plugins add their sections here (Diary, Reporting).
/// Nixie 1.4.0: every view is a tab; the bar folds them by width (<see cref="Fold"/>, from TopBar.Fit), and exactly one
/// destination is lit (<see cref="Lit"/>: the trail rule).
/// </summary>
public sealed partial class TopNavigation(INavigationServiceCore navigation, ILocalSettingsService localSettings, ILogger<TopNavigation> logger) : ObservableObject, INavigationViewServiceCore
{
	/// <summary>The setting the rail kept its pinned items in. Nixie 1.4.0: pins are gone (every library is a tab); never read.</summary>
	internal const string PinsSetting = "_Items";

	private readonly List<CustomNavigationViewItem> _sections = [];
	private IReadOnlyList<BaseItemDto> _libraries = [];

	public object? Key => null;

	/// <summary>Every tab, nothing folded: what the bar measures (TopBar.Fit).</summary>
	[ObservableProperty]
	public partial IReadOnlyList<TopTab> AllTabs { get; private set; } = TopTabs.Build([], []);

	/// <summary>The row the bar shows: <see cref="AllTabs"/> with the libraries that do not fit folded into "Libraries ▾".</summary>
	[ObservableProperty]
	public partial IReadOnlyList<TopTab> Tabs { get; private set; } = TopTabs.Build([], []);

	/// <summary>How many library tabs the bar has room for (TopBar.Fit); all of them until it says.</summary>
	public int ShownLibraries { get; private set; } = int.MaxValue;

	/// <summary>The marked tab in <see cref="Tabs"/>, -1 for none (the search box or Settings is lit, or nothing).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectedTab))]
	public partial int SelectedIndex { get; private set; } = 0;

	public TopTab? SelectedTab => SelectedIndex >= 0 && SelectedIndex < Tabs.Count ? Tabs[SelectedIndex] : null;

	/// <summary>Nixie 1.4.0: the one lit destination (the trail rule, <see cref="TopTabs.MarkFor"/>).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSearchLit), nameof(IsSettingsLit))]
	public partial TopMark? Lit { get; private set; } = new TopMark(TopTabs.HomeKey);

	/// <summary>The search box is the lit destination (its results, or an item opened from it): it wears the underline.</summary>
	public bool IsSearchLit => Lit == TopMark.Search;

	/// <summary>Settings is the lit destination: the account button wears the ring.</summary>
	public bool IsSettingsLit => Lit == TopMark.Settings;

	/// <summary>The lit destination was chosen again on its own page: the page goes back to its top (the shell does it).</summary>
	public event EventHandler? ReturnToTopRequested;

	private (string Key, object? Parameter)? _shown;

	/// <summary>
	/// When an item was chosen in the search box's list: the page it opens, within <see cref="SearchChoiceLasts"/>, lights the
	/// search box (a choice that opened nothing must not light it on a later page).
	/// </summary>
	private long? _searchChosenAt;

	public static readonly TimeSpan SearchChoiceLasts = TimeSpan.FromSeconds(3);

	/// <summary>The clock (milliseconds) the search choice is timed by (tests).</summary>
	internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

	/// <summary>A new shell (signed in): the sections are added again by the plugins after this, the libraries asked for by <see cref="SetLibraries"/>.</summary>
	public void Reset()
	{
		_sections.Clear();
		_libraries = [];
		_shown = null;
		_marks.Clear();
		_searchChosenAt = null;
		Lit = new TopMark(TopTabs.HomeKey);
		Rebuild();
	}

	public void SetLibraries(IReadOnlyList<BaseItemDto> libraries)
	{
		_libraries = libraries;
		Rebuild();
	}

	/// <summary>
	/// Nixie 1.4.0: the bar has room for <paramref name="shown"/> library tabs (TopBar.Fit); the rest fold into "Libraries ▾".
	/// </summary>
	public void Fold(int shown)
	{
		shown = Math.Max(0, shown);
		var libraries = TopTabs.LibraryCount(AllTabs);
		if (Math.Min(shown, libraries) == Math.Min(ShownLibraries, libraries))
		{
			ShownLibraries = shown;
			return;
		}

		ShownLibraries = shown;
		ShowRow();
	}

	/// <summary>The search box opened an item (its list): that page lights the search box (the trail rule).</summary>
	public void ChooseSearch() => _searchChosenAt = Clock();

	/// <summary>The destination lit when the page at each depth of the frame's history was shown.</summary>
	private readonly Dictionary<int, TopMark?> _marks = [];

	/// <summary>
	/// The shell's frame shows this page now (its view model's key and parameter): the destination it lights (the trail rule).
	/// With its depth in the frame's history, the destination is kept with the page; a page returned to
	/// (<paramref name="returning"/>: Back, Forward) gets the one it had.
	/// </summary>
	public void OnPageShown(string pageKey, object? parameter, int depth = -1, bool returning = false)
	{
		_shown = (pageKey, parameter);
		var fromSearch = _searchChosenAt is { } at && Clock() - at <= SearchChoiceLasts.TotalMilliseconds;
		_searchChosenAt = null;
		Lit = returning && depth >= 0 && _marks.TryGetValue(depth, out var mark)
			? mark
			: TopTabs.MarkFor(AllTabs, pageKey, parameter, Lit, fromSearch);
		ShowRow();
		ShowEyebrow(pageKey, parameter);

		if (depth < 0)
		{
			return;
		}

		if (!returning)
		{
			// a new page drops the marks of the pages the history no longer goes forward to
			foreach (var deeper in _marks.Keys.Where(d => d > depth).ToList())
			{
				_marks.Remove(deeper);
			}
		}

		_marks[depth] = Lit;
	}

	/// <summary>
	/// Nixie 1.4.0 (the trail rule's location, the review's Y3): the library an item page shows when it was reached from
	/// another section ("Movies ›" over a film opened from Home or the search results); null on any other page, or when the
	/// item's own library is the lit destination. Item pages show it (lc:TrailEyebrow).
	/// </summary>
	[ObservableProperty]
	public partial TopTab? Eyebrow { get; private set; }

	/// <summary>The item's library (its id), asked of the server (the shell sets it: the item's ancestors); null for none.</summary>
	public Func<BaseItemDto, Task<Guid?>>? LibraryOf { get; set; }

	/// <summary>The item pages that show the eyebrow.</summary>
	public static IReadOnlySet<string> ItemPages { get; } = new HashSet<string>
	{
		typeof(MovieViewModel).FullName!, typeof(SeriesViewModel).FullName!, typeof(SeasonViewModel).FullName!, typeof(EpisodeViewModel).FullName!,
	};

	private int _eyebrowFor;

	/// <summary>The eyebrow's last lookup (tests wait on it).</summary>
	internal Task EyebrowLookup { get; private set; } = Task.CompletedTask;

	private void ShowEyebrow(string pageKey, object? parameter)
	{
		var version = ++_eyebrowFor;
		Eyebrow = null;
		if (!ItemPages.Contains(pageKey) || parameter is not BaseItemDto item || LibraryOf is not { } libraryOf)
		{
			EyebrowLookup = Task.CompletedTask;
			return;
		}

		EyebrowLookup = Lookup();

		async Task Lookup()
		{
			Guid? library;
			try
			{
				library = await libraryOf(item);
			}
			catch (Exception ex)
			{
				logger.LogDebug("Top bar: no library for the eyebrow: {Error}", ex.Message);
				return;
			}

			// a page shown since: its own lookup decides
			if (version == _eyebrowFor)
			{
				Eyebrow = TopTabs.EyebrowFor(AllTabs, library, Lit);
			}
		}
	}

	/// <summary>
	/// Opens a tab's page (a tab with a menu opens its menu instead: the view does that). Nixie 1.4.0: the lit destination
	/// chosen again on its own page goes back to its top (<see cref="ReturnToTopRequested"/>); from a page drilled into from
	/// it, it opens its page.
	/// </summary>
	public bool Open(TopTab tab)
	{
		if (IsShowing(tab))
		{
			logger.LogDebug("Top bar: {Tab} again: back to its top", tab.Title);
			ReturnToTopRequested?.Invoke(this, EventArgs.Empty);
			return true;
		}

		var opened = navigation.NavigateTo(tab.Key, tab.Parameter);
		logger.LogDebug("Top bar: {Tab} ({Opened})", tab.Title, opened);
		return opened;
	}

	/// <summary>Whether the page shown is <paramref name="tab"/>'s own page.</summary>
	public bool IsShowing(TopTab tab)
	{
		if (_shown is not { } shown || shown.Key != tab.Key)
		{
			return false;
		}

		if (tab.Kind == TopTabKind.Library)
		{
			var id = shown.Parameter switch { BaseItemDto dto => dto.Id, Guid g => g, _ => null };
			return id is not null && id == tab.LibraryId;
		}

		return Equals(shown.Parameter, tab.Parameter) || tab.Parameter is null;
	}

	public void AddNavigationItem(CustomNavigationViewItem item)
	{
		// Nixie 1.4.0: a library pinned to the bar (the Libraries page's old "Add to the top bar") is a tab already
		if (item.Key == TopTabs.LibraryKey || _sections.Any(x => x.Key == item.Key && x.Parameter == item.Parameter))
		{
			return;
		}

		_sections.Add(item);
		Rebuild();
	}

	public void RemoveNavigationItem(CustomNavigationViewItem item)
	{
		if (_sections.RemoveAll(x => x.Key == item.Key && x.Parameter == item.Parameter) > 0)
		{
			Rebuild();
		}
	}

	/// <summary>Nixie 1.4.0: nothing is kept (the pins are gone; the plugins add their sections at every start).</summary>
	public void SaveCustomViews()
	{
	}

	private void Rebuild()
	{
		AllTabs = TopTabs.Build(_libraries, _sections);
		// the destination stays lit when the list changes (the libraries arrived, a plugin added a section), unless it went
		if (Lit is { } lit && lit != TopMark.Search && lit != TopMark.Settings
			&& !AllTabs.Any(t => t.Mark == lit) && _shown is { } shown)
		{
			Lit = TopTabs.MarkFor(AllTabs, shown.Key, shown.Parameter, null);
		}

		ShowRow();
	}

	/// <summary>The row for the room and the lit destination (the folded tab is named after a folded lit library).</summary>
	private void ShowRow()
	{
		var row = TopTabs.Fold(AllTabs, ShownLibraries, Lit);
		if (!SameRow(row, Tabs))
		{
			Tabs = row;
		}

		SelectedIndex = TopTabs.IndexOf(Tabs, Lit);
	}

	private static bool SameRow(IReadOnlyList<TopTab> a, IReadOnlyList<TopTab> b) =>
		a.Count == b.Count && a.Zip(b).All(p => p.First.Title == p.Second.Title && p.First.Mark == p.Second.Mark
			&& p.First.Kind == p.Second.Kind && p.First.Menu.Select(m => m.Mark).SequenceEqual(p.Second.Menu.Select(m => m.Mark)));
}
