using System.Xml.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6 colpage: the Collection page and its cards (the top bar, library and UI review's LB1, C1 to C6): one meaning per card
/// chip (a place leads the caption, only where the order means something), no "6 films" three times, a long Play label cut
/// rather than pushing Shuffle off, and the header facts a theme's stage draws.
/// </summary>
public sealed class CollectionPage160Tests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static List<BaseItemDto> Films(int n, bool backdrops = false, bool posters = true) =>
	[
		.. Enumerable.Range(0, n).Select(i => new BaseItemDto
		{
			Id = Guid.NewGuid(),
			Name = $"Film {i + 1}",
			Type = BaseItemDto_Type.Movie,
			ProductionYear = 1980 + i,
			PremiereDate = new DateTimeOffset(1980 + i, 6, 1, 0, 0, 0, TimeSpan.Zero),
			BackdropImageTags = backdrops && i > 0 ? [$"b{i}"] : [],
			ImageTags = posters ? new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = $"p{i}" } } : new BaseItemDto_ImageTags(),
			UserData = new UserItemDataDto { Played = false },
		}),
	];

	private static CollectionViewModel Page(BaseItemDto set, List<BaseItemDto> items)
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(set.Id!.Value)).ReturnsAsync(set);
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = items });
		client.Setup(x => x.GetPlaylistItems(set.Id!.Value)).ReturnsAsync(new BaseItemDtoQueryResult { Items = items });
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		return new CollectionViewModel(client.Object, commands);
	}

	[Fact]
	public async Task Places_LeadTheCaption_OnlyWhereTheOrderMeansSomething()
	{
		// a franchise in release order: numbered
		var franchise = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Saga" };
		var vm = Page(franchise, Films(3));
		await vm.OnNavigatedTo(franchise);
		Assert.Equal(["1", "2", "3"], vm.Entries.Select(e => e.CaptionPrefix));

		// another order picked: none
		vm.SetOrder(CollectionOrder.Name);
		Assert.All(vm.Entries, e => Assert.Equal("", e.CaptionPrefix));
		vm.SetOrder(CollectionOrder.Collection);
		Assert.Equal("1", vm.Entries[0].CaptionPrefix); // and back

		// a box set its admin sorts by name: A to Z says nothing a title doesn't, so no places
		var byName = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Mixed", DisplayOrder = "SortName" };
		vm = Page(byName, Films(3));
		await vm.OnNavigatedTo(byName);
		Assert.All(vm.Entries, e => Assert.Equal("", e.CaptionPrefix));

		// a playlist (the user's order): numbered
		var list = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Playlist, Name = "Rainy Sunday" };
		vm = Page(list, Films(2));
		await vm.OnNavigatedTo(list);
		Assert.Equal(["1", "2"], vm.Entries.Select(e => e.CaptionPrefix));

		// a Nixie collection with no order of its own (a decade): the collections stream says so
		vm = Page(franchise, Films(2));
		vm.PositionsOverride = false;
		await vm.OnNavigatedTo(franchise);
		Assert.All(vm.Entries, e => Assert.Equal("", e.CaptionPrefix));
		Assert.All(vm.Header.Contents, line => Assert.Equal("", line.Number));
	}

	[Fact]
	public void TheRule_ForEachKindOfSet()
	{
		Assert.True(CollectionBrowse.PositionsMeanSomething(CollectionOrder.Collection, playlist: false, displayOrder: null));
		Assert.True(CollectionBrowse.PositionsMeanSomething(CollectionOrder.Collection, playlist: false, displayOrder: "PremiereDate"));
		Assert.False(CollectionBrowse.PositionsMeanSomething(CollectionOrder.Collection, playlist: false, displayOrder: "SortName"));
		Assert.True(CollectionBrowse.PositionsMeanSomething(CollectionOrder.Collection, playlist: false, displayOrder: CollectionBrowse.TrackOrder));
		Assert.True(CollectionBrowse.PositionsMeanSomething(CollectionOrder.Collection, playlist: true, displayOrder: "SortName"));
		foreach (var other in new[] { CollectionOrder.ReleaseDate, CollectionOrder.Name, CollectionOrder.DateAdded })
		{
			Assert.False(CollectionBrowse.PositionsMeanSomething(other, playlist: true, displayOrder: null));
		}
	}

	/// <summary>LB1 and C3: the art's top-left square is for counts only; a card carries at most the count and the watched check.</summary>
	[Fact]
	public void TheCards_CarryTheirPlaceInTheCaption_NotOnTheArt()
	{
		var page = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/CollectionPage.xaml"));
		Assert.DoesNotContain("CollectionPositionStyle", page, StringComparison.Ordinal);
		Assert.DoesNotContain("PositionText", page, StringComparison.Ordinal);
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/CollectionPage.xaml.cs"));
		Assert.Contains("lc:CardCaption.Prefix=\"{x:Bind CaptionPrefix}\"", page, StringComparison.Ordinal);
		Assert.DoesNotContain("CaptionPrefix", code, StringComparison.Ordinal); // the entry hands it over (CardCaption), not the page

		// the shared card: the prefix is a run of its caption, in the secondary colour; the overlay holds two marks, no more
		var card = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Controls/BaseItemDtoCard.xaml"));
		var prefix = card.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "CaptionPrefixRun");
		Assert.Equal("Run", prefix.Name.LocalName);
		Assert.Equal("CaptionText", (string?)prefix.Parent!.Attribute(X + "Name"));
		var overlay = card.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "ContentOverlay");
		Assert.Equal(["CountChip", "WatchedMark"], overlay.Elements().Where(e => e.Name.LocalName == "Border").Select(e => (string?)e.Attribute(X + "Name")));

		// Editorial's poster takes a place too
		Assert.Contains("ICaptionPrefixed", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Themes/Editorial/Controls/EditorialPoster.xaml.cs")), StringComparison.Ordinal);
	}

	/// <summary>LB1: Editorial's letter tags sit on a hairline over the poster, never on its art.</summary>
	[Fact]
	public void EditorialsLetterTags_AreOffTheArt()
	{
		var page = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes/Editorial/Views/EditorialLibraryPage.xaml"));
		var tag = page.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "LetterTag");
		var cell = tag.Parent!;
		var poster = cell.Elements().Single(e => e.Name.LocalName == "EditorialPoster");
		Assert.Null(tag.Attribute(XName.Get("Grid.Row")));        // row 0: the line over the poster
		Assert.Equal("1", (string?)poster.Attribute("Grid.Row")); // row 1: the poster
		Assert.Equal("22", (string?)cell.Descendants().First(e => e.Name.LocalName == "RowDefinition").Attribute("Height")); // every cell keeps the line's room
	}

	/// <summary>C2: no grid heading while it would repeat the summary; only when Hide watched hides something.</summary>
	[Fact]
	public void TheGridHeading_ShowsOnlyWhenSomethingIsHidden()
	{
		var films = Films(6);
		Assert.Equal("", CollectionBrowse.ShownText(6, films));
		Assert.Equal("5 of 6 films", CollectionBrowse.ShownText(5, films));
		var page = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Views/CollectionPage.xaml"));
		var heading = page.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "ShownText");
		Assert.Contains("Shown(ViewModel.ShownText)", (string?)heading.Attribute("Visibility"), StringComparison.Ordinal);
	}

	/// <summary>C6: a long title is cut in the Primary's label (Shuffle stays on the line at 1100); Narrator keeps it whole.</summary>
	[Fact]
	public async Task ALongPlayLabel_IsCut_ButNarratorHearsItWhole()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "The Rings" };
		var films = Films(6);
		films[0].Name = "The Lord of the Rings: The Fellowship of the Ring";
		var vm = Page(set, films);
		await vm.OnNavigatedTo(set);
		Assert.Equal("Play The Lord of the Rings: The Fellowship of the Ring, film 1 of 6", vm.PlayName);

		var page = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Views/CollectionPage.xaml"));
		var label = page.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "PlayLabelText");
		Assert.Equal("320", (string?)label.Attribute("MaxWidth"));
		Assert.Equal("CharacterEllipsis", (string?)label.Attribute("TextTrimming"));
		var button = page.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "PlayButton");
		Assert.Contains("ViewModel.PlayName", (string?)button.Attribute("AutomationProperties.Name"), StringComparison.Ordinal);
		Assert.Contains("ViewModel.PlayName", (string?)button.Attribute("ToolTipService.ToolTip"), StringComparison.Ordinal);
		// 320 + the glyph and the padding stay under the button's cap, with room for Shuffle and the toggles at 1100
		Assert.True(320 + 16 + 8 + 48 <= double.Parse((string)button.Attribute("MaxWidth")!, System.Globalization.CultureInfo.InvariantCulture));
	}

	/// <summary>C1 and C5: the art a header stage draws, the contents list and the marquee's words.</summary>
	[Fact]
	public void TheHeaderFacts_PickTheArt_AndWordTheStage()
	{
		// the set's own backdrop first
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Copper and Glass", BackdropImageTags = ["own"] };
		var films = Films(6, backdrops: true);
		var facts = CollectionBrowse.Header(set, films, "Collection", numbered: true);
		Assert.Same(set, facts.BackdropFrom);
		Assert.Empty(facts.Mosaic);
		Assert.Equal("Collection", facts.Eyebrow);
		Assert.Equal("6 films", facts.CountWords);
		Assert.Equal("6 FEATURES", facts.Features);
		Assert.Equal("NOW SHOWING: COPPER AND GLASS · 6 FEATURES", facts.Marquee);
		Assert.Equal(["01", "02", "03", "04", "05", "06"], facts.Contents.Select(c => c.Number));
		Assert.Equal("01  Film 1  1980", facts.Contents[0].Text);
		Assert.Equal("", facts.ContentsMore);

		// none of its own: its first member's that has one (in the set's order)
		set.BackdropImageTags = [];
		Assert.Same(films[1], CollectionBrowse.Header(set, films, "Collection", numbered: true).BackdropFrom);

		// no backdrop anywhere: up to four posters for the 2×2 mosaic
		var plain = Films(6);
		facts = CollectionBrowse.Header(set, plain, "Collection", numbered: true);
		Assert.Null(facts.BackdropFrom);
		Assert.Equal(plain.Take(4), facts.Mosaic);

		// a long set lists eight, then "and 4 more"; a one-film set reads "1 FEATURE"; a set of shows names its noun
		facts = CollectionBrowse.Header(set, Films(12), "Collection", numbered: true);
		Assert.Equal(8, facts.Contents.Count);
		Assert.Equal("and 4 more", facts.ContentsMore);
		Assert.Equal("1 FEATURE", CollectionBrowse.Header(set, Films(1), "Collection", numbered: true).Features);
		var shows = Films(2);
		shows.ForEach(s => s.Type = BaseItemDto_Type.Series);
		Assert.Equal("2 SHOWS", CollectionBrowse.Header(set, shows, "Collection", numbered: true).Features);

		// empty and null fields: nothing breaks, nothing is made up
		facts = CollectionBrowse.Header(null, [], "Playlist", numbered: true);
		Assert.Equal(("Playlist", "", "", "NOW SHOWING: "), (facts.Eyebrow, facts.CountWords, facts.Features, facts.Marquee));
		var nameless = Films(1, posters: false);
		nameless[0].Name = null;
		nameless[0].ProductionYear = null;
		nameless[0].PremiereDate = null;
		facts = CollectionBrowse.Header(set, nameless, "Collection", numbered: false);
		Assert.Equal(("", "", ""), (facts.Contents[0].Number, facts.Contents[0].Title, facts.Contents[0].Year));
		Assert.Empty(facts.Mosaic);

		// a Nixie collection fills the eyebrow and the why line
		facts = CollectionBrowse.Header(set, films, "Collection", numbered: true, eyebrow: "Made by Nixie · Franchise", why: "Your films in TMDb's The Mummy collection");
		Assert.Equal(("Made by Nixie · Franchise", "Your films in TMDb's The Mummy collection"), (facts.Eyebrow, facts.Why));
	}

	[Fact]
	public async Task TheViewModel_KeepsTheHeaderFacts_InTheSetsOwnOrder()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Saga" };
		var films = Films(3);
		films.Reverse(); // the server's order is not the set's (release date)
		var vm = Page(set, films);
		await vm.OnNavigatedTo(set);
		Assert.Equal(["Film 1", "Film 2", "Film 3"], vm.Header.Contents.Select(c => c.Title));
		vm.SetOrder(CollectionOrder.Name);
		vm.HideWatched = true;
		Assert.Equal(["01", "02", "03"], vm.Header.Contents.Select(c => c.Number)); // the stage keeps the set's own order
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
