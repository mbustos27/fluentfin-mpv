using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.5 (search; the second review, 2026-11-20: people search for actors): what a person opens until the person
/// collections arrive: "With Ava Abara", the films and shows they are in, newest first, as a library-style poster grid
/// (<c>/Items?personIds=</c>). Loading, nothing found and a failure are the page's LoadState. 1.6.1 (BH-150-m3): the episodes
/// they are credited on too (a guest star is credited there only), one card per show, "in 3 episodes" (<see cref="Cards"/>).
/// </summary>
public partial class PersonItemsViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title))]
	public partial string Name { get; private set; } = "";

	/// <summary>"With Ava Abara".</summary>
	public string Title => TitleFor(Name);

	[ObservableProperty]
	public partial IReadOnlyList<BaseItemViewModel> Items { get; private set; } = [];

	public LoadState Results { get; } = new("their films and shows");

	public static string TitleFor(string? name) => string.IsNullOrWhiteSpace(name) ? "With this person" : $"With {name.Trim()}";

	public async Task OnNavigatedTo(object parameter)
	{
		var person = parameter as BaseItemDto;
		Name = person?.Name?.Trim() ?? "";
		Items = [];
		await Results.RunAsync(async () =>
		{
			if (person?.Id is not { } id)
			{
				return LoadOutcome.Empty("Nothing to show", "Choose a person in the search results.");
			}

			// 1.6.1 (the review): the films and shows, and at most EpisodeLimit episodes (one card per show), asked together
			var asked = JellyfinClient.GetPersonItems(id);
			var episodes = await JellyfinClient.GetPersonEpisodes(id, EpisodeLimit);
			var result = await asked;
			// the films and shows are the page; without the episodes it shows them, but it can't say "nothing" without both
			if (result is null || (episodes is null && result.Items is not { Count: > 0 }))
			{
				return LoadOutcome.Failed($"Couldn't load the films and shows with {(Name.Length > 0 ? Name : "this person")}", LoadMessages.CheckServer());
			}

			var episodeItems = episodes?.Items ?? [];
			var complete = episodes?.TotalRecordCount is not { } total || total <= episodeItems.Count;
			Items = Cards(NewestFirst(result.Items ?? [], episodeItems), complete);
			// 1.5 huntfix (BH-150-m3): it says what Nixie looked at (1.6.1: episodes too)
			return Items.Count == 0
				? LoadOutcome.Empty($"Nothing with {(Name.Length > 0 ? Name : "this person")}", EmptyDetail(LoadMessages.ServerName()))
				: LoadOutcome.Loaded;
		});
	}

	/// <summary>
	/// 1.6.1 (the review): how many of a person's episodes are asked, newest first: enough for a page of shows they are a guest
	/// in. A show's regular cast is credited on the show itself (its own card), so its hundreds of episodes are never needed.
	/// </summary>
	public const int EpisodeLimit = 200;

	/// <summary>1.6.1: the films and shows and the episodes, each newest first from the server, in one newest-first list.</summary>
	public static IEnumerable<BaseItemDto?> NewestFirst(IReadOnlyList<BaseItemDto?> films, IReadOnlyList<BaseItemDto?> episodes)
	{
		static int Year(BaseItemDto? item) => item?.ProductionYear ?? int.MinValue;
		int i = 0, j = 0;
		while (i < films.Count || j < episodes.Count)
		{
			yield return j >= episodes.Count || (i < films.Count && Year(films[i]) >= Year(episodes[j])) ? films[i++] : episodes[j++];
		}
	}

	/// <summary>1.5 huntfix (BH-150-m3): "No films, shows or episodes on Home Server list them." (1.6.1: episodes are asked too)</summary>
	public static string EmptyDetail(string server) => $"No films, shows or episodes on {server} list them.";

	/// <summary>
	/// 1.6.1 (BH-150-m3): the page's cards from what the server lists for the person, in its order (newest first): each film and
	/// show as it is, and the episodes they are credited on as one card per show, at its newest episode's place, opening the
	/// show, its line "in 3 episodes" ("in 1 episode"). A show they are credited on as a whole keeps its own card, and its
	/// episodes add none. An episode with no show is dropped. <paramref name="episodesComplete"/> false: the server had more
	/// episodes than were asked (<see cref="EpisodeLimit"/>), so the counts are "in 3+ episodes".
	/// </summary>
	public static IReadOnlyList<BaseItemViewModel> Cards(IEnumerable<BaseItemDto?> items, bool episodesComplete = true)
	{
		var list = items.Where(i => i is not null).Select(i => i!).ToList();
		var credited = list.Where(i => i.Type == BaseItemDto_Type.Series && i.Id is not null).Select(i => i.Id!.Value).ToHashSet();
		var cards = new List<BaseItemViewModel>();
		var guest = new Dictionary<Guid, (BaseItemViewModel Card, int Episodes)>();
		foreach (var item in list)
		{
			if (item.Type != BaseItemDto_Type.Episode)
			{
				cards.Add(BaseItemViewModel.FromDto(item));
				continue;
			}

			if (item.SeriesId is not { } series || credited.Contains(series))
			{
				continue;
			}

			if (guest.TryGetValue(series, out var seen))
			{
				guest[series] = (seen.Card, seen.Episodes + 1);
				continue;
			}

			var show = BaseItemViewModel.FromDto(new BaseItemDto
			{
				Id = series,
				Type = BaseItemDto_Type.Series,
				Name = string.IsNullOrWhiteSpace(item.SeriesName) ? "A show" : item.SeriesName,
				ProductionYear = item.ProductionYear,
				ImageTags = item.SeriesPrimaryImageTag is { Length: > 0 } tag ? new BaseItemDto_ImageTags { AdditionalData = { ["Primary"] = tag } } : null,
				IsFolder = true,
			});
			guest[series] = (show, 1);
			cards.Add(show);
		}

		foreach (var (card, episodes) in guest.Values)
		{
			card.CardSubtitle = !episodesComplete ? $"in {episodes}+ episodes" : episodes == 1 ? "in 1 episode" : $"in {episodes} episodes";
		}

		return cards;
	}

	public Task OnNavigatedFrom() => Task.CompletedTask;
}
