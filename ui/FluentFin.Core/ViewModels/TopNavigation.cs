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

	/// <summary>One library: a favourite's tab (Nixie 1.6), or an entry of "Libraries ▾".</summary>
	Library,

	/// <summary>
	/// "Libraries ▾": Nixie 1.6 (the review's T3), always a menu: All libraries, then every media library. Its first entry,
	/// All libraries, is the Libraries page.
	/// </summary>
	Libraries,

	/// <summary>A section a plugin adds (Diary, Playback reports). Nixie 1.6 (T4): in the account menu, not the bar.</summary>
	Section,

	/// <summary>Nixie 1.6 (T2): the one Collections destination (GlobalCommands.OpenCollections).</summary>
	Collections,
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
/// "Libraries ▾" the menu it opens instead.
/// </summary>
public sealed record TopTab(string Title, string Key, object? Parameter = null, TopTabKind Kind = TopTabKind.Section)
{
	/// <summary>Nixie 1.6: "Libraries ▾"'s menu: All libraries, then every media library in the server's order. Empty for any other tab.</summary>
	public IReadOnlyList<TopTab> Menu { get; init; } = [];

	/// <summary>Nixie 1.6: the Collections destination's views on the server (its box sets, its playlists), the pages it lights.</summary>
	public IReadOnlyList<Guid> Views { get; init; } = [];

	/// <summary>Nixie 1.6: a plugin section's glyph (the account menu shows it).</summary>
	public string? Glyph { get; init; }

	/// <summary>The library this tab opens, if it opens one.</summary>
	public Guid? LibraryId => Parameter switch
	{
		BaseItemDto dto => dto.Id,
		Guid id => id,
		_ => null,
	};

	/// <summary>Nixie 1.6 (T3): "Libraries ▾" is a menu at every width (its first entry, All libraries, is the page).</summary>
	public bool HasMenu => Kind == TopTabKind.Libraries && Menu.Count > 0;

	/// <summary>The destination this tab is (TopNavigation.Lit).</summary>
	public TopMark Mark => new(Key, Kind == TopTabKind.Library ? LibraryId : null);
}

/// <summary>1.2.0: the top bar's tabs, and which one a page marks.</summary>
public static class TopTabs
{
	public static string HomeKey { get; } = typeof(HomeViewModel).FullName!;

	public static string LibraryKey { get; } = typeof(LibraryViewModel).FullName!;

	public static string LibrariesKey { get; } = typeof(LibrariesLandingPageViewModel).FullName!;

	// 1.6 topbar
	/// <summary>
	/// Nixie 1.6: the Collections destination's key: the Collections page (1.6 integration: its view model's full name, as
	/// GlobalCommands.OpenCollections navigates to it).
	/// </summary>
	public static string CollectionsKey { get; } = typeof(CollectionsViewModel).FullName!;

	public const string CollectionsTitle = "Collections";

	/// <summary>The Collections destination (<see cref="TopTabKind.Collections"/>).</summary>
	public static TopMark CollectionsMark { get; } = new(CollectionsKey);
	// end 1.6 topbar

	/// <summary>The menu's first entry: the Libraries page.</summary>
	public const string AllLibraries = "All libraries";

	public const string LibrariesTitle = "Libraries";

	/// <summary>
	/// Nixie 1.6 (the review's T1 to T4): Home, the favourite libraries (<paramref name="favourites"/>, up to three, in their
	/// order), Collections, then "Libraries ▾", whose menu is All libraries and every media library in the server's order. The
	/// server's Collections and Playlists views are no tabs: the Collections destination opens them. The plugins' sections are
	/// in the account menu (<see cref="SectionTabs"/>).
	/// </summary>
	public static IReadOnlyList<TopTab> Build(IReadOnlyList<BaseItemDto> libraries, IReadOnlyList<Guid> favourites)
	{
		var media = libraries.Where(TopFavourites.IsMedia).ToList();
		List<TopTab> tabs = [new("Home", HomeKey, Kind: TopTabKind.Home)];
		foreach (var library in favourites.Distinct().Select(id => media.FirstOrDefault(l => l.Id == id)).OfType<BaseItemDto>().Take(TopFavourites.Max))
		{
			tabs.Add(Library(library));
		}

		tabs.Add(new TopTab(CollectionsTitle, CollectionsKey, Kind: TopTabKind.Collections)
		{
			Views = [.. libraries.Where(IsCollectionView).Where(v => v.Id is not null).Select(v => v.Id!.Value)],
		});
		tabs.Add(new TopTab(LibrariesTitle, LibrariesKey, Kind: TopTabKind.Libraries) { Menu = [AllLibrariesEntry, .. media.Select(Library)] });
		return tabs;
	}

	/// <summary>Nixie 1.6 (T4): the plugins' sections (Diary, Playback reports), in their order, for the account menu.</summary>
	public static IReadOnlyList<TopTab> SectionTabs(IReadOnlyList<CustomNavigationViewItem> sections)
	{
		List<TopTab> tabs = [];
		foreach (var section in sections.Where(x => !IsLibraryPin(x)))
		{
			if (!tabs.Any(t => t.Key == section.Key && Equals(t.Parameter, section.Parameter)))
			{
				tabs.Add(new TopTab(section.Name, section.Key, section.Parameter) { Glyph = string.IsNullOrEmpty(section.Glyph) ? null : section.Glyph });
			}
		}

		return tabs;
	}

	private static TopTab AllLibrariesEntry { get; } = new(AllLibraries, LibrariesKey, Kind: TopTabKind.Libraries);

	// 1.6 integration: a library's tidy name (the collections stream's CollectionNames.DisplayLibrary: "[HUB] Kids Movies" reads
	// "Kids Movies", unique among the server's libraries), as its page and the Libraries page's tile call it
	private static TopTab Library(BaseItemDto library) => new(CollectionNames.DisplayLibrary(library.Name ?? "Library"), LibraryKey, library, TopTabKind.Library);

	/// <summary>The server's views in its order, Collections (box sets) and Playlists after the media libraries.</summary>
	public static IReadOnlyList<BaseItemDto> Ordered(IReadOnlyList<BaseItemDto> libraries) =>
		[.. libraries.Where(x => !IsCollectionView(x)), .. libraries.Where(IsCollectionView)];

	/// <summary>A view of collections or playlists rather than of media.</summary>
	public static bool IsCollectionView(BaseItemDto view) =>
		view.CollectionType is BaseItemDto_CollectionType.Boxsets or BaseItemDto_CollectionType.Playlists;

	/// <summary>How many library tabs <paramref name="tabs"/> (a <see cref="Build"/> row) has: the favourites.</summary>
	public static int LibraryCount(IReadOnlyList<TopTab> tabs) => tabs.Count(t => t.Kind == TopTabKind.Library);

	/// <summary>Nixie 1.6: every library of a row: the favourites' tabs and "Libraries ▾"'s entries, each once.</summary>
	public static IEnumerable<TopTab> AllLibraryTabs(IReadOnlyList<TopTab> tabs) =>
		tabs.SelectMany(t => t.Menu.Prepend(t)).Where(t => t.Kind == TopTabKind.Library).DistinctBy(t => t.LibraryId);

	/// <summary>
	/// Nixie 1.4.0 (the review's X6 and X7), 1.6: the row the bar shows with room for <paramref name="shown"/> favourites: the
	/// ones after them leave the bar (they are in "Libraries ▾"'s menu, as every library is). While the lit destination
	/// (<paramref name="lit"/>) is a library without a tab of its own, "Libraries ▾" is named after it ("Anime"), so the bar
	/// still says where you are.
	/// </summary>
	public static IReadOnlyList<TopTab> Fold(IReadOnlyList<TopTab> tabs, int shown, TopMark? lit)
	{
		var libraries = tabs.Where(t => t.Kind == TopTabKind.Library).ToList();
		shown = Math.Clamp(shown, 0, libraries.Count);
		var folded = libraries.Skip(shown).ToList();
		var current = lit is { } m && m.Key == LibraryKey && !libraries.Take(shown).Any(t => t.Mark == m)
			? AllLibraryTabs(tabs).FirstOrDefault(t => t.Mark == m)
			: null;
		List<TopTab> row = [];
		foreach (var tab in tabs)
		{
			if (tab.Kind == TopTabKind.Library && folded.Contains(tab))
			{
				continue;
			}

			row.Add(tab.Kind == TopTabKind.Libraries ? tab with { Title = current?.Title ?? LibrariesTitle } : tab);
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
	/// directly lights itself: Home, a library (a tab, the Libraries menu, the Libraries page's tiles), the Libraries page,
	/// Collections (1.6: the Collections page, and the server's box-set and playlist views), a plugin's section
	/// (<paramref name="sections"/>: the account menu's), the search results, Settings. Any other page (an item, a season, a
	/// collection, the player, the server's administration) is drilled into from the lit one and keeps it
	/// (<paramref name="current"/>); one opened from the search box (<paramref name="fromSearch"/>) lights the search box.
	/// <paramref name="tabs"/> is the whole row (<see cref="Build"/>), folded or not.
	/// </summary>
	public static TopMark? MarkFor(IReadOnlyList<TopTab> tabs, string pageKey, object? parameter, TopMark? current, bool fromSearch = false, IReadOnlyList<TopTab>? sections = null)
	{
		if (pageKey == LibraryKey)
		{
			var id = parameter switch { BaseItemDto dto => dto.Id, Guid g => g, _ => null };
			if (id is not null && AllLibraryTabs(tabs).FirstOrDefault(t => t.LibraryId == id) is { } library)
			{
				return library.Mark;
			}

			// 1.6 topbar: the server's Collections or Playlists view is the Collections destination
			if (id is { } view && tabs.FirstOrDefault(t => t.Kind == TopTabKind.Collections) is { } collections && collections.Views.Contains(view))
			{
				return collections.Mark;
			}

			// a library page for something that is not a library (a collection: GlobalCommands.DisplayDto) is drilled into
			return fromSearch ? TopMark.Search : current;
		}

		if (tabs.FirstOrDefault(t => t.Key == pageKey && t.Kind != TopTabKind.Library) is { } own)
		{
			return own.Mark;
		}

		if (sections?.FirstOrDefault(t => t.Key == pageKey) is { } section)
		{
			return section.Mark;
		}

		if (OwnDestinations.TryGetValue(pageKey, out var destination))
		{
			return destination;
		}

		// Nixie 1.7.4 (the rows stream, from the friend's screenshot: "Home" lit over a season page): Home is no section an
		// item belongs to, so an item page drilled into from it lights nothing; TopNavigation lights the item's library once
		// that is known, when the bar has a tab for it (LibraryTab), holding Home until then (no blank step between)
		if (DrilledFromHome(pageKey, current, fromSearch))
		{
			return null;
		}

		return fromSearch ? TopMark.Search : current;
	}

	/// <summary>Nixie 1.7.4: an item page drilled into from Home (not from the search box), which Home cannot stay lit over.</summary>
	public static bool DrilledFromHome(string pageKey, TopMark? current, bool fromSearch) =>
		!fromSearch && current is { } lit && lit.Key == HomeKey && TopNavigation.ItemPages.Contains(pageKey);

	/// <summary>
	/// Nixie 1.7.4: the destination of <paramref name="library"/>'s own tab in <paramref name="row"/> (the bar as shown); null
	/// when it has none there (in "Libraries ▾"'s menu only: lighting it would rename that tab, and the bar would move).
	/// </summary>
	public static TopMark? LibraryTab(IReadOnlyList<TopTab> row, Guid? library) =>
		library is null ? null : row.FirstOrDefault(t => t.Kind == TopTabKind.Library && t.LibraryId == library)?.Mark;

	/// <summary>
	/// Nixie 1.4.0: the eyebrow over an item page: the tab of the item's <paramref name="library"/>, unless that library is the
	/// lit destination (the bar says it already); null for none.
	/// </summary>
	public static TopTab? EyebrowFor(IReadOnlyList<TopTab> tabs, Guid? library, TopMark? lit) =>
		library is null ? null
		: AllLibraryTabs(tabs).FirstOrDefault(t => t.LibraryId == library) is { } tab && tab.Mark != lit ? tab
		: null;

	/// <summary>
	/// The tab that shows <paramref name="lit"/> in <paramref name="row"/> (a <see cref="Fold"/> row): its own, or "Libraries ▾"
	/// for a library without a tab; -1 for none (the search box, Settings, a section, a destination that went).
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
	/// <summary>"Libraries ▾": All libraries, a separator, then every media library.</summary>
	public static IReadOnlyList<TopTab?> Libraries(TopTab libraries) =>
		libraries.Menu.Count == 0 ? [] : [libraries.Menu[0], .. libraries.Menu.Count > 1 ? [null, .. libraries.Menu.Skip(1)] : Array.Empty<TopTab?>()];

	/// <summary>
	/// The narrow window's Sections menu (1.6): Home, the favourites and Collections, as the bar has them; a separator; All
	/// libraries, then the other libraries (a flat list: one press to any library, as wide).
	/// </summary>
	public static IReadOnlyList<TopTab?> Sections(IReadOnlyList<TopTab> all)
	{
		List<TopTab?> entries = [];
		TopTab? libraries = null;
		foreach (var tab in all)
		{
			if (tab.Kind == TopTabKind.Libraries)
			{
				libraries = tab;
				continue;
			}

			entries.Add(tab);
		}

		if (libraries is not null)
		{
			entries.Add(null);
			entries.Add(libraries.Menu.Count > 0 ? libraries.Menu[0] : libraries);
			entries.AddRange(libraries.Menu.Skip(1).Where(l => !all.Any(t => t.Kind == TopTabKind.Library && t.Mark == l.Mark)));
		}

		return entries;
	}
}

/// <summary>
/// 1.2.0 (the top bar, replacing the left rail): the shell's tabs, which destination is lit, and opening one. It is the
/// shell's navigation-item service too: the plugins add their sections here (Diary, Playback reports; Nixie 1.6: shown in
/// the account menu). Nixie 1.4.0: the bar folds by width (<see cref="Fold"/>, from TopBar.Fit), and exactly one destination
/// is lit (<see cref="Lit"/>: the trail rule). Nixie 1.6: the bar's libraries are the user's favourites
/// (<see cref="TopFavourites"/>), kept per server and user (<see cref="FavouritesStore"/>).
/// </summary>
public sealed partial class TopNavigation(INavigationServiceCore navigation, ILocalSettingsService localSettings, ILogger<TopNavigation> logger) : ObservableObject, INavigationViewServiceCore
{
	/// <summary>
	/// The setting the rail kept its pinned items in (1.3's "Add to the top bar"). Nixie 1.6: read once per sign-in, never
	/// written, as the first favourites of a user who has starred nothing yet.
	/// </summary>
	internal const string PinsSetting = "_Items";

	private readonly List<CustomNavigationViewItem> _sections = [];
	private IReadOnlyList<BaseItemDto> _libraries = [];

	public object? Key => null;

	/// <summary>Every tab, nothing folded: what the bar measures (TopBar.Fit).</summary>
	[ObservableProperty]
	public partial IReadOnlyList<TopTab> AllTabs { get; private set; } = TopTabs.Build([], []);

	/// <summary>The row the bar shows: <see cref="AllTabs"/> with the favourites that do not fit left out.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<TopTab> Tabs { get; private set; } = TopTabs.Build([], []);

	/// <summary>Nixie 1.6 (T4): the plugins' sections, which the account menu shows.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAccountLit), nameof(AccountLitName))]
	public partial IReadOnlyList<TopTab> Sections { get; private set; } = [];

	/// <summary>How many library tabs the bar has room for (TopBar.Fit); all of them until it says.</summary>
	public int ShownLibraries { get; private set; } = int.MaxValue;

	/// <summary>The marked tab in <see cref="Tabs"/>, -1 for none (the search box, Settings or a section is lit, or nothing).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectedTab))]
	public partial int SelectedIndex { get; private set; } = 0;

	public TopTab? SelectedTab => SelectedIndex >= 0 && SelectedIndex < Tabs.Count ? Tabs[SelectedIndex] : null;

	/// <summary>Nixie 1.4.0: the one lit destination (the trail rule, <see cref="TopTabs.MarkFor"/>).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSearchLit), nameof(IsSettingsLit), nameof(IsAccountLit), nameof(AccountLitName))]
	public partial TopMark? Lit { get; private set; } = new TopMark(TopTabs.HomeKey);

	/// <summary>The search box is the lit destination (its results, or an item opened from it): it wears the underline.</summary>
	public bool IsSearchLit => Lit == TopMark.Search;

	/// <summary>Settings is the lit destination.</summary>
	public bool IsSettingsLit => Lit == TopMark.Settings;

	/// <summary>Nixie 1.6: Settings or a section of the account menu (Diary) is lit: the account button wears the ring.</summary>
	public bool IsAccountLit => IsSettingsLit || Sections.Any(s => s.Mark == Lit);

	/// <summary>Nixie 1.6: what the account's ring says is open ("Settings", "Diary"); null when it is not lit.</summary>
	public string? AccountLitName => IsSettingsLit ? "Settings" : Sections.FirstOrDefault(s => s.Mark == Lit)?.Title;

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

	// 1.6 topbar
	private readonly FavouritesStore _store = new(localSettings);

	/// <summary>Who is signed in where ("server|user"): the favourites are theirs (tests set it).</summary>
	internal Func<string?> Profile { get; set; } = FavouritesStore.ProfileKey;

	private string? _profile;
	private FavouritesRecord _record = new();
	private IReadOnlyList<Guid> _pins = [];

	/// <summary>Nixie 1.6: the favourite libraries the bar shows (up to <see cref="TopFavourites.Max"/>), in order.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<Guid> Favourites { get; private set; } = [];

	/// <summary>Whether <paramref name="library"/> is one of the bar's favourites (its star is lit).</summary>
	public bool IsFavourite(Guid? library) => library is { } id && Favourites.Contains(id);

	/// <summary>
	/// Nixie 1.6: a library's star pressed (in "Libraries ▾" or on the Libraries page): it joins the bar or leaves it, and
	/// the choice is kept for this server and user. A fourth is refused (<see cref="FavouriteChange.Full"/>: say so).
	/// </summary>
	public FavouriteChange ToggleFavourite(Guid library)
	{
		if (!_libraries.Any(l => l.Id == library && TopFavourites.IsMedia(l)))
		{
			// 1.6.1 (the 1.6 review): no libraries at all is a list that could not be read (the star said nothing): read again
			if (!_libraries.Any(TopFavourites.IsMedia))
			{
				logger.LogInformation("Top bar: a star with no libraries read: asking for them again");
				LibrariesWanted?.Invoke(this, EventArgs.Empty);
				return FavouriteChange.NotRead;
			}

			return FavouriteChange.NotALibrary;
		}

		var (favourites, change) = TopFavourites.Toggle(Favourites, library);
		if (change == FavouriteChange.Full)
		{
			return change;
		}

		_record.Chosen = [.. favourites];
		Save();
		logger.LogInformation("Top bar: a library {Change} (the bar shows {Count})", change == FavouriteChange.Added ? "added" : "removed", favourites.Count);
		Rebuild();
		return change;
	}

	/// <summary>Nixie 1.6.1: the bar has no libraries and wants them (a star pressed meanwhile): the shell reads them again.</summary>
	public event EventHandler? LibrariesWanted;

	/// <summary>The opener of the Collections destination (the shell sets it: GlobalCommands.OpenCollections); null: the Collections page directly.</summary>
	public Action<CollectionsSection>? OpenCollections { get; set; }

	private void ReadFavourites()
	{
		_profile = Profile();
		_record = _store.Read(_profile);
		_pins = _store.PinsFor(_profile); // 1.6.1: only while nobody else has them
	}

	/// <summary>
	/// Nixie 1.6.1 (the 1.6 review): the 1.3 pins were every user's first favourites on this PC. The first profile whose server
	/// has a pinned library keeps them: they become that user's stars on that server (so they stay theirs), and the pins are
	/// marked as theirs (nobody else gets them). A profile whose server has none of them leaves them for another.
	/// </summary>
	/// <remarks>
	/// Review of 1.6.1 (F4): claimed whether or not the user has stars already (a 1.6.0 user who starred from the pins has):
	/// claiming only for a user with no stars left the pins for the next user to sign in. A user with stars keeps them as they
	/// are; one without gets the pins as their stars.
	/// </remarks>
	private void ClaimPins(IReadOnlyList<Guid> favourites)
	{
		if (_pins.Count == 0 || _profile is not { } profile || !_libraries.Any(l => l.Id is { } id && _pins.Contains(id) && TopFavourites.IsMedia(l)))
		{
			return;
		}

		if (_record.Chosen is null)
		{
			_record.Chosen = [.. favourites];
			Save();
		}

		if (_store.PinsClaimedBy() == profile)
		{
			return;
		}

		try
		{
			_store.ClaimPins(profile);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			logger.LogWarning("Top bar: the 1.3 pins could not be marked as this user's: {Error}", ex.Message);
		}

		logger.LogInformation("Top bar: the libraries pinned in 1.3 are this user's on this server now ({Count} favourites)", favourites.Count);
	}

	private void Save()
	{
		try
		{
			_store.Save(_profile, _record);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			logger.LogWarning("Top bar: the favourites could not be saved: {Error}", ex.Message);
		}
	}

	/// <summary>A library's page opened (not returned to, not refreshed): one more open, for the default favourites.</summary>
	private void CountOpen(string pageKey, object? parameter, bool returning, (string Key, object? Parameter)? before)
	{
		if (returning || pageKey != TopTabs.LibraryKey)
		{
			return;
		}

		var id = parameter switch { BaseItemDto dto => dto.Id, Guid g => g, _ => null };
		if (id is not { } library || !_libraries.Any(l => l.Id == library && TopFavourites.IsMedia(l)))
		{
			return;
		}

		var beforeId = before is { Key: var k, Parameter: var p } && k == TopTabs.LibraryKey ? p switch { BaseItemDto dto => dto.Id, Guid g => g, _ => null } : null;
		if (beforeId == library)
		{
			return; // the same library again (R, a sort): not another open
		}

		_record.Opens[library] = _record.Opens.GetValueOrDefault(library) + 1;
		Save();
	}
	// end 1.6 topbar

	/// <summary>A new shell (signed in): the sections are added again by the plugins after this, the libraries asked for by <see cref="SetLibraries"/>.</summary>
	public void Reset()
	{
		FollowNames(); // 1.6.1
		_sections.Clear();
		_libraries = [];
		_shown = null;
		_marks.Clear();
		_searchChosenAt = null;
		_shownDepth = -1;
		_holdingHome = false; // 1.7.4 integration
		Lit = new TopMark(TopTabs.HomeKey);
		ForgetTrails(); // 1.7 upnav
		ReadFavourites(); // 1.6 topbar: this user's favourites on this server
		Rebuild();
	}

	public void SetLibraries(IReadOnlyList<BaseItemDto> libraries)
	{
		FollowNames(); // 1.6.1
		_libraries = libraries;
		if (_profile != Profile())
		{
			ReadFavourites();
		}

		Rebuild();
	}

	/// <summary>
	/// Nixie 1.4.0: the bar has room for <paramref name="shown"/> library tabs (TopBar.Fit); the rest leave the bar.
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

	/// <summary>1.6 topbar: the depth of the page shown (-1: not known).</summary>
	private int _shownDepth = -1;

	/// <summary>
	/// The shell's frame shows this page now (its view model's key and parameter): the destination it lights (the trail rule).
	/// With its depth in the frame's history, the destination is kept with the page; a page returned to
	/// (<paramref name="returning"/>: Back, Forward) gets the one it had.
	/// </summary>
	public void OnPageShown(string pageKey, object? parameter, int depth = -1, bool returning = false)
	{
		var before = _shown;
		_shown = (pageKey, parameter);
		_shownDepth = depth; // 1.6 topbar
		CountOpen(pageKey, parameter, returning, before); // 1.6 topbar
		var fromSearch = _searchChosenAt is { } at && Clock() - at <= SearchChoiceLasts.TotalMilliseconds;
		_searchChosenAt = null;
		var lit = returning && depth >= 0 && _marks.TryGetValue(depth, out var mark)
			? mark
			: TopTabs.MarkFor(AllTabs, pageKey, parameter, Lit, fromSearch, Sections);
		// 1.7.4 integration (the rows review's m5): an item page drilled into from Home keeps Home lit while its library is asked
		// (ShowEyebrow), then lights that library's tab, or nothing, in one change: Home, nothing, then Shows moved the bar twice.
		// Only while there is a lookup to wait for; a page returned to before its lookup ended holds again
		var lookup = ItemPages.Contains(pageKey) && parameter is BaseItemDto && LibraryOf is not null;
		var home = new TopMark(TopTabs.HomeKey);
		_holdingHome = lookup && (lit is null && !returning && TopTabs.DrilledFromHome(pageKey, Lit, fromSearch) || returning && lit == home);
		Lit = _holdingHome ? home : lit;
		ShowRow();
		ShowEyebrow(pageKey, parameter);
		ShowTrail(pageKey, parameter); // 1.7 upnav: the series and season crumbs (TopNavigation.Trail.cs)

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

	/// <summary>1.7.4 integration: Home is held lit over an item page drilled into from it until its library is known.</summary>
	private bool _holdingHome;

	/// <summary>The held Home lets go: the item's library's tab, or nothing (<paramref name="library"/> unknown, or no tab for it in the bar).</summary>
	private void EndHomeHold(Guid? library)
	{
		if (!_holdingHome)
		{
			return;
		}

		_holdingHome = false;
		Lit = TopTabs.LibraryTab(Tabs, library);
		ShowRow();
		if (_shownDepth >= 0)
		{
			_marks[_shownDepth] = Lit;
		}
	}

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
				if (version == _eyebrowFor)
				{
					EndHomeHold(null); // not known: nothing lit, never Home
				}

				return;
			}

			// a page shown since: its own lookup decides
			if (version == _eyebrowFor)
			{
				// Nixie 1.7.4: an item page drilled into from Home lights its library, when it has a tab (else nothing)
				EndHomeHold(library);
				Eyebrow = TopTabs.EyebrowFor(AllTabs, library, Lit);
			}
		}
	}

	/// <summary>
	/// Opens a tab's page (a tab with a menu opens its menu instead: the view does that). Nixie 1.4.0: the lit destination
	/// chosen again on its own page goes back to its top (<see cref="ReturnToTopRequested"/>); from a page drilled into from
	/// it, it opens its page. Nixie 1.6: Collections through <see cref="OpenCollections"/> (GlobalCommands.OpenCollections).
	/// </summary>
	public bool Open(TopTab tab)
	{
		if (IsShowing(tab))
		{
			logger.LogDebug("Top bar: {Tab} again: back to its top", tab.Title);
			ReturnToTopRequested?.Invoke(this, EventArgs.Empty);
			return true;
		}

		// 1.6 topbar
		// 1.6 integration: Collections is its own page (the collections stream's), so the Libraries page is never shown for it and
		// the in-place re-light of topbar's interim route (Collections and All libraries on one page) is retired with that route
		if (tab.Kind == TopTabKind.Collections)
		{
			logger.LogDebug("Top bar: {Tab}", tab.Title);
			if (OpenCollections is { } open)
			{
				open(CollectionsSection.All);
				return true;
			}

			return navigation.NavigateTo(TopTabs.CollectionsKey, new CollectionsPlace(CollectionsSection.All));
		}
		// end 1.6 topbar

		var opened = navigation.NavigateTo(tab.Key, tab.Parameter);
		logger.LogDebug("Top bar: {Tab} ({Opened})", tab.Title, opened);
		return opened;
	}

	/// <summary>Whether the page shown is <paramref name="tab"/>'s own page.</summary>
	public bool IsShowing(TopTab tab)
	{
		if (_shown is not { } shown)
		{
			return false;
		}

		// 1.6 topbar: Collections' page, while it is lit. 1.6 integration: the Collections page, not a "See all" drilled into
		// from it (a kind's every collection) nor the server's box-set view (lit as Collections): from those it opens its page
		if (tab.Kind == TopTabKind.Collections)
		{
			return Lit == tab.Mark && shown.Key == TopTabs.CollectionsKey && shown.Parameter is not CollectionsPlace { SeeAll: true };
		}

		if (shown.Key != tab.Key)
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
		// Nixie 1.4.0: a library pinned to the bar (the Libraries page's old "Add to the top bar") is no section (1.6: the
		// pins are read as the first favourites, FavouritesStore.Pins)
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

	/// <summary>Nixie 1.4.0: nothing is kept here (the plugins add their sections at every start; 1.6: the favourites keep themselves).</summary>
	public void SaveCustomViews()
	{
	}

	private bool _followsNames;

	/// <summary>
	/// Nixie 1.6.1 (the merge review's R4): the tabs carry the libraries' names as they were when built (the tidy names,
	/// <see cref="CollectionNames.DisplayLibrary"/>): "Tidy collection names" turned off or on builds them again, at once.
	/// </summary>
	private void FollowNames()
	{
		if (_followsNames)
		{
			return;
		}

		_followsNames = true;
		CollectionNames.TidyChanged += (_, _) =>
		{
			logger.LogDebug("Top bar: the library names changed (Tidy collection names)");
			Rebuild();
		};
	}

	private void Rebuild()
	{
		// 1.6 topbar: the favourites for the libraries the server lists now
		var favourites = TopFavourites.Pick(_libraries, _record.Chosen, _pins, _record.Opens);
		ClaimPins(favourites); // 1.6.1
		if (!favourites.SequenceEqual(Favourites))
		{
			Favourites = favourites;
		}

		var sections = TopTabs.SectionTabs(_sections);
		if (!sections.SequenceEqual(Sections))
		{
			Sections = sections;
		}

		AllTabs = TopTabs.Build(_libraries, Favourites);
		// the destination stays lit when the list changes (the libraries arrived, a plugin added a section), unless it went
		if (Lit is { } lit && lit != TopMark.Search && lit != TopMark.Settings
			&& !AllTabs.Any(t => t.Mark == lit) && !TopTabs.AllLibraryTabs(AllTabs).Any(t => t.Mark == lit)
			&& !Sections.Any(t => t.Mark == lit) && _shown is { } shown)
		{
			Lit = TopTabs.MarkFor(AllTabs, shown.Key, shown.Parameter, null, false, Sections);
		}

		ShowRow();
	}

	/// <summary>The row for the room and the lit destination ("Libraries ▾" is named after a lit library without a tab).</summary>
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
			&& p.First.Kind == p.Second.Kind && p.First.Menu.Select(m => (m.Mark, m.Title)).SequenceEqual(p.Second.Menu.Select(m => (m.Mark, m.Title)))
			&& p.First.Views.SequenceEqual(p.Second.Views));
}
