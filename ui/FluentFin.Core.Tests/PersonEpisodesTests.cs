using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (BH-161-q1, q3): "With <name>" asked for a person's first 200 episodes only. A long-running show of their own
/// filled them, so their older guest roles were left out with nothing said, and once the 200 were reached every guest show
/// read "in N+ episodes", even shows whose episodes had all come. Now the rest come a page at a time up to
/// <see cref="PersonItemsViewModel.MaxEpisodes"/>, and only the shows at the cut say "or more".
/// </summary>
public sealed class PersonEpisodesTests
{
	private static readonly BaseItemDto Person = new() { Id = Guid.Parse("cccccccc-0000-0000-0000-000000000175"), Name = "Sunny Holm", Type = BaseItemDto_Type.Person };

	private static BaseItemDto Episode(int n, Guid series, string show, int year) =>
		new() { Id = Guid.NewGuid(), Name = $"Episode {n}", Type = BaseItemDto_Type.Episode, SeriesId = series, SeriesName = show, ProductionYear = year, SeriesPrimaryImageTag = "tag" };

	// the server: these episodes newest first (as it sorts them), answered a page at a time from where each page starts; every
	// page asked is kept
	private static Mock<IJellyfinClient> Server(List<BaseItemDto> episodes, List<(int Start, int Limit)> asked, Dictionary<Guid, int>? firstYears = null)
	{
		var client = new Mock<IJellyfinClient>();
		// (1.7.5: the shows' own first years, asked once when not every episode came)
		client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) =>
			(IReadOnlyList<BaseItemDto>)[.. ids.Where(id => firstYears?.ContainsKey(id) == true).Select(id => new BaseItemDto { Id = id, Type = BaseItemDto_Type.Series, ProductionYear = firstYears![id] })]);
		client.Setup(x => x.GetPersonItems(Person.Id!.Value)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		client.Setup(x => x.GetPersonEpisodes(Person.Id!.Value, It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync((Guid _, int start, int limit) =>
		{
			lock (asked)
			{
				asked.Add((start, limit));
			}

			return new BaseItemDtoQueryResult { Items = [.. episodes.Skip(start).Take(limit)], TotalRecordCount = episodes.Count };
		});
		return client;
	}

	private static string? Line(PersonItemsViewModel vm, string show) => vm.Items.Single(x => x.Dto.Name == show).CardSubtitle;

	[Fact]
	public async Task AShowOfTheirOwn_PastTheFirst200_LeavesNoGuestRoleOut()
	{
		var own = Guid.NewGuid();
		var oldGuest = Guid.NewGuid();
		var midGuest = Guid.NewGuid();
		// 300 episodes of their own show (2010 to 2024), then a guest role in 2005 and three in 1999
		List<BaseItemDto> episodes =
		[
			.. Enumerable.Range(0, 300).Select(i => Episode(i, own, "Their Own Show", 2024 - (i / 20))),
			Episode(1, midGuest, "Mid Guest", 2005),
			.. Enumerable.Range(0, 3).Select(i => Episode(i, oldGuest, "Old Guest", 1999)),
		];
		var asked = new List<(int Start, int Limit)>();
		var vm = new PersonItemsViewModel(Server(episodes, asked).Object);

		await vm.OnNavigatedTo(Person);
		await vm.Rest; // (1.7.5: the rest of the episodes, read behind the first page's cards)

		Assert.True(vm.Results.IsLoaded);
		Assert.Equal(["Their Own Show", "Mid Guest", "Old Guest"], vm.Items.Select(x => x.Dto.Name));
		Assert.Equal("in 300 episodes", Line(vm, "Their Own Show"));
		Assert.Equal("in 1 episode", Line(vm, "Mid Guest"));
		Assert.Equal("in 3 episodes", Line(vm, "Old Guest")); // every episode came: no "+"
		Assert.Equal([(0, PersonItemsViewModel.EpisodeLimit), (PersonItemsViewModel.EpisodeLimit, PersonItemsViewModel.EpisodePage)], asked);
	}

	[Fact]
	public async Task PastTheMost_OnlyTheShowsAtTheCut_SayOrMore()
	{
		var daily = Guid.NewGuid();
		var recent = Guid.NewGuid();
		var early = Guid.NewGuid();
		// a host: two guest episodes of a recent show (2025), 3,000 of their daily show (2024 back to 2000), and a guest role
		// in 1990 that the cut leaves out
		List<BaseItemDto> episodes =
		[
			Episode(1, recent, "Recent Guest", 2025),
			Episode(2, recent, "Recent Guest", 2025),
			.. Enumerable.Range(0, 3000).Select(i => Episode(i, daily, "The Daily", 2024 - (i / 125))),
			Episode(1, early, "Early Guest", 1990),
		];
		var asked = new List<(int Start, int Limit)>();
		// the shows' own first years: the daily show began in 2000, before the cut (2007); the recent one in 2025
		var vm = new PersonItemsViewModel(Server(episodes, asked, new() { [daily] = 2000, [recent] = 2025 }).Object);

		await vm.OnNavigatedTo(Person);
		await vm.Rest; // (1.7.5: the rest of the episodes, read behind the first page's cards)

		Assert.Equal(PersonItemsViewModel.MaxEpisodes, asked.Sum(x => x.Limit)); // no more than the most, in pages
		Assert.Equal(5, asked.Count);
		Assert.Equal("in 2 episodes", Line(vm, "Recent Guest")); // all came, before the cut: as it is
		Assert.Equal($"in {PersonItemsViewModel.MaxEpisodes - 2}+ episodes", Line(vm, "The Daily")); // at the cut: or more
		Assert.DoesNotContain(vm.Items, x => x.Dto.Name == "Early Guest");
	}

	[Fact]
	public async Task AServerThatSendsTheFirstPageAgain_IsNotCountedTwice()
	{
		var show = Guid.NewGuid();
		List<BaseItemDto> first = [.. Enumerable.Range(0, PersonItemsViewModel.EpisodeLimit).Select(i => Episode(i, show, "Same Show", 2020))];
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetPersonItems(Person.Id!.Value)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		// (it ignores where a page starts: the same 200 every time, of 900)
		client.Setup(x => x.GetPersonEpisodes(Person.Id!.Value, It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = first, TotalRecordCount = 900 });
		var vm = new PersonItemsViewModel(client.Object);

		await vm.OnNavigatedTo(Person);
		await vm.Rest; // (1.7.5: the rest of the episodes, read behind the first page's cards)

		Assert.Equal($"in {PersonItemsViewModel.EpisodeLimit}+ episodes", Assert.Single(vm.Items).CardSubtitle);
		client.Verify(x => x.GetPersonEpisodes(Person.Id!.Value, It.IsAny<int>(), It.IsAny<int>()), Times.Exactly(2));
	}

	[Fact]
	public async Task ALaterPageThatFails_ShowsWhatCame_AtTheCutOrMore()
	{
		var own = Guid.NewGuid();
		var guest = Guid.NewGuid();
		List<BaseItemDto> episodes =
		[
			Episode(1, guest, "Guest", 2025),
			.. Enumerable.Range(0, 400).Select(i => Episode(i, own, "Own", 2024 - (i / 100))),
		];
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetPersonItems(Person.Id!.Value)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		client.Setup(x => x.GetPersonEpisodes(Person.Id!.Value, It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync((Guid _, int start, int limit) =>
			start == 0 ? new BaseItemDtoQueryResult { Items = [.. episodes.Take(limit)], TotalRecordCount = episodes.Count } : null);
		client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<BaseItemDto>)[new BaseItemDto { Id = guest, ProductionYear = 2025 }, new BaseItemDto { Id = own, ProductionYear = 2010 }]);
		var vm = new PersonItemsViewModel(client.Object);

		await vm.OnNavigatedTo(Person);
		await vm.Rest; // (1.7.5: the rest of the episodes, read behind the first page's cards)

		Assert.True(vm.Results.IsLoaded);
		Assert.Equal("in 1 episode", Line(vm, "Guest"));
		Assert.Equal($"in {PersonItemsViewModel.EpisodeLimit - 1}+ episodes", Line(vm, "Own"));
	}

	/// <summary>The real client asks the next page from where it starts (startIndex), the first without one.</summary>
	[Fact]
	public async Task TheClient_AsksTheNextPage_FromWhereItStarts()
	{
		using var server = new FakeJellyfin();
		server.Start();
		server.Respond("/Items", 200, """{"Items":[],"TotalRecordCount":0}""");
		var client = new FluentFin.Core.Services.JellyfinClient(new CoreListLogger<FluentFin.Core.Services.JellyfinClient>(), Mock.Of<IObserver<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>>(),
			Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<FluentFin.Core.Settings.ISettings>(), TestHomeCache.Unused());
		await client.Initialize(server.BaseUrl, new AuthenticationResult { AccessToken = "token-175-person", ServerId = "fake-server", User = new UserDto { Id = Guid.NewGuid(), Name = "alice" } });
		try
		{
			var person = Guid.NewGuid();
			Assert.NotNull(await client.GetPersonEpisodes(person, 0, 200));
			Assert.NotNull(await client.GetPersonEpisodes(person, 200, 500));

			var asked = server.ApiRequests.Where(r => r.Path.Equals("/Items", StringComparison.OrdinalIgnoreCase)).ToList();
			string? Value(FakeRequest r, string key) => r.Query.AllKeys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) is { } k ? r.Query[k] : null;
			Assert.Equal(2, asked.Count);
			Assert.Null(Value(asked[0], "startIndex"));
			Assert.Equal("200", Value(asked[0], "limit"));
			Assert.Equal("200", Value(asked[1], "startIndex"));
			Assert.Equal("500", Value(asked[1], "limit"));
			// 1.7.5 (review Minor 4): the total is asked once, with the first page
			Assert.Equal("true", Value(asked[0], "enableTotalRecordCount")?.ToLowerInvariant());
			Assert.NotEqual("true", Value(asked[1], "enableTotalRecordCount")?.ToLowerInvariant());
		}
		finally
		{
			client.StopWebSocket();
		}
	}

	// 1.7.5 (review Minor 4): the first page's cards show at once; the rest is read behind them, and not once the page is left
	[Fact]
	public async Task TheFirstPageShowsAtOnce_TheRestBehindIt_AndNothingMoreOnceLeft()
	{
		var own = Guid.NewGuid();
		var guest = Guid.NewGuid();
		List<BaseItemDto> episodes =
		[
			.. Enumerable.Range(0, 300).Select(i => Episode(i, own, "Their Own Show", 2024 - (i / 20))),
			Episode(1, guest, "Old Guest", 1999),
		];
		var held = new TaskCompletionSource();
		var asked = new List<int>();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetPersonItems(Person.Id!.Value)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		client.Setup(x => x.GetPersonEpisodes(Person.Id!.Value, It.IsAny<int>(), It.IsAny<int>())).Returns(async (Guid _, int start, int limit) =>
		{
			lock (asked)
			{
				asked.Add(start);
			}

			if (start > 0)
			{
				await held.Task;
			}

			return new BaseItemDtoQueryResult { Items = [.. episodes.Skip(start).Take(limit)], TotalRecordCount = episodes.Count };
		});
		var vm = new PersonItemsViewModel(client.Object);

		await vm.OnNavigatedTo(Person).WaitAsync(TimeSpan.FromSeconds(10)); // not held by the rest
		Assert.True(vm.Results.IsLoaded);
		Assert.Equal("in 200+ episodes", Line(vm, "Their Own Show")); // the first page's, "or more" until the rest is in
		var card = vm.Items[0];

		held.SetResult();
		await vm.Rest.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(["Their Own Show", "Old Guest"], vm.Items.Select(x => x.Dto.Name));
		Assert.Equal("in 300 episodes", Line(vm, "Their Own Show"));

		// left while the rest is read: nothing more is asked
		lock (asked)
		{
			asked.Clear();
		}

		var again = new TaskCompletionSource();
		held = again;
		await vm.OnNavigatedTo(Person);
		await vm.OnNavigatedFrom();
		again.SetResult();
		await vm.Rest.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal([0, PersonItemsViewModel.EpisodeLimit], asked); // the page in flight, then none
	}

	// 1.7.5 (review Minor 5): past the cut, a show with episodes after the cut and before it, but none in its year, says "or more"
	[Fact]
	public void PastTheCut_AShowThatBeganBeforeIt_MayHaveMore_EvenWithNoEpisodeInItsYear()
	{
		var spanning = Guid.NewGuid();
		var late = Guid.NewGuid();
		BaseItemDto?[] read = [Episode(1, spanning, "Spanning", 2020), Episode(2, late, "Late", 2015), Episode(3, Guid.NewGuid(), "At the cut", 2010)];
		var more = PersonItemsViewModel.MayHaveMore(read, new Dictionary<Guid, int?> { [spanning] = 2001, [late] = 2014 });

		Assert.True(more(spanning));   // its 2001 episodes are past the cut (2010)
		Assert.False(more(late));      // began in 2014: every episode of it came
		Assert.True(more(Guid.NewGuid())); // not known: may have more
		Assert.True(PersonItemsViewModel.MayHaveMore(read, null)(late)); // the shows' years not known at all
	}

	// 1.7.5 (re-review Minor): the read behind a stale Back stops when the page is left again
	[Fact]
	public async Task ARefreshBehindBack_StopsWhenThePageIsLeftAgain()
	{
		var own = Guid.NewGuid();
		List<BaseItemDto> episodes = [.. Enumerable.Range(0, 1200).Select(i => Episode(i, own, "Their Own Show", 2024 - (i / 60)))];
		TaskCompletionSource? held = null;
		var asked = new List<int>();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetPersonItems(Person.Id!.Value)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		client.Setup(x => x.GetPersonEpisodes(Person.Id!.Value, It.IsAny<int>(), It.IsAny<int>())).Returns(async (Guid _, int start, int limit) =>
		{
			lock (asked)
			{
				asked.Add(start);
			}

			if (start > 0 && held is { } wait)
			{
				await wait.Task;
			}

			return new BaseItemDtoQueryResult { Items = [.. episodes.Skip(start).Take(limit)], TotalRecordCount = episodes.Count };
		});
		var vm = new PersonItemsViewModel(client.Object);
		await vm.OnNavigatedTo(Person);
		await vm.Rest;
		await vm.OnNavigatedFrom();

		held = new TaskCompletionSource();
		lock (asked)
		{
			asked.Clear();
		}

		var back = ((FluentFin.Contracts.ViewModels.IKeptOnBack)vm).Returned(Person, stale: true);
		await Task.Delay(300);
		await vm.OnNavigatedFrom(); // left again while it reads behind
		held.SetResult();
		await back.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal([0, PersonItemsViewModel.EpisodeLimit], asked); // the page in flight, then none
		Assert.Equal("in 1200 episodes", Line(vm, "Their Own Show")); // the page as it was
	}
}