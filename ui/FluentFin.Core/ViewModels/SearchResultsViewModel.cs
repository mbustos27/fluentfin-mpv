using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Search;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Seaglass 1.1.4 (interaction audit, item 2): the search results page, opened by Enter on the title bar's search (or its
/// "See all" row), with the query in the title and the page in the back history. Loading, no results and a failed search
/// are the page's LoadState.
/// <para>
/// Nixie 1.5 (search; docs/research/search.md): ranked on the PC (<see cref="SearchRanking"/>). An exact or near-exact
/// title of any kind leads as the top result; the groups follow in the order of their best match; weak matches wait behind
/// each group's "Show N more"; chips show one kind at a time (All, Shows, Movies, Episodes, Collections). The lit chip and
/// the opened groups outlive Back and R for the same query (<see cref="SearchPageMemory"/>); a new search starts on All.
/// </para>
/// </summary>
public partial class SearchResultsViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	private RankedResults _ranked = RankedResults.Empty("");

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title))]
	public partial string Query { get; private set; } = "";

	public string Title => $"Results for “{Query}”";

	/// <summary>The kind shown: null for All.</summary>
	[ObservableProperty]
	public partial SearchKind? Kind { get; private set; }

	/// <summary>The chips: All, then each kind with results, in the fixed order. Empty when only one kind has results.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<SearchChip> Chips { get; private set; } = [];

	/// <summary>The top result (All only): an exact or near-exact title of any kind.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasTopResult))]
	public partial BaseItemViewModel? TopResult { get; private set; }

	public bool HasTopResult => TopResult is not null;

	/// <summary>The top result's kind ("Show").</summary>
	[ObservableProperty]
	public partial string TopResultKind { get; private set; } = "";

	/// <summary>Beside the top result's card: its title, as the card's own title is small.</summary>
	[ObservableProperty]
	public partial string TopResultTitle { get; private set; } = "";

	/// <summary>"Show · 2005" (an episode: "Episode of Spy x Family · 2022").</summary>
	[ObservableProperty]
	public partial string TopResultMeta { get; private set; } = "";

	/// <summary>Why it matched when its name doesn't say: its original title, or the episode that led to the show.</summary>
	[ObservableProperty]
	public partial string TopResultNote { get; private set; } = "";

	/// <summary>The groups shown, in the order of their best match (All), or the lit chip's one group.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<SearchResultGroup> CardGroups { get; private set; } = [];

	public LoadState Results { get; } = new("the search results");

	public async Task OnNavigatedTo(object parameter)
	{
		Query = (parameter as string)?.Trim() ?? "";
		SearchPageMemory.Forgotten -= OnForgotten;
		SearchPageMemory.Forgotten += OnForgotten;
		await Results.RunAsync(Load);
	}

	public Task OnNavigatedFrom()
	{
		SearchPageMemory.Forgotten -= OnForgotten;
		return Task.CompletedTask;
	}

	/// <summary>
	/// Enter in the search box with this page's own query: the navigation service shows nothing new for the same page and
	/// query, so the page starts over itself: All, every group folded.
	/// </summary>
	private void OnForgotten(object? sender, string query)
	{
		if (!string.Equals(query, Query, StringComparison.Ordinal) || _ranked.Count == 0)
		{
			return;
		}

		Kind = null;
		Show();
	}

	/// <summary>A chip was chosen: that kind (null: All). The groups' "Show N more" close again.</summary>
	[RelayCommand]
	public void ShowKind(SearchKind? kind)
	{
		if (kind is { } k && _ranked.GroupOf(k) is null)
		{
			kind = null; // a kind with nothing (a chip from an older answer): All
		}

		if (Kind == kind && CardGroups.Count > 0)
		{
			return;
		}

		Kind = kind;
		SearchPageMemory.Remember(Query, kind, []);
		Show();
	}

	private async Task<LoadOutcome> Load()
	{
		_ranked = RankedResults.Empty(Query);
		Clear();
		if (string.IsNullOrEmpty(Query))
		{
			return LoadOutcome.Empty("Nothing to search for", "Type in the search box, then press Enter.");
		}

		if (await SearchRunner.Run(JellyfinClient, Query) is not { } ranked)
		{
			return LoadOutcome.Failed($"Couldn't search {LoadMessages.ServerName()}", LoadMessages.CheckServer());
		}

		_ranked = ranked;
		if (ranked.Count == 0)
		{
			return LoadOutcome.Empty($"No results for “{Query}”", "Check the spelling, or try fewer words.");
		}

		var (kind, expanded) = SearchPageMemory.Recall(Query);
		Kind = kind is { } k && ranked.GroupOf(k) is not null ? kind : null;
		Show(expanded);
		return LoadOutcome.Loaded;
	}

	private void Clear()
	{
		Chips = [];
		TopResult = null;
		TopResultKind = "";
		(TopResultTitle, TopResultMeta, TopResultNote) = ("", "", "");
		CardGroups = [];
	}

	private void Show(IReadOnlySet<SearchKind>? expanded = null)
	{
		var groups = _ranked.Groups;
		var top = Kind is null ? _ranked.Top : null;
		TopResult = top is null ? null : BaseItemViewModel.FromDto(top.Item);
		TopResultKind = top is null ? "" : SearchLabels.KindTitle(top.Item.Type);
		(TopResultTitle, TopResultMeta, TopResultNote) = top is null ? ("", "", "") : SearchResultsText.Describe(top);
		CardGroups = [.. (Kind is { } kind ? groups.Where(g => g.Kind.Kind == kind) : groups)
			.Select(g => SearchResultGroup.From(g, top, expanded?.Contains(g.Kind.Kind) == true, OnExpandedChanged))
			.Where(g => g.Count > 0)];

		if (groups.Count < 2)
		{
			Chips = [];
			return;
		}

		if (Chips.Count == 0)
		{
			Chips = [new SearchChip(null, "All", _ranked.Count, ShowKind), .. groups.OrderBy(g => g.Kind.Kind).Select(g => new SearchChip(g.Kind.Kind, g.Kind.Title, g.Count, ShowKind))];
		}

		foreach (var chip in Chips)
		{
			chip.IsSelected = chip.Kind == Kind;
		}
	}

	private void OnExpandedChanged() =>
		SearchPageMemory.Remember(Query, Kind, [.. CardGroups.Where(g => g.IsExpanded).Select(g => g.Kind)]);
}

/// <summary>Nixie 1.5 (search): the words beside the top result's card.</summary>
public static class SearchResultsText
{
	public static (string Title, string Meta, string Note) Describe(RankedItem top)
	{
		var item = top.Item;
		var kind = SearchLabels.KindTitle(item.Type);
		var year = item.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture);
		var what = item.Type == Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode && !string.IsNullOrWhiteSpace(item.SeriesName)
			? $"{kind} of {item.SeriesName.Trim()}"
			: kind;
		var meta = string.Join(" · ", new[] { what, year }.Where(x => !string.IsNullOrEmpty(x)));
		var note = top.ViaEpisode is { Name: { Length: > 0 } episode }
			? $"Has the episode “{episode.Trim()}”"
			: !string.IsNullOrWhiteSpace(item.OriginalTitle) && !string.Equals(item.OriginalTitle.Trim(), item.Name?.Trim(), StringComparison.Ordinal)
				? $"Original title: {item.OriginalTitle.Trim()}"
				: "";
		return (item.Name?.Trim() ?? "", meta, note);
	}
}

/// <summary>
/// Nixie 1.5 (search): a type chip: radius 8, one lit at a time; Narrator reads "Movies, 12 results". Lit by any means (a
/// click, Space, Narrator's select: each only checks the radio button), it shows its kind.
/// </summary>
public sealed partial class SearchChip(SearchKind? kind, string label, int count, Action<SearchKind?>? chosen = null) : ObservableObject
{
	public SearchKind? Kind { get; } = kind;

	public string Label { get; } = label;

	public int Count { get; } = count;

	public string Name => $"{Label}, {Count} {(Count == 1 ? "result" : "results")}";

	[ObservableProperty]
	public partial bool IsSelected { get; set; }

	partial void OnIsSelectedChanged(bool value)
	{
		if (value)
		{
			chosen?.Invoke(Kind);
		}
	}
}

/// <summary>
/// A group of the results page as the page shows it: its heading ("Movies (3)"), its cards (the strong matches, and the
/// weak ones once "Show N more" is chosen), and that button's words.
/// </summary>
public sealed partial class SearchResultGroup : ObservableObject
{
	private readonly Action? _changed;

	private SearchResultGroup(SearchKindInfo kind, IReadOnlyList<BaseItemViewModel> strong, IReadOnlyList<BaseItemViewModel> weak, bool expanded, Action? changed)
	{
		Info = kind;
		Strong = strong;
		Weak = weak;
		IsExpanded = expanded && weak.Count > 0;
		_changed = changed;
	}

	public static SearchResultGroup From(RankedGroup group, RankedItem? top, bool expanded, Action? changed = null)
	{
		bool Shown(RankedItem r) => top is null || !ReferenceEquals(r.Item, top.Item);
		return new(group.Kind,
			[.. group.Strong.Where(Shown).Select(r => BaseItemViewModel.FromDto(r.Item))],
			[.. group.Weak.Where(Shown).Select(r => BaseItemViewModel.FromDto(r.Item))],
			expanded, changed);
	}

	public SearchKindInfo Info { get; }

	public SearchKind Kind => Info.Kind;

	/// <summary>The group's name, as Narrator reads its list ("Movies").</summary>
	public string Name => Info.Title;

	public int Count => Strong.Count + Weak.Count;

	/// <summary>"Movies (3)": what Nixie found (Jellyfin 12's TotalRecordCount is a capped candidate count, not a count of matches).</summary>
	public string Heading => $"{Info.Title} ({Count})";

	public IReadOnlyList<BaseItemViewModel> Strong { get; }

	public IReadOnlyList<BaseItemViewModel> Weak { get; }

	public bool HasMore => Weak.Count > 0;

	/// <summary>Nothing strong here: the group is its heading and its button until the button is chosen.</summary>
	public bool HasCards => Items.Count > 0;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Items), nameof(HasCards), nameof(MoreText), nameof(MoreName))]
	public partial bool IsExpanded { get; set; }

	public IReadOnlyList<BaseItemViewModel> Items => IsExpanded ? [.. Strong, .. Weak] : Strong;

	/// <summary>"Show 12 more" after the strong matches ("Show 295 weaker matches" when there are none), then "Show fewer".</summary>
	public string MoreText => IsExpanded ? "Show fewer"
		: Strong.Count == 0 ? $"Show {Weak.Count} weaker {(Weak.Count == 1 ? "match" : "matches")}"
		: $"Show {Weak.Count} more";

	/// <summary>What Narrator reads: "Show 295 more movies, weaker matches", "Show fewer movies".</summary>
	public string MoreName => IsExpanded ? $"Show fewer {Info.Nouns}" : $"Show {Weak.Count} more {(Weak.Count == 1 ? Info.Noun : Info.Nouns)}, weaker matches";

	[RelayCommand]
	private void ToggleMore()
	{
		if (HasMore)
		{
			IsExpanded = !IsExpanded;
			_changed?.Invoke();
		}
	}
}

/// <summary>
/// Nixie 1.5 (search): the lit chip and the opened groups of the last few queries, so Back to a results page and R on it
/// keep them. The search box forgets a query's when it is searched anew (a new search starts on All).
/// </summary>
public static class SearchPageMemory
{
	private const int Kept = 12;
	private static readonly List<(string Query, SearchKind? Kind, SearchKind[] Expanded)> Entries = [];
	private static readonly Lock Gate = new();

	public static void Remember(string query, SearchKind? kind, SearchKind[] expanded)
	{
		lock (Gate)
		{
			Entries.RemoveAll(e => string.Equals(e.Query, query, StringComparison.Ordinal));
			if (kind is not null || expanded.Length > 0)
			{
				Entries.Add((query, kind, expanded));
			}

			if (Entries.Count > Kept)
			{
				Entries.RemoveAt(0);
			}
		}
	}

	public static (SearchKind? Kind, IReadOnlySet<SearchKind> Expanded) Recall(string query)
	{
		lock (Gate)
		{
			var entry = Entries.LastOrDefault(e => string.Equals(e.Query, query, StringComparison.Ordinal));
			return entry.Query is null ? (null, new HashSet<SearchKind>()) : (entry.Kind, entry.Expanded.ToHashSet());
		}
	}

	/// <summary>A new search for the query: its place is forgotten, and a results page showing it starts over.</summary>
	public static void Forget(string query)
	{
		Remember(query, null, []);
		Forgotten?.Invoke(null, query);
	}

	public static event EventHandler<string>? Forgotten;
}
