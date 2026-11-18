using System.Xml.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4 (interaction audit, item 2): the title bar's search made conventional. The arrows only move through the
/// rows (the box shows the row's name, nothing opens), Enter on a row opens it, Enter on the text opens a results page
/// grouped by type; Narrator reads each row's name, year and type (never the class name); the text clears after you leave.
/// </summary>
public sealed class SearchTests
{
	private static BaseItemDto Item(string name, BaseItemDto_Type type, int? year = null, string? series = null) =>
		new() { Id = Guid.NewGuid(), Name = name, Type = type, ProductionYear = year, SeriesName = series };

	private static BaseItemDtoQueryResult Result(params BaseItemDto[] items) => new() { Items = [.. items], TotalRecordCount = items.Length };

	// ---- the rows

	[Fact]
	public void ARow_ShowsTheItemsNameInTheBox_AndNarratorReadsNameYearAndType()
	{
		var row = Assert.IsType<SearchSuggestion>(Assert.Single(SearchSuggestions.For("quiet", Result(Item("Quiet Brightwater", BaseItemDto_Type.Series, 2019)))));

		// the AutoSuggestBox's TextMemberPath is "Name": what the box shows while the arrows are on the row
		Assert.Equal("Quiet Brightwater", row.Name);
		// the list names its rows from ToString: it was "Jellyfin.Sdk.Generated.Models.BaseItemDto" for every row
		Assert.Equal("Quiet Brightwater, 2019, show", row.ToString());
		Assert.DoesNotContain("Jellyfin.Sdk", row.ToString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(BaseItemDto_Type.Movie, 2010, null, "Inception, 2010, movie")]
	[InlineData(BaseItemDto_Type.Episode, null, "Quiet Brightwater", "Quiet Brightwater: Inception, episode")]
	[InlineData(BaseItemDto_Type.Series, null, null, "Inception, show")]
	public void SearchLabels_NameYearAndType(BaseItemDto_Type type, int? year, string? series, string label)
	{
		Assert.Equal(label, SearchLabels.For(Item("Inception", type, year, series)));
	}

	[Theory]
	[InlineData(BaseItemDto_Type.Series, "Show")]
	[InlineData(BaseItemDto_Type.Movie, "Movie")]
	[InlineData(BaseItemDto_Type.Episode, "Episode")]
	[InlineData(BaseItemDto_Type.BoxSet, "Collection")]
	[InlineData(BaseItemDto_Type.Folder, "")]
	public void TheKindUnderARow_IsAFriendlyName(BaseItemDto_Type type, string name)
	{
		// layout audit item 10: the raw kind was shown ("Series", "BoxSet")
		Assert.Equal(name, SearchLabels.KindTitle(type));
	}

	[Fact]
	public void ManyMatches_ShowSix_AndASeeAllRow()
	{
		var items = Enumerable.Range(1, 12).Select(i => Item($"Movie {i}", BaseItemDto_Type.Movie)).ToArray();

		var rows = SearchSuggestions.For("movie", Result(items));

		Assert.Equal(SearchSuggestions.Shown + 1, rows.Count);
		Assert.All(rows.Take(SearchSuggestions.Shown), r => Assert.IsType<SearchSuggestion>(r));
		var all = Assert.IsType<SearchSeeAll>(rows[^1]);
		Assert.Equal("movie", all.Name);
		Assert.Equal("See all 12 results for “movie”", all.ToString());
	}

	[Fact]
	public void TheMessageRows_KeepTheQueryInTheBox_AndReadAsTheirMessage()
	{
		var none = Assert.IsType<SearchMessage>(Assert.Single(SearchSuggestions.For("zzz", Result())));
		Assert.Equal("zzz", none.Name);
		Assert.False(none.CanRetry);
		Assert.StartsWith("No results for “zzz”.", none.ToString(), StringComparison.Ordinal);

		var failed = Assert.IsType<SearchMessage>(Assert.Single(SearchSuggestions.For("zzz", null)));
		Assert.Equal("zzz", failed.Name);
		Assert.True(failed.CanRetry);
		Assert.Contains("Choose this to try again.", failed.ToString(), StringComparison.Ordinal);
	}

	// ---- what Enter (or a click on a row) does

	[Fact]
	public void EnterOnARow_OpensIt()
	{
		var item = Item("Quiet Brightwater", BaseItemDto_Type.Series);

		var action = SearchSuggestions.OnSubmitted(new SearchSuggestion(item), "Quiet Brightwater");

		Assert.Same(item, Assert.IsType<SearchAction.Open>(action).Item);
	}

	[Theory]
	[InlineData("quiet", "quiet")]
	[InlineData("  quiet  ", "quiet")]
	public void EnterOnTheText_OpensTheResultsPage(string text, string query)
	{
		Assert.Equal(query, Assert.IsType<SearchAction.ShowResults>(SearchSuggestions.OnSubmitted(null, text)).Query);
	}

	[Fact]
	public void TheOtherRows_SeeAllRetryAndNoResults()
	{
		Assert.Equal("quiet", Assert.IsType<SearchAction.ShowResults>(SearchSuggestions.OnSubmitted(new SearchSeeAll("quiet", 40), "quiet")).Query);
		Assert.Equal("quiet", Assert.IsType<SearchAction.Retry>(SearchSuggestions.OnSubmitted(new SearchMessage("quiet", "t", "m", CanRetry: true), "quiet")).Query);
		Assert.IsType<SearchAction.Nothing>(SearchSuggestions.OnSubmitted(new SearchMessage("quiet", "t", "m", CanRetry: false), "quiet"));
		Assert.IsType<SearchAction.Nothing>(SearchSuggestions.OnSubmitted(null, "   "));
	}

	// ---- the results page

	[Fact]
	public void TheResults_AreGroupedMoviesShowsEpisodes_WithoutPeopleOrEmptyGroups()
	{
		var groups = SearchResults.Group(
		[
			Item("E1", BaseItemDto_Type.Episode),
			Item("S1", BaseItemDto_Type.Series),
			Item("Someone", BaseItemDto_Type.Person),
			Item("M1", BaseItemDto_Type.Movie),
			Item("E2", BaseItemDto_Type.Episode),
		]);

		Assert.Equal(["Movies", "Shows", "Episodes"], groups.Select(g => g.Title));
		Assert.Equal(["E1", "E2"], groups[2].Items.Select(i => i.Name));
		Assert.Equal("Episodes (2)", groups[2].Heading);
		Assert.Equal(["Movies"], SearchResults.Group([Item("M1", BaseItemDto_Type.Movie)]).Select(g => g.Title));
	}

	[Fact]
	public async Task TheResultsPage_SearchesItsQuery_AndShowsTheGroups()
	{
		var client = new Mock<IJellyfinClient>();
		// one request per type: a shared limit let 100 episodes crowd the movies and shows out (seen live)
		client.Setup(x => x.Search("quiet", It.Is<IReadOnlyList<BaseItemKind>>(k => k.Single() == BaseItemKind.Movie), 100)).ReturnsAsync(Result(Item("Quiet Night", BaseItemDto_Type.Movie)));
		client.Setup(x => x.Search("quiet", It.Is<IReadOnlyList<BaseItemKind>>(k => k.Single() == BaseItemKind.Series), 100)).ReturnsAsync(Result(Item("Quiet Brightwater", BaseItemDto_Type.Series)));
		client.Setup(x => x.Search("quiet", It.Is<IReadOnlyList<BaseItemKind>>(k => k.Single() == BaseItemKind.Episode), 100))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [.. Enumerable.Range(1, 100).Select(i => Item($"Quiet {i}", BaseItemDto_Type.Episode))], TotalRecordCount = 163 });
		var vm = new SearchResultsViewModel(client.Object);

		await vm.OnNavigatedTo("  quiet ");

		Assert.Equal("Results for “quiet”", vm.Title);
		Assert.True(vm.Results.IsLoaded);
		// the server's count when it has more than the page asked for
		Assert.Equal(["Movies (1)", "Shows (1)", "Episodes (100 of 163)"], vm.CardGroups.Select(g => g.Heading));
		Assert.Equal("Quiet Night", vm.CardGroups[0].Items.Single().Dto.Name);
	}

	[Fact]
	public async Task TheResultsPage_SaysNoResults_AndAFailedSearch()
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Search("zzz", It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>())).ReturnsAsync(Result());
		client.Setup(x => x.Search("down", It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		var vm = new SearchResultsViewModel(client.Object);

		await vm.OnNavigatedTo("zzz");
		Assert.True(vm.Results.IsEmpty);
		Assert.Equal("No results for “zzz”", vm.Results.Title);

		await vm.OnNavigatedTo("down");
		Assert.True(vm.Results.IsFailed);

		await vm.OnNavigatedTo("   ");
		Assert.True(vm.Results.IsEmpty);
		client.Verify(x => x.Search(It.Is<string>(q => string.IsNullOrWhiteSpace(q)), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>()), Times.Never);
	}

	[Fact]
	public void TheSearch_AsksForWhatOpensAPage()
	{
		// people have no page in Seaglass (a person row opened nothing); the box suggests movies and shows, the results page
		// adds the episodes, each type asked for on its own
		Assert.Equal([BaseItemKind.Movie, BaseItemKind.Series], JellyfinClient.SuggestionKinds);
		Assert.Equal([BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode], SearchResults.Kinds);
	}

	// ---- the wiring (read from the files, as SettingsAndRailXamlTests does)

	[Fact]
	public void TheBox_ShowsNames_IsBoundForClearing_AndTheArrowsOpenNothing()
	{
		XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
		var box = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Views/TitleBarControl.xaml")).Descendants(presentation + "AutoSuggestBox").Single();

		Assert.Equal("Name", (string?)box.Attribute("TextMemberPath"));
		Assert.Equal("{x:Bind ViewModel.SearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string?)box.Attribute("Text"));

		var behavior = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Behaviors/GlobalSearchBoxBehavior.cs"));
		// SuggestionChosen is raised while the arrows move: it must not open anything (it opened the item on one Down)
		Assert.DoesNotContain("SuggestionChosen", behavior, StringComparison.Ordinal);
		Assert.Contains("switch (SearchSuggestions.OnSubmitted(args.ChosenSuggestion, args.QueryText))", behavior, StringComparison.Ordinal);
		Assert.Contains("NavigateTo<SearchResultsViewModel>(results.Query)", behavior, StringComparison.Ordinal);

		Assert.Contains("Configure<SearchResultsViewModel, SearchResultsPage>();", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Services/PageService.cs")), StringComparison.Ordinal);
	}

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}
}
