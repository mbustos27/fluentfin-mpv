using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Search;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.6.1 (bug hunt BH-160-M2): "Two collections that would read the same keep their server names", everywhere a
/// collection's name is tidied, not on the Collections page only. "Winter Films" and "Winter Films (4K)" ("(4K)" is an end
/// tag) read "Winter Films" twice in search, on the Collection page's title, on cards and in "Part of the collection". One
/// rule (<see cref="CollectionNames.Display"/> against the box sets the client read, <see cref="CollectionNames.RememberBoxSets"/>),
/// and a test per place.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class CollectionNameRuleTests : IDisposable
{
	private readonly BaseItemDto _winter = BoxSet("Winter Films");
	private readonly BaseItemDto _winter4K = BoxSet("Winter Films (4K)");
	private readonly BaseItemDto _marvel = BoxSet("[HUB] Marvel");

	private static BaseItemDto BoxSet(string name) => new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.BoxSet, ChildCount = 4 };

	public CollectionNameRuleTests() => CollectionNames.ForgetBoxSets();

	public void Dispose()
	{
		CollectionNames.Tidy = true;
		CollectionNames.Owner = FavouritesStore.ProfileKey;
		CollectionNames.ForgetBoxSets();
		CollectionNames.RememberLibraries([]);
	}

	private void ServerHasAll() => CollectionNames.RememberBoxSets([_winter, _winter4K, _marvel]);

	[Fact]
	public void TheRule_TwoThatWouldReadTheSame_KeepTheServersNames_TheRestAreTidied()
	{
		// nothing read yet (review of 1.6.1, F2): the server's names, since another set could read the same
		Assert.Equal("Winter Films (4K)", CollectionNames.Display(_winter4K));
		Assert.Equal("[HUB] Marvel", CollectionNames.Display(_marvel));

		ServerHasAll();

		Assert.Equal("Winter Films", CollectionNames.Display(_winter));
		Assert.Equal("Winter Films (4K)", CollectionNames.Display(_winter4K));
		Assert.Equal("Marvel", CollectionNames.Display(_marvel));
		Assert.Equal("Shown on the server as “[HUB] Marvel”", CollectionNames.ServerNameNote(_marvel));
		Assert.Equal("", CollectionNames.ServerNameNote(_winter4K));
		// two that only tidy to the same: both stay
		CollectionNames.RememberBoxSets([BoxSet("[HUB] Bond"), BoxSet("Bond (Box Set)")]);
		Assert.Equal("[HUB] Bond", CollectionNames.Display(BoxSet("[HUB] Bond")));
		Assert.Equal("Bond (Box Set)", CollectionNames.Display(BoxSet("Bond (Box Set)")));
		// the setting off: the server's names, as always
		CollectionNames.Tidy = false;
		Assert.Equal("[HUB] Marvel", CollectionNames.Display(_marvel));
	}

	[Fact]
	public void TheListReadLast_IsTheOneThatCounts_AndLibrariesKeepTheirOwnRule()
	{
		ServerHasAll();
		CollectionNames.RememberBoxSets([_winter4K]);
		Assert.Equal("Winter Films", CollectionNames.Display(_winter4K));
		Assert.True(CollectionNames.BoxSetsKnown);

		// a library named like a box set is no box set: the two rules keep their own lists
		CollectionNames.RememberLibraries(["Winter Films"]);
		try
		{
			Assert.Equal("Winter Films", CollectionNames.Display(_winter4K));
			Assert.Equal("Movies (4K)", CollectionNames.DisplayLibrary("Movies (4K)"));
		}
		finally
		{
			CollectionNames.RememberLibraries([]);
		}
	}

	// ---- the places

	[Fact]
	public async Task Search_TheTopResultAndTheCards_SayBothServerNames_EvenBeforeTheListIsRead()
	{
		// the hunt's repro: search "Winter Films" on a server with both; nothing has read the box sets yet
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>()))
			.ReturnsAsync((string _, IReadOnlyList<BaseItemKind> kinds, int _) => new BaseItemDtoQueryResult
			{
				Items = kinds.Contains(BaseItemKind.BoxSet) ? [_winter, _winter4K] : [],
			});
		var vm = new SearchResultsViewModel(client.Object);
		SearchPageMemory.Forget("Winter Films");

		await vm.OnNavigatedTo("Winter Films");

		var shown = new[] { vm.TopResultTitle }.Concat(vm.CardGroups.SelectMany(g => g.Items).Select(i => ItemLabels.CardName(i).Split(", ")[0])).ToList();
		Assert.Equal(["Winter Films", "Winter Films (4K)"], shown.Order());
	}

	[Fact]
	public void Search_TheTopResult_SaysTheServersName()
	{
		ServerHasAll();

		Assert.Equal("Winter Films (4K)", SearchResultsText.Describe(new RankedItem(_winter4K, new SearchMatch(SearchTier.Whole, 1), 0)).Title);
		Assert.Equal("Marvel", SearchResultsText.Describe(new RankedItem(_marvel, new SearchMatch(SearchTier.Whole, 1), 0)).Title);
	}

	[Fact]
	public async Task TheCollectionPage_ItsTitle_IsTheNameItsTileSaid()
	{
		ServerHasAll();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(_winter4K.Id!.Value)).ReturnsAsync(_winter4K);
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		var vm = new CollectionViewModel(client.Object, commands);

		await vm.OnNavigatedTo(_winter4K);

		Assert.Equal("Winter Films (4K)", vm.Title);
		Assert.Equal("Winter Films (4K)", new ServerCollection(_winter4K).Title);
		// the Collections page's tiles: the same names
		Assert.Equal(["Winter Films", "Winter Films (4K)", "Marvel"], CollectionNames.DisplayAll([_winter, _winter4K, _marvel]));
	}

	[Fact]
	public void Cards_SayTheServersName()
	{
		ServerHasAll();

		Assert.Equal("Winter Films (4K)", ItemLabels.CardName(BaseItemViewModel.FromDto(_winter4K)).Split(", ")[0]);
		Assert.Equal("Winter Films", ItemLabels.CardName(BaseItemViewModel.FromDto(_winter)).Split(", ")[0]);
		Assert.Equal("Marvel", ItemLabels.CardName(BaseItemViewModel.FromDto(_marvel)).Split(", ")[0]);
	}

	[Fact]
	public void PartOfTheCollection_SaysTheServersName()
	{
		ServerHasAll();

		Assert.Equal("Winter Films (4K)", ItemMeta.PartOfShown(_winter4K));
		Assert.Equal("Part of the collection Winter Films (4K)", ItemMeta.PartOfName(_winter4K));
		Assert.Equal("Winter Films and Winter Films (4K)", ItemMeta.PartOfText([_winter, _winter4K]));
		Assert.Equal("Part of the collection Marvel", ItemMeta.PartOfName(_marvel));
	}

	/// <summary>The client's read of the box sets is the list the names stay unique against.</summary>
	[Fact]
	public async Task TheClientsReadOfTheBoxSets_IsRemembered()
	{
		using var server = new FakeJellyfin();
		server.Start();
		server.Respond("/Items", 200, $$"""
			{"Items":[{"Id":"{{_winter.Id:N}}","Name":"Winter Films","Type":"BoxSet"},{"Id":"{{_winter4K.Id:N}}","Name":"Winter Films (4K)","Type":"BoxSet"}],"TotalRecordCount":2}
			""");
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		await client.Initialize(server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-collection-names-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000161"), Name = "alice" },
		});
		Assert.False(CollectionNames.BoxSetsKnown);
		Assert.Equal("Winter Films (4K)", CollectionNames.Display(_winter4K));

		var read = await client.GetBoxSets();

		Assert.Equal(2, read?.Items?.Count);
		Assert.Equal("Winter Films (4K)", CollectionNames.Display(_winter4K));
	}

	// ---- review of 1.6.1 (F2): before the list is read, and another user's list

	[Fact]
	public void BeforeTheListIsRead_ANameThatWouldBeTidied_KeepsTheServers_InEveryPlace()
	{
		// a first start with a big library: a film's "Part of" and the Collection page come before any list
		Assert.False(CollectionNames.BoxSetsKnown);
		Assert.Equal("Winter Films", CollectionNames.Display(_winter));
		Assert.Equal("Winter Films (4K)", CollectionNames.Display(_winter4K));
		Assert.Equal("Part of the collection Winter Films (4K)", ItemMeta.PartOfName(_winter4K));
		Assert.Equal("Winter Films (4K)", new ServerCollection(_winter4K).Title);
		Assert.Equal("Winter Films (4K)", ItemLabels.CardName(BaseItemViewModel.FromDto(_winter4K)).Split(", ")[0]);
		Assert.Equal("", CollectionNames.ServerNameNote(_marvel));
		// a page of every box set (the Collections page) knows them all: it tidies, keeping the two that would read the same
		Assert.Equal(["Winter Films", "Winter Films (4K)", "Marvel"], CollectionNames.DisplayAll([_winter, _winter4K, _marvel]));
		// a search's answer before the list is read adds nothing: still the server's names
		CollectionNames.NoteBoxSets([_marvel]);
		Assert.False(CollectionNames.BoxSetsKnown);
		Assert.Equal("[HUB] Marvel", CollectionNames.Display(_marvel));
	}

	[Fact]
	public void AnotherUsersList_NeitherKeepsNorTidiesThisUsersNames()
	{
		// alice's lists, read before she signed out
		var owner = "server|alice";
		CollectionNames.Owner = () => owner;
		CollectionNames.RememberBoxSets([_winter, _winter4K, _marvel]);
		CollectionNames.RememberLibraries(["Movies", "[HUB] Movies"]);
		Assert.Equal("Marvel", CollectionNames.Display(_marvel));
		Assert.Equal("[HUB] Movies", CollectionNames.DisplayLibrary("[HUB] Movies"));

		// bob signs in: alice's box sets don't count for him (his names stay the server's until his list is read), and her
		// libraries don't keep his "[HUB] Movies" as it is
		owner = "server|bob";
		Assert.False(CollectionNames.BoxSetsKnown);
		Assert.Equal("[HUB] Marvel", CollectionNames.Display(_marvel));
		Assert.Equal("Movies", CollectionNames.DisplayLibrary("[HUB] Movies"));
		CollectionNames.RememberBoxSets([_marvel]);
		Assert.Equal("Marvel", CollectionNames.Display(_marvel));
		Assert.Equal("Winter Films", CollectionNames.Display(_winter4K)); // his list has no "Winter Films"
	}

	// ---- Nixie 1.6.1 (the merge review's R4): the switch renames the top bar's tabs at once

	[Fact]
	public void TidyCollectionNames_RenamesTheTopBarsTabs_AtOnce()
	{
		var kids = new BaseItemDto { Id = Guid.NewGuid(), Name = "[HUB] Kids Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var shows = new BaseItemDto { Id = Guid.NewGuid(), Name = "Shows", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Tvshows };
		CollectionNames.RememberLibraries([kids.Name, shows.Name]);
		var settings = new MemorySettings();
		var top = new TopNavigation(Mock.Of<INavigationServiceCore>(), settings, NullLogger<TopNavigation>.Instance) { Profile = () => "server|user" };
		top.Reset();
		top.SetLibraries([kids, shows]);
		if (!top.IsFavourite(kids.Id))
		{
			top.ToggleFavourite(kids.Id!.Value);
		}

		var page = new CollectionsSettingsViewModel(settings);
		string[] Row() => [.. top.Tabs.Select(t => t.Title)];
		string[] Menu() => [.. top.AllTabs.Single(t => t.Kind == TopTabKind.Libraries).Menu.Select(t => t.Title)];
		Assert.Contains("Kids Movies", Row());

		page.TidyNames.IsOn = false;

		Assert.False(CollectionNames.Tidy);
		Assert.Contains("[HUB] Kids Movies", Row());
		Assert.DoesNotContain("Kids Movies", Row());
		Assert.Contains("[HUB] Kids Movies", Menu());

		page.TidyNames.IsOn = true;

		Assert.Contains("Kids Movies", Row());
		Assert.Contains("Kids Movies", Menu());
		Assert.DoesNotContain("[HUB] Kids Movies", Menu());
		CollectionNames.RememberLibraries([]);
	}

	/// <summary>The settings file in memory (as LocalSettingsService keeps it: JSON nodes by key).</summary>
	private sealed class MemorySettings : ILocalSettingsService
	{
		private readonly Dictionary<string, JsonNode?> _values = [];

		public JsonNode? ReadSettingRaw(string key) => _values.GetValueOrDefault(key);

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is not null ? v.Deserialize<T>()! : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = JsonNode.Parse(JsonSerializer.Serialize(value));

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
