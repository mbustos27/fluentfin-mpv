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

	// ---- review: a search dropped (Enter, a page change, a sign-out) never shows

	private static BaseItemDtoQueryResult Found(string name) => Result(Item(name, BaseItemDto_Type.Movie));

	private static async Task WaitFor(Func<bool> condition)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while (!condition())
		{
			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "timed out");
			await Task.Delay(10);
		}
	}

	[Fact]
	public async Task ALateAnswer_AfterEnter_IsNeverShown()
	{
		// type "matrix", Enter while its search is still running: the results page opened, then the late answer filled the
		// list and could pop it open over the page
		var typed = new System.Reactive.Subjects.Subject<string>();
		var drop = new System.Reactive.Subjects.Subject<System.Reactive.Unit>();
		var slow = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var started = new System.Collections.Concurrent.ConcurrentQueue<string>();
		var updates = new System.Collections.Concurrent.ConcurrentQueue<SearchUpdate>();
		using var _ = SearchSuggestions.Pipeline(typed, System.Reactive.Linq.Observable.Never<string>(), q => { started.Enqueue(q); return slow.Task; }, TimeSpan.FromMilliseconds(10), drop)
			.Subscribe(updates.Enqueue);

		typed.OnNext("matrix");
		await WaitFor(() => started.Contains("matrix"));
		drop.OnNext(System.Reactive.Unit.Default);
		await WaitFor(() => !updates.IsEmpty);
		slow.SetResult(Found("The Matrix"));
		await Task.Delay(150);

		var update = Assert.Single(updates);
		Assert.True(update.IsDropped);
		Assert.Empty(update.Suggestions);
	}

	[Fact]
	public async Task EnterWithinThePause_StartsNoSearch()
	{
		var typed = new System.Reactive.Subjects.Subject<string>();
		var drop = new System.Reactive.Subjects.Subject<System.Reactive.Unit>();
		var started = new System.Collections.Concurrent.ConcurrentQueue<string>();
		var updates = new System.Collections.Concurrent.ConcurrentQueue<SearchUpdate>();
		using var _ = SearchSuggestions.Pipeline(typed, System.Reactive.Linq.Observable.Never<string>(), q => { started.Enqueue(q); return Task.FromResult<BaseItemDtoQueryResult?>(Found(q)); }, TimeSpan.FromMilliseconds(200), drop)
			.Subscribe(updates.Enqueue);

		typed.OnNext("matrix");
		drop.OnNext(System.Reactive.Unit.Default);
		await Task.Delay(400);

		Assert.Empty(started);
		Assert.True(Assert.Single(updates).IsDropped);
	}

	[Fact]
	public async Task AfterADrop_TheSameQueryTypedAgain_SearchesAgain()
	{
		// the remembered last query is forgotten: coming back and typing "matrix" again lists it again
		var typed = new System.Reactive.Subjects.Subject<string>();
		var drop = new System.Reactive.Subjects.Subject<System.Reactive.Unit>();
		var started = new System.Collections.Concurrent.ConcurrentQueue<string>();
		var updates = new System.Collections.Concurrent.ConcurrentQueue<SearchUpdate>();
		using var _ = SearchSuggestions.Pipeline(typed, System.Reactive.Linq.Observable.Never<string>(), q => { started.Enqueue(q); return Task.FromResult<BaseItemDtoQueryResult?>(Found(q)); }, TimeSpan.FromMilliseconds(10), drop)
			.Subscribe(updates.Enqueue);

		typed.OnNext("matrix");
		await WaitFor(() => started.Count == 1 && updates.Count == 1);
		drop.OnNext(System.Reactive.Unit.Default);
		await WaitFor(() => updates.Count == 2);
		typed.OnNext("matrix");
		await WaitFor(() => started.Count == 2 && updates.Count == 3);

		Assert.Equal(["matrix", "matrix"], started);
		Assert.Equal("matrix", updates.Last().Query);
		Assert.False(updates.Last().IsDropped);
	}

	[Fact]
	public void TheBox_DropsItsSearch_OnEnter_OnAPageChange_AndOnASignOut()
	{
		var behavior = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Behaviors/GlobalSearchBoxBehavior.cs"));

		Assert.Contains("TimeSpan.FromMilliseconds(500), _drop)", behavior, StringComparison.Ordinal);
		// Enter on a row and on the text: both drop it (the retry row searches again instead)
		var submitted = behavior[behavior.IndexOf("private void AssociatedObject_QuerySubmitted", StringComparison.Ordinal)..];
		submitted = submitted[..submitted.IndexOf("private void Drop()", StringComparison.Ordinal)];
		Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(submitted, @"Drop\(\);").Count);
		// the title bar's reset: another page, or the user signed out (TitleBarAccountTests)
		Assert.Contains("_titleBar.SearchReset += OnSearchReset;", behavior, StringComparison.Ordinal);
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

	// ---- the results page (Nixie 1.5: ranked on the PC; SearchRankingTests has the ranking itself)

	[Fact]
	public async Task TheResultsPage_SearchesEachKind_AndOrdersTheGroupsByTheirBestMatch()
	{
		var client = new Mock<IJellyfinClient>();
		void Answer(BaseItemKind kind, BaseItemDtoQueryResult result) =>
			client.Setup(x => x.Search("quiet", It.Is<IReadOnlyList<BaseItemKind>>(k => k.Single() == kind), 100)).ReturnsAsync(result);
		Answer(BaseItemKind.Movie, Result(Item("A Quiet Night", BaseItemDto_Type.Movie)));
		Answer(BaseItemKind.Series, Result(Item("Quiet", BaseItemDto_Type.Series)));
		// the server's capped total (12.x: three times the limit) is never shown as a count of matches
		Answer(BaseItemKind.Episode, new BaseItemDtoQueryResult { Items = [.. Enumerable.Range(1, 100).Select(i => Item($"The Quiet {i}", BaseItemDto_Type.Episode))], TotalRecordCount = 295 });
		Answer(BaseItemKind.BoxSet, Result());
		var vm = new SearchResultsViewModel(client.Object);

		await vm.OnNavigatedTo("  quiet ");

		Assert.Equal("Results for “quiet”", vm.Title);
		Assert.True(vm.Results.IsLoaded);
		// the show's whole title leads as the top result; then the groups by their best match (not Movies first)
		Assert.Equal("Quiet", vm.TopResult?.Dto.Name);
		Assert.Equal("Show", vm.TopResultKind);
		Assert.Equal(["Episodes (100)", "Movies (1)"], vm.CardGroups.Select(g => g.Heading));
		Assert.DoesNotContain(vm.CardGroups, g => g.Name == "Shows"); // its one show is the top result
		Assert.Equal(["All", "Shows", "Movies", "Episodes"], vm.Chips.Select(c => c.Label));
		Assert.Equal("All, 102 results", vm.Chips[0].Name);
		Assert.True(vm.Chips[0].IsSelected);
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
		Assert.Empty(vm.Chips);

		await vm.OnNavigatedTo("down");
		Assert.True(vm.Results.IsFailed);
		Assert.Empty(vm.CardGroups);

		await vm.OnNavigatedTo("   ");
		Assert.True(vm.Results.IsEmpty);
		client.Verify(x => x.Search(It.Is<string>(q => string.IsNullOrWhiteSpace(q)), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>()), Times.Never);
	}

	[Fact]
	public void TheSearch_AsksForWhatOpensAPage()
	{
		// the box suggests movies and shows; the results page asks for each kind on its own (people too, since the second
		// review: a person opens "With <name>"), with the titles the PC ranks on
		Assert.Equal([BaseItemKind.Movie, BaseItemKind.Series], JellyfinClient.SuggestionKinds);
		Assert.Equal([BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.BoxSet, BaseItemKind.Person], Search.SearchKinds.All.Select(k => k.ServerKind));
		Assert.Equal(typeof(PersonItemsViewModel), GlobalCommands.PageFor(BaseItemDto_Type.Person));
		Assert.Equal([ItemFields.OriginalTitle, ItemFields.SortName], JellyfinClient.SearchFields);
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

	[Fact]
	public void APersonsCard_HasNoPlayWatchedOrFavourite()
	{
		// review (m9): People are search results now; the card's hover strip offered "Mark as watched" on a person
		var card = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/BaseItemDtoCard.xaml.cs"));
		Assert.Contains("private bool IsPerson => Model?.Dto?.Type == Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Person;", card, StringComparison.Ordinal);
		Assert.Contains("PlayButton.Visibility = MarkWatchedButton.Visibility = AddToFavoriteButton.Visibility = person ? Visibility.Collapsed : Visibility.Visible;", card, StringComparison.Ordinal);
		var listed = card[card.IndexOf("internal IEnumerable<UIElement> VisibleActions()", StringComparison.Ordinal)..];
		Assert.True(listed.IndexOf("if (!IsPerson)", StringComparison.Ordinal) < listed.IndexOf("yield return MarkWatchedButton;", StringComparison.Ordinal));
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
