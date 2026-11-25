using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.5 (search; the second review, 2026-11-20: people search for actors): what a person opens until the person
/// collections arrive: "With Ava Abara", the films and shows they are in, newest first, as a library-style poster grid
/// (<c>/Items?personIds=</c>). Loading, nothing found and a failure are the page's LoadState. 1.6.1 (BH-150-m3): the episodes
/// they are credited on too (a guest star is credited there only), one card per show, "in 3 episodes" (<see cref="Cards"/>).
/// </summary>
public partial class PersonItemsViewModel(IJellyfinClient jellyfinClient, ILogger<PersonItemsViewModel>? logger = null) : ObservableObject, INavigationAware, IKeptOnBack
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
		StopRest();
		await Results.RunAsync(() => Load(person?.Id, behind: false));
	}

	// Nixie 1.7.5 (review Minor 4): the rest of the episodes, read behind the first page's cards; stopped when the page is left
	private CancellationTokenSource? _rest;

	/// <summary>Tests: the reading of the rest of the episodes behind the cards shown (done when there is none).</summary>
	internal Task Rest { get; private set; } = Task.CompletedTask;

	private void StopRest()
	{
		_restStopped |= _rest is not null && (!Rest.IsCompleted || _readingBehind);
		_rest?.Cancel();
		_rest = null;
	}

	// the rest of the episodes was stopped before it was all read (the page was left): Back reads it
	private bool _restStopped;

	// a read behind Back is running (left again meanwhile: the next Back reads again)
	private bool _readingBehind;

	private async Task<LoadOutcome> Load(Guid? person, bool behind)
	{
		if (person is not { } id)
		{
			return LoadOutcome.Empty("Nothing to show", "Choose a person in the search results.");
		}

		// 1.6.1 (the review): the films and shows, and the first EpisodeLimit episodes (one card per show), asked together
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var asked = JellyfinClient.GetPersonItems(id);
		var episodes = await JellyfinClient.GetPersonEpisodes(id, 0, EpisodeLimit);
		var result = await asked;
		// the films and shows are the page; without the episodes it shows them, but it can't say "nothing" without both
		if (result is null || (episodes is null && result.Items is not { Count: > 0 }))
		{
			return LoadOutcome.Failed($"Couldn't load the films and shows with {(Name.Length > 0 ? Name : "this person")}", LoadMessages.CheckServer());
		}

		// 1.7.5 (BH-161-q1): the rest of their episodes, a page at a time, up to MaxEpisodes (a long-running show of their own
		// filled the first 200, and their older guest roles were left out). Review Minor 4: the first page's cards show at once
		// and the rest is read behind them (a refresh behind Back reads it all before it shows anything new)
		var films = result.Items ?? [];
		var firstOnly = EpisodesComplete(episodes);
		_restStopped = false;
		if (behind || firstOnly)
		{
			// (re-review Minor: the read behind Back stops too when the page is left again; a stopped read keeps the page)
			var token = behind ? (_rest = new CancellationTokenSource()).Token : CancellationToken.None;
			_readingBehind = behind;
			try
			{
				var (all, complete, requests) = await RestOfEpisodes(id, episodes, token);
				var cards = await CardsOf(films, all, complete, token);
				token.ThrowIfCancellationRequested();
				Show(cards, behind);
				LogRead(films.Count, all.Count, complete, requests + 1, watch);
			}
			finally
			{
				_readingBehind = false;
			}
		}
		else
		{
			// the first page now ("in 3+ episodes" where some may be missing), the rest behind
			IReadOnlyList<BaseItemDto?> first = episodes?.Items ?? [];
			Show(Cards(NewestFirst(films, first), _ => true), behind: false);
			var rest = _rest = new CancellationTokenSource();
			Rest = ReadRestBehind(id, films, episodes, watch, rest.Token);
		}

		// 1.5 huntfix (BH-150-m3): it says what Nixie looked at (1.6.1: episodes too)
		return Items.Count == 0
			? LoadOutcome.Empty($"Nothing with {(Name.Length > 0 ? Name : "this person")}", EmptyDetail(LoadMessages.ServerName()))
			: LoadOutcome.Loaded;
	}

	private static bool EpisodesComplete(BaseItemDtoQueryResult? first) =>
		first is null || first.TotalRecordCount is not { } total || total <= (first.Items?.Count ?? 0);

	// Nixie 1.7.4: behind the cards Back showed, the same titles (and lines) keep their cards. 1.7.5: and the rest of the
	// episodes behind the first page's: the same shows keep their cards (their lines say their counts now)
	private void Show(IReadOnlyList<BaseItemViewModel> cards, bool behind)
	{
		if (Items.Count > 0 && cards.Select(x => x.Dto?.Id).SequenceEqual(Items.Select(x => x.Dto?.Id)))
		{
			if (!(behind && cards.Select(x => x.CardSubtitle).SequenceEqual(Items.Select(x => x.CardSubtitle)) && KeptCards.Refresh(Items, [.. cards.Select(x => x.Dto)])))
			{
				for (var i = 0; i < cards.Count; i++)
				{
					Items[i].CardSubtitle = cards[i].CardSubtitle;
				}
			}

			return;
		}

		Items = cards;
	}

	private async Task ReadRestBehind(Guid id, IReadOnlyList<BaseItemDto?> films, BaseItemDtoQueryResult? first, System.Diagnostics.Stopwatch watch, CancellationToken token)
	{
		try
		{
			var (all, complete, requests) = await RestOfEpisodes(id, first, token);
			var cards = await CardsOf(films, all, complete, token);
			token.ThrowIfCancellationRequested();
			Show(cards, behind: false);
			LogRead(films.Count, all.Count, complete, requests + 1, watch);
		}
		catch (OperationCanceledException)
		{
			// the page was left (or opened for someone else): nothing of it is wanted
		}
		catch (Exception ex)
		{
			logger?.LogWarning("With a person: the rest of the episodes couldn't be read ({Error}); the first page's stay", ex.GetType().Name);
		}
	}

	// the cards of every episode read; past the cut, the shows' own first years say which may have more (one request)
	private async Task<IReadOnlyList<BaseItemViewModel>> CardsOf(IReadOnlyList<BaseItemDto?> films, IReadOnlyList<BaseItemDto?> episodes, bool complete, CancellationToken token)
	{
		if (complete)
		{
			return Cards(NewestFirst(films, episodes), null);
		}

		var shows = episodes.Select(x => x?.SeriesId).OfType<Guid>().Distinct().ToList();
		IReadOnlyList<BaseItemDto>? found = null;
		try
		{
			found = shows.Count == 0 ? [] : await JellyfinClient.GetItemsByIds(shows, token);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// (unknown first years: every show may have more)
		}

		var firstYears = found?.Where(x => x.Id is not null).GroupBy(x => x.Id!.Value).ToDictionary(g => g.Key, g => g.First().ProductionYear);
		return Cards(NewestFirst(films, episodes), MayHaveMore(episodes, firstYears));
	}

	// rule 10: how long the page took to its titles (counts and a duration only)
	private void LogRead(int titles, int episodes, bool complete, int requests, System.Diagnostics.Stopwatch watch) =>
		logger?.LogInformation("With a person: {Titles} films and shows, {Episodes} episodes{More} in {Requests} requests, {Ms} ms", titles,
			episodes, complete ? "" : " (not all)", requests, watch.ElapsedMilliseconds);

	// ---- Nixie 1.7.4: Back shows the person's page as it was (IKeptOnBack) ----------------------------------------------------

	public bool CanKeep => Results.Status is LoadStatus.Loaded or LoadStatus.Empty;

	/// <summary>Back or Forward shows the page as it was; when something may have changed since, their titles are asked again
	/// behind it (the same titles keep their cards: a watch state shows on its card).</summary>
	public async Task Returned(object parameter, bool stale)
	{
		// 1.7.5: a page left before the rest of its episodes came reads them now, behind what it shows
		if ((stale || _restStopped) && parameter is BaseItemDto { Id: { } id })
		{
			await Results.RefreshBehindAsync(() => Load(id, behind: true));
		}
	}

	/// <summary>
	/// 1.6.1 (the review): how many of a person's episodes are asked, newest first: enough for a page of shows they are a guest
	/// in. A show's regular cast is credited on the show itself (its own card), so its hundreds of episodes are never needed.
	/// </summary>
	public const int EpisodeLimit = 200;

	/// <summary>1.7.5 (BH-161-q1): how many of a person's episodes are asked after the first <see cref="EpisodeLimit"/>, a page at a time.</summary>
	public const int EpisodePage = 500;

	/// <summary>
	/// 1.7.5 (BH-161-q1): the most episodes asked in all (the first page and four more): past it, the shows at the cut say
	/// "or more" (<see cref="MayHaveMore"/>). A talk show's host is credited on thousands.
	/// </summary>
	public const int MaxEpisodes = 2200;

	/// <summary>
	/// 1.7.5 (BH-161-q1): the episodes after the first page, a page at a time, until the server's total or <see cref="MaxEpisodes"/>;
	/// each once (a server that ignored where a page starts would send the first again: the reading stops there). A page that
	/// fails leaves what came, not all of it.
	/// </summary>
	private async Task<(IReadOnlyList<BaseItemDto?> Episodes, bool Complete, int Requests)> RestOfEpisodes(Guid id, BaseItemDtoQueryResult? first, CancellationToken token)
	{
		var all = new List<BaseItemDto?>(first?.Items ?? []);
		var ids = all.Select(x => x?.Id).OfType<Guid>().ToHashSet();
		var total = first?.TotalRecordCount;
		var requests = 0;
		var asked = all.Count;
		while (first is not null && total is { } count && asked < Math.Min(count, MaxEpisodes))
		{
			token.ThrowIfCancellationRequested(); // 1.7.5 (review Minor 4): no page more once the page is left
			var page = await JellyfinClient.GetPersonEpisodes(id, asked, Math.Min(EpisodePage, MaxEpisodes - asked));
			requests++;
			var items = page?.Items ?? [];
			var added = items.Where(x => x?.Id is { } episode && ids.Add(episode)).ToList();
			if (added.Count == 0)
			{
				break; // failed, empty, or the same episodes again
			}

			all.AddRange(added);
			asked += items.Count;
		}

		return (all, first is null || total is not { } server || server <= all.Count, requests);
	}

	/// <summary>
	/// 1.7.5 (BH-161-q3): with not every episode in, the shows that may have more. The server lists the episodes newest first,
	/// so every episode not read is from the cut's year (the last one read) or before: a show may have more when its own run
	/// began in that year or before (<paramref name="firstYears"/>, the shows' own years), or when that is not known. A show
	/// that began after the cut has all its episodes in, and says its count as it is. Review Minor 5: it went by the episodes
	/// read, and a show with episodes after the cut and before it, but none in its year, lost its "+".
	/// </summary>
	public static Func<Guid, bool> MayHaveMore(IReadOnlyList<BaseItemDto?> episodes, IReadOnlyDictionary<Guid, int?>? firstYears)
	{
		var cut = episodes.LastOrDefault(x => x is not null)?.ProductionYear;
		return series => cut is not { } year || firstYears is null || !firstYears.TryGetValue(series, out var first) || first is not { } began || began <= year;
	}

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
	public static IReadOnlyList<BaseItemViewModel> Cards(IEnumerable<BaseItemDto?> items, bool episodesComplete = true) =>
		Cards(items, episodesComplete ? null : _ => true);

	/// <summary>1.7.5 (BH-161-q3): the same, "in 3+ episodes" only on the shows <paramref name="mayHaveMore"/> names (null: every episode came).</summary>
	public static IReadOnlyList<BaseItemViewModel> Cards(IEnumerable<BaseItemDto?> items, Func<Guid, bool>? mayHaveMore)
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

		foreach (var (series, (card, episodes)) in guest)
		{
			card.CardSubtitle = mayHaveMore?.Invoke(series) == true ? $"in {episodes}+ episodes" : episodes == 1 ? "in 1 episode" : $"in {episodes} episodes";
		}

		return cards;
	}

	public Task OnNavigatedFrom()
	{
		StopRest(); // 1.7.5 (review Minor 4): nothing more is asked once the page is left
		return Task.CompletedTask;
	}
}
