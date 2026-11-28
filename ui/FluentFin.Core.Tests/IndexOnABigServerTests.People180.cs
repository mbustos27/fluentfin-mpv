using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-2, stream a): the people step of the index, back for "More from" and Spotlight, the top three billed only.
/// A read's titles are shown first and never wait for people; every library's people are read once, then only the new
/// titles' by id; what didn't come is asked again on its own within the hour; R during the people step reads the titles
/// again at once. (1.7.3 had no people pass; 1.7.1's read every library's people at every read.)
/// </summary>
public sealed partial class IndexOnABigServerTests
{
	// a people page of the 2,500 films (as the fixture's), after `wait`
	private void PeoplePages(Func<CancellationToken, Task> wait) =>
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool total, CancellationToken token) =>
			{
				Interlocked.Increment(ref _peoplePages);
				await wait(token);
				return new BaseItemDtoQueryResult
				{
					Items = [.. _films.Skip(start).Take(limit).Select(x => new BaseItemDto { Id = x.Id, Type = BaseItemDto_Type.Movie, People = [new BaseItemPerson { Name = $"Director {x.ProductionYear}", Type = BaseItemPerson_Type.Director }] })],
					TotalRecordCount = total ? _films.Count : null,
				};
			});

	[Fact]
	public async Task AFirstRead_ShowsItsTitlesFirst_ThenReadsEveryLibrarysPeopleOnce_AndNoReadAfterAsksThemAgain()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
		var release = new TaskCompletionSource();
		PeoplePages(token => release.Task.WaitAsync(token));
		using (var first = Index(time))
		{
			// rule 1: the titles come while the people are still on their way
			var shown = await first.GetAsync().WaitAsync(TimeSpan.FromSeconds(20));
			Assert.Equal(2500, shown!.Entries.Count);
			Assert.True(shown.PeopleDue);
			Assert.False(shown.HasPeople);
			Assert.All(shown.Entries, x => Assert.Empty(x.People));

			release.SetResult();
			await first.Settled.WaitAsync(TimeSpan.FromSeconds(20));
			var read = first.Current!;
			Assert.True(read.HasPeople);
			Assert.False(read.PeopleDue);
			Assert.Empty(read.PeopleToAsk);
			Assert.All(read.Entries, x => Assert.Equal($"Director {1950 + (int.Parse(x.Name[5..]) % 70)}", Assert.Single(x.People).Name));

			// R: the titles again, their people lent; no library's people asked again
			var people = _peoplePages;
			Assert.NotNull(await first.RefreshAsync());
			await first.Settled;
			Assert.Equal(people, _peoplePages);
			Assert.True(first.Current!.HasPeople);
			Assert.All(first.Current.Entries, x => Assert.Single(x.People));
		}

		var asked = _peoplePages;
		time.Now += LibraryIndex.FreshFor + TimeSpan.FromHours(1); // a restart a day on: the saved people at once, then the day's read
		using var day = Index(time);
		Assert.True((await day.GetAsync())!.HasPeople);
		await WaitForIndex(day, x => x.ReadAt == time.Now);
		await day.Settled;

		Assert.Equal(asked, _peoplePages); // 1.7.1 read every library's people at every read
		Assert.Equal(0, _peopleById); // nothing new
		Assert.All(day.Current!.Entries, x => Assert.Single(x.People));
	}

	[Fact]
	public async Task AnIndexSavedBefore18_ReadsOnlyItsPeople_InTheBackground()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);

		// as 1.7.4 left it: no people pass, no people
		var file = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single();
		var saved = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
		saved["HasPeople"] = false;
		foreach (var entry in saved["Entries"]!.AsArray())
		{
			entry!["People"] = new JsonArray();
		}

		await File.WriteAllTextAsync(file, saved.ToJsonString());
		var (titles, people) = (_pages, _peoplePages);
		time.Now += TimeSpan.FromHours(2); // past the hour, within the day

		using var index = Index(time);
		var shown = await index.GetAsync();
		Assert.False(shown!.HasPeople); // the saved one at once, as it is
		await WaitForIndex(index, x => x.HasPeople);
		await index.Settled;

		Assert.Equal(titles, _pages); // no title read again
		Assert.True(_peoplePages > people); // every library's people, once
		Assert.Equal(time.Now - TimeSpan.FromHours(2), index.Current!.ReadAt); // the index keeps its date
		Assert.All(index.Current.Entries, x => Assert.Single(x.People));
		await WaitForSaved(json => json.Contains("\"HasPeople\":true", StringComparison.Ordinal));
	}

	[Fact]
	public async Task NewTitlesWhosePeopleDidntCome_AreAskedAgainOnTheirOwn_AfterTheHour()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
		SavedSince(); // 1.9: a change is read as what changed
		await ReadAndSave(time);
		_check = "2501:another";
		_films.Add(Added(2500, time.Now + TimeSpan.FromHours(1), time.Now + TimeSpan.FromHours(1))); // a film added
		_peopleByIdFails = true;

		time.Now += TimeSpan.FromHours(2);
		using (var change = Index(time)) // the check says the libraries changed: the new film's people don't come
		{
			await change.GetAsync();
			await WaitForIndex(change, x => x.Entries.Count == 2501);
			await change.Settled;
			Assert.True(change.Current!.HasPeople); // the others' lent
			Assert.Equal([_films[2500].Id!.Value], change.Current.PeopleToAsk);
		}

		await WaitForSaved(json => json.Contains(_films[2500].Id!.Value.ToString(), StringComparison.Ordinal) && json.Contains("\"PeopleToAsk\":[\"", StringComparison.Ordinal));
		_peopleByIdFails = false;
		var (titles, people, byId) = (_pages, _peoplePages, _peopleByIdTitles);

		time.Now += TimeSpan.FromMinutes(30);
		using (var soon = Index(time)) // within the hour: not yet
		{
			await soon.GetAsync();
			await Task.Delay(100);
			await soon.Settled;
			Assert.Equal(byId, _peopleByIdTitles);
		}

		time.Now += TimeSpan.FromHours(1);
		using var later = Index(time);
		await later.GetAsync();
		await WaitForIndex(later, x => x.PeopleToAsk.Count == 0);
		await later.Settled;

		Assert.Equal((titles, people), (_pages, _peoplePages)); // no title and no library's people read again
		Assert.Equal(byId + 1, _peopleByIdTitles); // the one film
		Assert.Equal("Director 2000", Assert.Single(later.Current!.Entries.Single(x => x.Id == _films[2500].Id).People).Name);
	}

	[Fact]
	public async Task ThePeopleById_AreLean_OnTheWire_AndTheTitlesPagesBringThePlays()
	{
		using var server = new FakeJellyfin();
		server.Start();
		server.Respond("/Items", 200, """{"Items":[],"TotalRecordCount":0}""");
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		await client.Initialize(server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-index-180-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000001800"), Name = "alice" },
		});
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];

		Assert.NotNull(await client.GetPeopleByIds(ids, CancellationToken.None));
		await client.GetLibraryIndexPage(library, 0, 200, true, CancellationToken.None);

		var asked = server.ApiRequests.Where(r => r.Path.Equals("/Items", StringComparison.OrdinalIgnoreCase)).ToList();
		Assert.Equal(2, asked.Count);
		string? Q(FakeRequest r, string name) => r.Query.GetValues(name)?.FirstOrDefault();
		// the people by id: those titles, People only, no images, no user data, no count, no library
		var sent = (asked[0].Query.GetValues("ids") ?? []).SelectMany(x => x.Split(',')).Select(Guid.Parse).ToList();
		Assert.Equal(ids.Order(), sent.Order());
		Assert.Equal("People", Q(asked[0], "fields"));
		Assert.Equal("false", Q(asked[0], "enableImages"), ignoreCase: true);
		Assert.Equal("false", Q(asked[0], "enableUserData"), ignoreCase: true);
		Assert.Equal("false", Q(asked[0], "enableTotalRecordCount"), ignoreCase: true);
		Assert.Null(Q(asked[0], "parentId"));
		// the titles' pages keep the user data (the play count and last play come with them: no request more)
		Assert.NotEqual("false", Q(asked[1], "enableUserData")?.ToLowerInvariant());
	}

	[Fact]
	public async Task RDuringThePeopleStep_ReadsTheTitlesAgainAtOnce()
	{
		var release = new TaskCompletionSource();
		PeoplePages(token => release.Task.WaitAsync(token));
		using var index = Index(wholeAnyTime: true); // 1.9: R reads every title again, as before (the day's limit: IndexOnABigServerTests.Load190.cs)
		Assert.NotNull(await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(20))); // the titles; the people held
		var titles = _pages;

		var again = await index.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(20)); // R while the people step waits

		Assert.NotNull(again);
		Assert.True(_pages > titles); // the titles read again at once, not after the people
		release.SetResult();
		await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));
		Assert.True(index.Current!.HasPeople);
		Assert.All(index.Current.Entries, x => Assert.Single(x.People));
	}

	// 1.8 review: an index whose people step keeps what came at every page (tests: no minute's wait)
	private LibraryIndex KeepingIndex(TimeProvider time, bool wholeAnyTime = false) =>
		new(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), time) { PeopleKeptEvery = TimeSpan.Zero, WholeReadEvery = wholeAnyTime ? TimeSpan.Zero : LibraryIndex.FreshFor };

	// the people pages of the 2,500 films; those from `from` on wait for `release` (or the read's end)
	private void PeoplePagesHeldFrom(int from, Task release) =>
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool total, CancellationToken token) =>
			{
				Interlocked.Increment(ref _peoplePages);
				if (start >= from)
				{
					await release.WaitAsync(token);
				}

				return new BaseItemDtoQueryResult
				{
					Items = [.. _films.Skip(start).Take(limit).Select(x => new BaseItemDto { Id = x.Id, Type = BaseItemDto_Type.Movie, People = [new BaseItemPerson { Name = $"Director {x.ProductionYear}", Type = BaseItemPerson_Type.Director }] })],
					TotalRecordCount = total ? _films.Count : null,
				};
			});

	private static int WithPeople(IndexSnapshot index) => index.Entries.Count(x => x.People.Length > 0);

	// review (Major): on the server the owner uses the people of every library took 682 s; an app closed before the end
	// started them again from zero. What came is kept, and the next start asks only the rest, by id
	[Fact]
	public async Task APeopleStepClosedHalfway_KeepsWhatCame_AndTheNextStartAsksOnlyTheRest()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
		PeoplePagesHeldFrom(700, new TaskCompletionSource().Task); // never: the app closes first
		var first = KeepingIndex(time);
		Assert.NotNull(await first.GetAsync());
		await WaitForIndex(first, x => WithPeople(x) >= 450); // kept as they came
		first.Dispose(); // closed
		await first.Settled.WaitAsync(TimeSpan.FromSeconds(20));

		await WaitForSaved(json => json.Contains("\"PeopleToAsk\":[\"", StringComparison.Ordinal));
		var (pages, came) = (_peoplePages, 0);
		time.Now += TimeSpan.FromHours(2);
		using var next = Index(time); // the next start
		var saved = await next.GetAsync();
		came = WithPeople(saved!);
		Assert.InRange(came, 450, 2499);
		Assert.Equal(2500 - came, saved!.PeopleToAsk.Count);
		await WaitForIndex(next, x => x.PeopleToAsk.Count == 0);
		await next.Settled;

		Assert.Equal(pages, _peoplePages); // no library's people again
		Assert.Equal(2500 - came, _peopleByIdTitles); // only the rest, by id
		Assert.Equal(2500, WithPeople(next.Current!));
	}

	[Fact]
	public async Task RDuringThePeopleStep_KeepsWhatCame_AndTheReadAfterAsksOnlyTheRest()
	{
		PeoplePagesHeldFrom(700, new TaskCompletionSource().Task);
		using var index = KeepingIndex(TimeProvider.System, wholeAnyTime: true);
		Assert.NotNull(await index.GetAsync());
		await WaitForIndex(index, x => WithPeople(x) >= 450);
		var pages = _peoplePages;
		var titlesHeld = new TaskCompletionSource();
		Pages(_ => titlesHeld.Task); // R's titles wait until the step cut short has kept what came

		var refreshing = index.RefreshAsync(); // R
		await WaitForIndex(index, x => x.PeopleToAsk.Count > 0);
		var came = WithPeople(index.Current!);
		titlesHeld.SetResult();
		Assert.NotNull(await refreshing.WaitAsync(TimeSpan.FromSeconds(20)));
		await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));

		Assert.Equal(pages, _peoplePages); // R's read asked no library's people
		Assert.Equal(2500 - came, _peopleByIdTitles); // the rest, by id
		Assert.Equal(2500, WithPeople(index.Current!));
	}

	// review (Minor): a sign-in to another server ended nothing: the step asked server B for A's titles, and a switch back to A
	// could save A as having people it never got
	[Fact]
	public async Task ASignInElsewhere_EndsThePeopleStep_AndASwitchBackClaimsNoPeopleItDidntGet()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
		SavedSince(); // 1.9: a change is read as what changed
		await ReadAndSave(time);
		var asked = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		_client.Setup(x => x.GetPeopleByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.Returns(async (IReadOnlyList<Guid> _, CancellationToken token) =>
			{
				asked.TrySetResult(token);
				await Task.Delay(Timeout.Infinite, token);
				return null;
			});
		_check = "2505:another";
		_films.AddRange(Enumerable.Range(2500, 5).Select(i => Added(i, time.Now + TimeSpan.FromHours(1), time.Now + TimeSpan.FromHours(1)))); // five films added
		time.Now += TimeSpan.FromHours(2);
		using var index = Index(time);
		await index.GetAsync();
		var token = await asked.Task.WaitAsync(TimeSpan.FromSeconds(20)); // the step asks the new films' people

		_homeCache.SignedIn("http://another-server", Guid.NewGuid(), "another", "bob"); // another server
		await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));
		Assert.True(token.IsCancellationRequested); // its request ended, not left asking the other server

		_homeCache.SignedIn("http://server", User, "fake", "alice"); // and back
		var back = await index.GetAsync();
		Assert.Equal(_films.Skip(2500).Select(x => x.Id!.Value).Order(), back!.PeopleToAsk.Order()); // still to ask: never claimed
		Assert.All(back.Entries.Where(x => back.PeopleToAsk.Contains(x.Id)), x => Assert.Empty(x.People));
	}

	// review (Minor, tests): the people alone, asked again within the hour while the app stays open (no restart, no check due)
	[Fact]
	public async Task MissingPeople_AreAskedAgainAfterTheHour_WhileTheAppStaysOpen()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
		SavedSince(); // 1.9: a change is read as what changed
		await ReadAndSave(time);
		_check = "2501:another";
		_films.Add(Added(2500, time.Now + TimeSpan.FromHours(1), time.Now + TimeSpan.FromHours(1)));
		_peopleByIdFails = true;
		time.Now += TimeSpan.FromHours(2);
		using var open = Index(time); // the app, open all along from here
		var published = 0;
		open.Changed += (_, _) => Interlocked.Increment(ref published);
		await open.GetAsync();
		await WaitForIndex(open, x => x.Entries.Count == 2501);
		await open.Settled;
		Assert.Single(open.Current!.PeopleToAsk);
		Assert.Equal(2, published); // the saved one, then the titles; a step of which nothing came rebuilt no page
		_peopleByIdFails = false;
		var byId = _peopleByIdTitles;

		time.Now += TimeSpan.FromMinutes(30); // a page visit within the hour: not yet
		await open.SavedAsync();
		await open.Settled;
		Assert.Equal(byId, _peopleByIdTitles);

		time.Now += TimeSpan.FromMinutes(31); // a page visit after it
		await open.SavedAsync();
		await WaitForIndex(open, x => x.PeopleToAsk.Count == 0);
		await open.Settled;
		Assert.Equal(byId + 1, _peopleByIdTitles);
		Assert.Equal("Director 2000", Assert.Single(open.Current!.Entries.Single(x => x.Id == _films[2500].Id).People).Name);
	}
	// re-review (Minor 1, the reviewer's probe): R before any minute's keep (the one-minute default): only the cut-short keep
	// can keep what came; and (Minor 2) what came isn't enough for a slot made of people until the rest are in
	[Fact]
	public async Task RBeforeAnyMinuteKeep_KeepsWhatCameByTheCut_AndPeopleSlotsWaitForTheRest()
	{
		PeoplePagesHeldFrom(700, new TaskCompletionSource().Task);
		using var index = Index(wholeAnyTime: true); // a minute between keeps
		Assert.NotNull(await index.GetAsync());
		await WaitFor(() => Volatile.Read(ref _peoplePages) >= 5); // the pages before 700 in, those from 700 waiting
		Assert.Equal(0, WithPeople(index.Current!)); // no minute's keep yet
		var pages = _peoplePages;
		var titlesHeld = new TaskCompletionSource();
		Pages(_ => titlesHeld.Task);

		var refreshing = index.RefreshAsync(); // R
		await WaitForIndex(index, x => x.PeopleToAsk.Count > 0);
		var came = WithPeople(index.Current!);
		Assert.Equal(700, came);
		Assert.True(index.Current!.HasPeople);
		Assert.False(index.Current.PeopleCovered); // 700 of 2,500: no More from or Spotlight of a person on them
		titlesHeld.SetResult();
		Assert.NotNull(await refreshing.WaitAsync(TimeSpan.FromSeconds(20)));
		await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));

		Assert.Equal(pages, _peoplePages);
		Assert.Equal(2500 - came, _peopleByIdTitles);
		Assert.Equal(2500, WithPeople(index.Current!));
		Assert.True(index.Current!.PeopleCovered);
	}

	// re-review (Minor 3): Sign in again as the same user, mid-step, doesn't end what the step keeps
	[Fact]
	public async Task SigningInAgainAsTheSameUser_MidStep_StillKeepsWhatCame()
	{
		_homeCache.SignedIn("http://server", User, "fake", "alice"); // the app's sign-in, before any read
		PeoplePagesHeldFrom(700, new TaskCompletionSource().Task);
		var first = Index();
		Assert.NotNull(await first.GetAsync());
		await WaitFor(() => Volatile.Read(ref _peoplePages) >= 5);

		_homeCache.SignedIn("http://server", User, "fake", "alice"); // Sign in again, the same user and server
		first.Dispose(); // then closed
		await first.Settled.WaitAsync(TimeSpan.FromSeconds(20));

		await WaitForSaved(json => json.Contains("\"PeopleToAsk\":[\"", StringComparison.Ordinal)); // what came, kept by the cut
		using var next = Index();
		Assert.Equal(700, WithPeople((await next.GetAsync())!));
	}

	// re-review (Minor 4): the cut-short step's save and R's publish could be written out of order, leaving an older index on
	// this PC than the one shown
	[Fact]
	public async Task ACutShortSaveBehindRsPublish_IsLeftOut_TheFileIsTheNewer()
	{
		var release = new TaskCompletionSource();
		var held = 0;
		IndexSnapshot? cut = null;
		var decided = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		PeoplePagesHeldFrom(700, new TaskCompletionSource().Task);
		using var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System)
		{
			WholeReadEvery = TimeSpan.Zero, // 1.9: R reads every title again, as before
			// the cut-short save (the first written with titles still to ask) waits until R's read has written everything
			BeforeSave = async snapshot =>
			{
				if (snapshot.PeopleToAsk.Count > 0 && Interlocked.CompareExchange(ref held, 1, 0) == 0)
				{
					cut = snapshot;
					await release.Task;
				}
			},
		};
		index.SaveDecided += (snapshot, written) =>
		{
			if (ReferenceEquals(snapshot, cut))
			{
				decided.TrySetResult(written);
			}
		};
		Assert.NotNull(await index.GetAsync());
		await WaitFor(() => Volatile.Read(ref _peoplePages) >= 5);
		var titlesHeld = new TaskCompletionSource();
		Pages(_ => titlesHeld.Task);
		var refreshing = index.RefreshAsync(); // R
		await WaitForIndex(index, x => x.PeopleToAsk.Count > 0);
		titlesHeld.SetResult();
		await refreshing.WaitAsync(TimeSpan.FromSeconds(20));
		await index.Settled.WaitAsync(TimeSpan.FromSeconds(20)); // R's read, its people too, in and written

		release.SetResult(); // now the older save
		Assert.False(await decided.Task.WaitAsync(TimeSpan.FromSeconds(20)));
		await WaitForSaved(json => json.Contains("\"PeopleToAsk\":[]", StringComparison.Ordinal));
		using var next = Index();
		var saved = (await next.GetAsync())!;
		Assert.Equal(2500, WithPeople(saved));
		Assert.Empty(saved.PeopleToAsk);
	}

	private static async Task WaitFor(Func<bool> condition)
	{
		for (var i = 0; i < 800 && !condition(); i++)
		{
			await Task.Delay(25);
		}

		Assert.True(condition(), "it happened");
	}
}
