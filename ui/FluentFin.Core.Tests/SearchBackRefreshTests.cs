using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Search;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (1.7.4's KNOWN-ISSUES line): search results shown by Back weren't asked again, so a title added on the server
/// showed only after R. When something may have changed since (stale), the search runs again behind the results: the same
/// titles keep their cards (and take the server's watch state), new ones show; a fresh Back asks nothing.
/// </summary>
public sealed class SearchBackRefreshTests
{
	private static BaseItemDto Film(string name, bool played = false) =>
		new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { Played = played } };

	// the server's films for any search of films (other kinds: none)
	private static Mock<IJellyfinClient> Server(Func<List<BaseItemDto>> films)
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>()))
			.ReturnsAsync((string _, IReadOnlyList<BaseItemKind> kinds, int _) =>
			{
				var found = kinds.Contains(BaseItemKind.Movie) ? films() : [];
				return new BaseItemDtoQueryResult { Items = found, TotalRecordCount = found.Count };
			});
		return client;
	}

	private static List<BaseItemViewModel> Cards(SearchResultsViewModel vm) =>
		[.. (vm.TopResult is { } top ? [top] : Array.Empty<BaseItemViewModel>()).Concat(vm.CardGroups.SelectMany(g => g.Items))];

	private static List<string?> Shown(SearchResultsViewModel vm) => [.. Cards(vm).Select(x => x.Dto?.Name)];

	[Fact]
	public async Task AStaleBack_SearchesAgainBehind_AndShowsATitleAddedSince()
	{
		var films = new List<BaseItemDto> { Film("Sunny Days"), Film("Sunny Nights") };
		var client = Server(() => [.. films]);
		var vm = new SearchResultsViewModel(client.Object);
		await vm.OnNavigatedTo("sunny");
		Assert.Equal(["Sunny Days", "Sunny Nights"], Shown(vm));

		films.Add(Film("Sunny Mornings")); // added on the server while the user was on a film's page
		await vm.Returned("sunny", stale: true);

		Assert.True(vm.Results.IsLoaded);
		Assert.Contains("Sunny Mornings", Shown(vm));
		Assert.Equal(3, Shown(vm).Count);
	}

	[Fact]
	public async Task AStaleBack_WithTheSameTitles_KeepsTheirCards_AndTheirNewWatchState()
	{
		var days = Film("Sunny Days");
		var nights = Film("Sunny Nights");
		var answer = new List<BaseItemDto> { days, nights };
		var client = Server(() => [.. answer]);
		var vm = new SearchResultsViewModel(client.Object);
		await vm.OnNavigatedTo("sunny");
		var cards = Cards(vm);
		var groups = vm.CardGroups;

		// the same films, one marked watched meanwhile (a new record from the server)
		answer = [new BaseItemDto { Id = days.Id, Name = days.Name, Type = days.Type, UserData = new UserItemDataDto { Played = true } }, nights];
		await vm.Returned("sunny", stale: true);

		Assert.Same(groups, vm.CardGroups); // the cards stay (the keyboard and the scroll with them)
		Assert.Equal(cards, Cards(vm));
		Assert.Contains(cards, c => c.Dto?.Id == days.Id && c.Dto.UserData?.Played == true);
	}

	[Fact]
	public async Task AFreshBack_AsksNothing_AndAFailedSearchBehind_KeepsTheResults()
	{
		var client = Server(() => [Film("Sunny Days")]);
		var vm = new SearchResultsViewModel(client.Object);
		await vm.OnNavigatedTo("sunny");
		client.Invocations.Clear();

		await vm.Returned("sunny", stale: false);
		Assert.Empty(client.Invocations);

		client.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		await vm.Returned("sunny", stale: true);
		Assert.True(vm.Results.IsLoaded);
		Assert.Equal(["Sunny Days"], Shown(vm));
	}

	[Fact]
	public void TheSameResults_AreTheSameTitlesInTheSamePlaces()
	{
		var a = Film("Sunny Days");
		var b = Film("Sunny Nights");
		RankedResults Ranked(params BaseItemDto[] items) =>
			new("sunny", null, [new RankedGroup(SearchKinds.All.First(k => k.Types.Contains(BaseItemDto_Type.Movie)), [.. items.Select((x, i) => new RankedItem(x, default, i))], [])]);

		Assert.True(SearchResultsViewModel.SameResults(Ranked(a, b), Ranked(a, b)));
		Assert.False(SearchResultsViewModel.SameResults(Ranked(a, b), Ranked(b, a)));
		Assert.False(SearchResultsViewModel.SameResults(Ranked(a), Ranked(a, b)));
	}
}
