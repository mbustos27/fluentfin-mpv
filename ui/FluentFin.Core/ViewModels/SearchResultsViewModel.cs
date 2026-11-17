using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Seaglass 1.1.4 (interaction audit, item 2): the search results page, opened by Enter on the title bar's search (or its
/// "See all" row): every match for the query, grouped by type (<see cref="SearchResults"/>), with the query in the title and
/// the page in the back history. Loading, no results and a failed search are the page's LoadState.
/// </summary>
public partial class SearchResultsViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title))]
	public partial string Query { get; private set; } = "";

	public string Title => $"Results for “{Query}”";

	[ObservableProperty]
	public partial IReadOnlyList<SearchGroup> Groups { get; private set; } = [];

	/// <summary>The groups' items as cards (the card control takes a BaseItemViewModel), in the groups' order.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<SearchResultGroup> CardGroups { get; private set; } = [];

	public LoadState Results { get; } = new("the search results");

	public async Task OnNavigatedTo(object parameter)
	{
		Query = (parameter as string)?.Trim() ?? "";
		await Results.RunAsync(Load);
	}

	public Task OnNavigatedFrom() => Task.CompletedTask;

	private async Task<LoadOutcome> Load()
	{
		if (string.IsNullOrEmpty(Query))
		{
			Groups = [];
			CardGroups = [];
			return LoadOutcome.Empty("Nothing to search for", "Type in the search box, then press Enter.");
		}

		// one request per type, together: one shared limit let the episodes crowd the movies and shows out
		var answers = await Task.WhenAll(SearchResults.Kinds.Select(kind => JellyfinClient.Search(Query, [kind])));
		if (answers.All(x => x is null))
		{
			return LoadOutcome.Failed($"Couldn't search {LoadMessages.ServerName()}", LoadMessages.CheckServer());
		}

		var totals = SearchResults.Kinds.Zip(answers)
			.Where(x => x.Second?.TotalRecordCount is not null)
			.ToDictionary(x => SearchResults.TypeOf(x.First), x => x.Second!.TotalRecordCount!.Value);
		Groups = SearchResults.Group(answers.Where(x => x?.Items is not null).SelectMany(x => x!.Items!), totals);
		CardGroups = [.. Groups.Select(g => new SearchResultGroup(g.Heading, g.Title, [.. g.Items.Select(BaseItemViewModel.FromDto)]))];
		return Groups.Count == 0
			? LoadOutcome.Empty($"No results for “{Query}”", "Check the spelling, or try fewer words.")
			: LoadOutcome.Loaded;
	}
}

/// <summary>A group of the results page as the page shows it: its heading ("Movies (3)"), its name, its cards.</summary>
public sealed record SearchResultGroup(string Heading, string Name, IReadOnlyList<BaseItemViewModel> Items);
