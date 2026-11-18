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

	/// <summary>One library (when there are few, each is a tab).</summary>
	Library,

	/// <summary>"Libraries": every library in a menu (when there are many, or none could be listed).</summary>
	Libraries,

	/// <summary>A section a plugin adds (Diary, Reporting), or a pinned library.</summary>
	Section,
}

/// <summary>
/// 1.2.0 (the top bar): one tab of the top bar: its text, the page it opens (a view model's key, with a parameter), and for
/// "Libraries" the menu it opens instead.
/// </summary>
public sealed record TopTab(string Title, string Key, object? Parameter = null, TopTabKind Kind = TopTabKind.Section)
{
	/// <summary>The "Libraries" tab's menu: "All libraries", then each library. Empty for any other tab.</summary>
	public IReadOnlyList<TopTab> Menu { get; init; } = [];

	/// <summary>The library this tab opens, if it opens one (by the library itself, or a pin's id).</summary>
	public Guid? LibraryId => Parameter switch
	{
		BaseItemDto dto => dto.Id,
		Guid id => id,
		_ => null,
	};

	public bool HasMenu => Kind == TopTabKind.Libraries && Menu.Count > 0;
}

/// <summary>1.2.0: the top bar's tabs, and which one a page marks.</summary>
public static class TopTabs
{
	/// <summary>Up to this many libraries are tabs of their own; more go in one "Libraries" tab with a menu.</summary>
	public const int MaxLibraryTabs = 4;

	public static string HomeKey { get; } = typeof(HomeViewModel).FullName!;

	public static string LibraryKey { get; } = typeof(LibraryViewModel).FullName!;

	public static string LibrariesKey { get; } = typeof(LibrariesLandingPageViewModel).FullName!;

	/// <summary>
	/// Pages that are not in any tab's section: they mark no tab (Settings, the search results, the server's administration).
	/// Any other page off the tabs (an item, a season, the player) keeps the tab you came from.
	/// </summary>
	public static bool IsOutsideTheTabs(string pageKey) => OutsideTheTabs.Contains(pageKey);

	private static readonly HashSet<string> OutsideTheTabs =
	[
		typeof(SettingsViewModel).FullName!,
		typeof(SearchResultsViewModel).FullName!,
		typeof(DashboardViewModel).FullName!,
		// the server's administration (Settings > Servers and accounts), a page of the app's own project
		"FluentFin.Views.JellyfinSettingsViewModel",
	];

	/// <summary>
	/// Home; then each library when there are 1 to <see cref="MaxLibraryTabs"/>, else one "Libraries" tab whose menu lists
	/// them (with pinned libraries as tabs of their own before it); then the sections the plugins add, in their order.
	/// </summary>
	public static IReadOnlyList<TopTab> Build(IReadOnlyList<BaseItemDto> libraries, IReadOnlyList<CustomNavigationViewItem> sections)
	{
		List<TopTab> tabs = [new("Home", HomeKey, Kind: TopTabKind.Home)];
		var inline = libraries.Count is > 0 and <= MaxLibraryTabs;
		if (inline)
		{
			tabs.AddRange(libraries.Select(Library));
		}
		else
		{
			// a pin is a tab of its own only when the libraries are in the menu; with few, every library already is one
			foreach (var pin in sections.Where(IsLibraryPin))
			{
				var library = libraries.FirstOrDefault(x => x.Id == pin.Parameter);
				tabs.Add(library is null ? new TopTab(pin.Name, pin.Key, pin.Parameter, TopTabKind.Library) : Library(library));
			}

			tabs.Add(new TopTab("Libraries", LibrariesKey, Kind: TopTabKind.Libraries)
			{
				Menu = libraries.Count == 0 ? [] : [new("All libraries", LibrariesKey, Kind: TopTabKind.Libraries), .. libraries.Select(Library)],
			});
		}

		foreach (var section in sections.Where(x => !IsLibraryPin(x)))
		{
			if (!tabs.Any(t => t.Key == section.Key && Equals(t.Parameter, section.Parameter)))
			{
				tabs.Add(new TopTab(section.Name, section.Key, section.Parameter));
			}
		}

		return tabs;
	}

	private static TopTab Library(BaseItemDto library) => new(library.Name ?? "Library", LibraryKey, library, TopTabKind.Library);

	private static bool IsLibraryPin(CustomNavigationViewItem item) => item.Key == LibraryKey && item.Parameter is not null;

	/// <summary>
	/// The tab a page marks: Home, its library's tab (or "Libraries" when the libraries are in the menu), the section's own
	/// tab; -1 for a page outside the tabs (<see cref="IsOutsideTheTabs"/>); <paramref name="current"/> for a page inside a
	/// section (an item, a season, the player).
	/// </summary>
	public static int SelectionFor(IReadOnlyList<TopTab> tabs, string pageKey, object? parameter, int current)
	{
		if (pageKey == LibraryKey)
		{
			var id = parameter switch { BaseItemDto dto => dto.Id, Guid g => g, _ => null };
			var library = id is null ? -1 : IndexOf(tabs, t => t.Kind == TopTabKind.Library && t.LibraryId == id);
			if (library >= 0)
			{
				return library;
			}

			var menu = IndexOf(tabs, t => t.Kind == TopTabKind.Libraries);
			return menu >= 0 ? menu : current;
		}

		var own = IndexOf(tabs, t => t.Key == pageKey && t.Kind != TopTabKind.Library);
		if (own >= 0)
		{
			return own;
		}

		return IsOutsideTheTabs(pageKey) ? -1 : current;
	}

	private static int IndexOf(IReadOnlyList<TopTab> tabs, Func<TopTab, bool> match)
	{
		for (var i = 0; i < tabs.Count; i++)
		{
			if (match(tabs[i]))
			{
				return i;
			}
		}

		return -1;
	}
}

/// <summary>
/// 1.2.0 (the top bar, replacing the left rail): the shell's tabs, which one is marked, and opening one. It is the shell's
/// navigation-item service too: the plugins add their sections here (Diary, Reporting) and "Pin" adds a library, kept on
/// this PC as the rail's were (the same setting).
/// </summary>
public sealed partial class TopNavigation(INavigationServiceCore navigation, ILocalSettingsService localSettings, ILogger<TopNavigation> logger) : ObservableObject, INavigationViewServiceCore
{
	/// <summary>The setting the rail kept its pinned items in (NavigationViewService with no key): kept, so pins survive.</summary>
	internal const string PinsSetting = "_Items";

	private readonly List<CustomNavigationViewItem> _sections = [];
	private IReadOnlyList<BaseItemDto> _libraries = [];

	public object? Key => null;

	[ObservableProperty]
	public partial IReadOnlyList<TopTab> Tabs { get; private set; } = TopTabs.Build([], []);

	/// <summary>The marked tab, -1 for none.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectedTab))]
	public partial int SelectedIndex { get; private set; } = 0;

	public TopTab? SelectedTab => SelectedIndex >= 0 && SelectedIndex < Tabs.Count ? Tabs[SelectedIndex] : null;

	private (string Key, object? Parameter)? _shown;

	/// <summary>
	/// A new shell (signed in): the sections are added again by the plugins after this, the pins read back, the libraries
	/// asked for by <see cref="SetLibraries"/>.
	/// </summary>
	public void Reset()
	{
		_sections.Clear();
		_libraries = [];
		_shown = null;
		foreach (var pin in localSettings.ReadSetting<List<CustomNavigationViewItem>>(PinsSetting, []))
		{
			_sections.Add(pin);
		}

		Rebuild();
		SelectedIndex = 0;
	}

	public void SetLibraries(IReadOnlyList<BaseItemDto> libraries)
	{
		_libraries = libraries;
		Rebuild();
	}

	/// <summary>The shell's frame shows this page now (its view model's key and parameter): the tab it marks.</summary>
	public void OnPageShown(string pageKey, object? parameter)
	{
		_shown = (pageKey, parameter);
		SelectedIndex = TopTabs.SelectionFor(Tabs, pageKey, parameter, SelectedIndex);
	}

	/// <summary>Opens a tab's page (a tab with a menu opens its menu instead: the view does that).</summary>
	public bool Open(TopTab tab)
	{
		var opened = navigation.NavigateTo(tab.Key, tab.Parameter);
		logger.LogDebug("Top bar: {Tab} ({Opened})", tab.Title, opened);
		return opened;
	}

	public void AddNavigationItem(CustomNavigationViewItem item)
	{
		if (_sections.Any(x => x.Key == item.Key && x.Parameter == item.Parameter))
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
			SaveCustomViews();
			Rebuild();
		}
	}

	public void SaveCustomViews() => localSettings.SaveSetting(PinsSetting, _sections.Where(x => x.Persistent).ToList());

	private void Rebuild()
	{
		var marked = SelectedTab;
		Tabs = TopTabs.Build(_libraries, _sections);
		// the same tab stays marked when the list changes (the libraries arrived, a plugin added a section)
		SelectedIndex = _shown is { } shown
			? TopTabs.SelectionFor(Tabs, shown.Key, shown.Parameter, marked is null ? -1 : IndexOfSame(marked))
			: marked is null ? -1 : Math.Max(0, IndexOfSame(marked));
	}

	private int IndexOfSame(TopTab tab)
	{
		for (var i = 0; i < Tabs.Count; i++)
		{
			if (Tabs[i].Key == tab.Key && Tabs[i].LibraryId == tab.LibraryId)
			{
				return i;
			}
		}

		return -1;
	}
}
