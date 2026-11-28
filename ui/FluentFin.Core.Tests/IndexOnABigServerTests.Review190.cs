using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.9: the review of wip/190-index (the index's load): its Majors and items, each with its test.</summary>
public sealed partial class IndexOnABigServerTests
{
	// --- Major 1 and item 7: the favourites --------------------------------------------------------------------------------

	// the reviewer's: a favourites page after the first that fails took the favourite mark off every title on it
	[Fact]
	public async Task AFavouritesPageThatFails_LeavesTheFavouritesAsTheyWere()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		for (var i = 0; i < 700; i++)
		{
			_films[i].UserData = new UserItemDataDto { Played = false, IsFavorite = true };
		}

		Marks();
		var first = await ReadAndSave(time);
		Assert.Equal(700, first.Entries.Count(x => x.Favorite));
		_client.Setup(x => x.GetFavoritesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((int start, int limit, CancellationToken _) =>
			{
				if (start > 0)
				{
					return null; // a timeout on the second page
				}

				var favourites = _films.Where(x => x.UserData?.IsFavorite == true).ToList();
				return new BaseItemDtoQueryResult { Items = [.. favourites.Skip(start).Take(limit)], TotalRecordCount = favourites.Count };
			});
		time.Now += TimeSpan.FromHours(1);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		var shown = await index.GetAsync();
		await index.Checking;
		await Task.Delay(200);
		await index.Settled;
		Assert.Equal(700, index.Current!.Entries.Count(x => x.Favorite));
		Assert.Same(shown, index.Current); // nothing applied
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: the marks couldn't be read (a page of the favourites couldn't be read (500 of 700 in))", StringComparison.Ordinal));
	}

	// item 7: more favourites than the most: their count alone is asked (one request of one), none applied, the rest of the
	// marks are
	[Fact]
	public async Task MoreThan2000Favourites_AreCountedOnly_AndLeftToTheDaysRead()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		for (var i = 0; i < 2100; i++)
		{
			_films[i].UserData = new UserItemDataDto { Played = false, IsFavorite = true };
		}

		var marks = Marks();
		var pagesAsked = new List<int>();
		_client.Setup(x => x.GetFavoritesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((int start, int limit, CancellationToken _) =>
			{
				Interlocked.Increment(ref marks.Value);
				lock (pagesAsked)
				{
					pagesAsked.Add(limit);
				}

				var favourites = _films.Where(x => x.UserData?.IsFavorite == true).ToList();
				return new BaseItemDtoQueryResult { Items = [.. favourites.Skip(start).Take(limit)], TotalRecordCount = favourites.Count };
			});
		await ReadAndSave(time);
		_films[0].UserData = new UserItemDataDto { Played = false, IsFavorite = false }; // taken off on another device
		_films[7].UserData = PlayedAt(time.Now + TimeSpan.FromMinutes(30)); // and a film played
		time.Now += TimeSpan.FromHours(1);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		await index.GetAsync();
		await WaitForIndex(index, x => x.Entries.Single(e => e.Id == _films[7].Id).Played);
		await index.Settled;

		Assert.Equal([1], pagesAsked); // the count alone
		Assert.Equal(2100, index.Current!.Entries.Count(x => x.Favorite)); // as they were (a played mark doesn't touch them)
		Assert.True(index.Current.Entries.Single(x => x.Id == _films[0].Id).Favorite); // left to the day's read
		Assert.Contains(log.Entries, x => x.Message.Contains("favourites left to the day's read (more than the most)", StringComparison.Ordinal));
	}

	// --- Major 2: the read of what changed has a cap ---------------------------------------------------------------------------

	// the reviewer's: a mass save again on the server (a metadata refresh) within the day's margin was read again at every R,
	// each about a read of every title (8 pages of 7,500 here). Now each R stops at the first page's count (one request of the
	// titles saved since), they wait for the day's read, and R past the day reads every title (dated anew)
	[Fact]
	public async Task AMassResave_FollowedByFourRs_StopsAtTheFirstPagesCount_AndTheDaysReadBringsIt()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		_films.AddRange(Enumerable.Range(2500, 5000).Select(Film));
		_check = "7500:newest";
		var asked = SavedSince();
		Marks();
		var first = await ReadAndSave(time);
		var scan = time.Now + TimeSpan.FromHours(1);
		foreach (var film in _films)
		{
			_savedAt[film.Id!.Value] = scan;
		}

		_films[10].Name = "Film 00010, refreshed";
		time.Now += TimeSpan.FromHours(2);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		await index.GetAsync();
		await index.Checking;
		await index.Settled;
		var pages = _pages;
		var counts = new List<int>();
		for (var r = 0; r < 4; r++)
		{
			time.Now += TimeSpan.FromHours(4);
			var before = asked.Value;
			await index.RefreshAsync();
			await index.Settled;
			counts.Add(asked.Value - before);
		}

		Assert.Equal([1, 1, 1, 1], counts); // the first page and its count, each R (8 pages each before)
		Assert.Equal(pages, _pages); // no read of every title within the day
		Assert.Equal("Film 00010", index.Current!.Entries.Single(x => x.Id == _films[10].Id).Name); // left to the day's read
		Assert.Equal(first.ReadAt, index.Current.ReadAt);
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: 7500 titles saved since the last read or in a library new to the index, more than a read of what changed reads (1875): they come with the day's read", StringComparison.Ordinal));

		time.Now = first.ReadAt + LibraryIndex.FreshFor + TimeSpan.FromMinutes(1); // past the day: R reads every title
		await index.RefreshAsync();
		await index.Settled;
		Assert.True(_pages > pages);
		Assert.Equal("Film 00010, refreshed", index.Current!.Entries.Single(x => x.Id == _films[10].Id).Name);
		Assert.Equal(time.Now, index.Current.ReadAt);
	}

	// --- item 3: a day's read cut short, and its wait for Home's first row -----------------------------------------------------

	// a day's read closed part way (Nixie closed, a sign-out) held the next one for a day: the note said it began. Only a
	// server's failure holds it now (ADaysReadThatFailsPartWay_IsNotBegunAgainWithinTheDay_EvenAfterARestart); a close doesn't
	[Fact]
	public async Task ADaysReadClosedPartWay_DoesntHoldTheNextStartsRead()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		time.Now += LibraryIndex.FreshFor + TimeSpan.FromHours(1); // the next day: its read begins
		var held = new TaskCompletionSource();
		Pages(start => start == 0 ? Task.CompletedTask : held.Task);
		var before = _pages;
		var closing = Index(time);
		await closing.GetAsync();
		for (var i = 0; i < 400 && _pages == before; i++)
		{
			await Task.Delay(10);
		}

		Assert.True(_pages > before, "the day's read began");
		closing.Dispose(); // Nixie closed part way
		held.SetResult();
		await closing.Settled.WaitAsync(TimeSpan.FromSeconds(20));
		Pages(_ => Task.CompletedTask);
		var cut = _pages;

		time.Now += TimeSpan.FromHours(2); // the next start, past the hour
		using var next = Index(time);
		await next.GetAsync();
		for (var i = 0; i < 400 && _pages == cut; i++)
		{
			await Task.Delay(10);
		}

		await next.Settled;
		Assert.True(_pages > cut, "the day's read began again");
		Assert.Equal(time.Now, next.Current!.ReadAt);
	}

	// the day's read begins after Home's first row (the saved index's rows first), or after FirstRowLongest; R doesn't wait
	[Fact]
	public async Task ADaysRead_BeginsAfterHomesFirstRow_AndRDoesntWait()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		time.Now += LibraryIndex.FreshFor + TimeSpan.FromHours(1);
		var firstRow = new TaskCompletionSource();
		var before = _pages;
		using (var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), time) { FirstScreen = firstRow.Task })
		{
			Assert.NotNull(await index.GetAsync()); // the saved index at once
			await Task.Delay(200);
			Assert.Equal(before, _pages); // the day's read waits for Home's first row
			firstRow.SetResult();
			for (var i = 0; i < 400 && _pages == before; i++)
			{
				await Task.Delay(10);
			}

			await index.Settled;
			Assert.True(_pages > before);
			Assert.Equal(time.Now, index.Current!.ReadAt);
		}

		time.Now += LibraryIndex.FreshFor + TimeSpan.FromHours(1);
		var r = _pages;
		using var other = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), time) { FirstScreen = new TaskCompletionSource().Task };
		Assert.NotNull(await other.SavedAsync());
		Assert.NotNull(await other.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(20))); // R past the day: every title now
		Assert.True(_pages > r);
	}

	// --- item 4: RemovalsDue at work ---------------------------------------------------------------------------------------------

	// a Nixie collection's page asks its titles by id: one the server doesn't answer while the index says titles were removed
	// leaves the index at once (Made by Nixie's other rows with it); before that, nothing is dropped on an answer alone
	[Fact]
	public async Task ACollectionPage_DropsTitlesTheServerNoLongerHas_WhileRemovalsAreDue()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		await ReadAndSave(time);
		var (gone, kept) = (_films[10], _films[11]);
		_client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) => (IReadOnlyList<BaseItemDto>?)[.. _films.Where(x => ids.Contains(x.Id!.Value))]);
		var collection = new SmartCollection("pick:highly-rated", SmartCollectionKind.HighlyRated, "Highly rated, not watched", "", false, [gone.Id!.Value, kept.Id!.Value]);
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		time.Now += TimeSpan.FromHours(2);
		using var index = Index(time);
		SmartCollectionServices.Index = index;
		try
		{
			await index.GetAsync();
			await index.Checking;
			await index.Settled;
			_films.Remove(gone); // removed on the server; the index not told yet
			await new CollectionViewModel(_client.Object, commands).OnNavigatedTo(new NixieCollection(collection));
			await Task.Delay(100);
			Assert.Contains(index.Current!.Entries, x => x.Id == gone.Id); // an answer alone drops nothing

			_films.Add(Added(2500, time.Now - TimeSpan.FromMinutes(10), time.Now - TimeSpan.FromMinutes(10)));
			_check = "2500:newer";
			await index.RefreshAsync(); // R: what changed: the count fell by one more than it should
			await index.Settled;
			Assert.True(index.Current!.RemovalsDue);
			Assert.Contains(index.Current.Entries, x => x.Id == gone.Id);

			// the save waited for, not polled (a read of the file while it is replaced makes that save fail)
			var savedWithout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			index.SaveDecided += (snapshot, written) =>
			{
				if (written && snapshot.Entries.All(e => e.Id != gone.Id))
				{
					savedWithout.TrySetResult();
				}
			};
			await new CollectionViewModel(_client.Object, commands).OnNavigatedTo(new NixieCollection(collection));
			await WaitForIndex(index, x => x.Entries.All(e => e.Id != gone.Id));
			Assert.Contains(index.Current!.Entries, x => x.Id == kept.Id);
			await savedWithout.Task.WaitAsync(TimeSpan.FromSeconds(20));
			Assert.DoesNotContain(gone.Id!.Value.ToString(), await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single()), StringComparison.Ordinal); // and saved so
		}
		finally
		{
			SmartCollectionServices.Index = null;
		}
	}

	// a library's count that rose by more than its titles new to it (one that came with no save since: a library the user was
	// given back) is said apart from one that fell, and due no removals
	[Fact]
	public async Task ACountThatRose_IsSaidApartFromOneThatFell_AndDueNoRemovals()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		await ReadAndSave(time);
		_films.Add(Film(2500)); // its save date long ago: not among the titles saved since
		_check = "2501:newer";
		time.Now += TimeSpan.FromHours(2);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		await index.GetAsync();
		await WaitForIndex(index, x => x.Check == "2501:newer");
		await index.Settled;

		Assert.False(index.Current!.RemovalsDue);
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: titles came that the titles saved since didn't bring (library 1 of 1: 0 titles new to it since the last read, but its count moved by 1)", StringComparison.Ordinal));
		Assert.DoesNotContain(log.Entries, x => x.Message.StartsWith("Library index: titles were removed", StringComparison.Ordinal));
	}

	// the Collections page follows a merged read of what changed (R within the day) as it follows a read of every title: every
	// tile holds the collection the merged index makes
	[Fact]
	public async Task TheCollectionsPage_FollowsAMergedReadOfWhatChanged()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		await ReadAndSave(time);
		var pages = _pages;
		time.Now += TimeSpan.FromHours(2);
		using var index = Index(time);
		using var ui = new UiThread();
		var vm = Page(index);
		var done = new TaskCompletionSource();
		done.SetResult();
		try
		{
			await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
			await TilesSettleAs(ui, vm, index);
			var before = (await vm.MadeByNixie(index.Current!)).Single(x => x.Title == "Heists").ItemIds.Count;
			foreach (var i in new[] { 2500, 2504, 2508, 2512 }) // four heists added on the server
			{
				_films.Add(Added(i, time.Now - TimeSpan.FromMinutes(30), time.Now - TimeSpan.FromMinutes(30)));
			}

			await index.RefreshAsync(); // R: what changed
			await TilesSettleAs(ui, vm, index);
			Assert.Equal(2504, index.Current!.Entries.Count);
			Assert.True((await vm.MadeByNixie(index.Current)).Single(x => x.Title == "Heists").ItemIds.Count > before);
			Assert.Equal(pages, _pages); // no page of every title
		}
		finally
		{
			await Settle(done, vm, index, ui);
		}
	}

	// item 7: the favourites at a start and on R, not with a library change's read (at most hourly while Nixie is open)
	[Fact]
	public async Task TheFavourites_AreAskedOnR_NotWithALibraryChangesRead()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		Marks();
		var favouritesAsked = 0;
		_client.Setup(x => x.GetFavoritesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((int start, int limit, CancellationToken _) =>
			{
				Interlocked.Increment(ref favouritesAsked);
				return new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 };
			});
		await ReadAndSave(time);
		_films.Add(Added(2500, time.Now + TimeSpan.FromHours(1), time.Now + TimeSpan.FromHours(1)));
		_check = "2501:newer";
		time.Now += TimeSpan.FromHours(2);
		using var index = Index(time);
		await index.GetAsync();
		await WaitForIndex(index, x => x.Check == "2501:newer"); // a library change's read, with the marks
		await index.Settled;
		Assert.Equal(0, favouritesAsked);

		await index.RefreshAsync(); // R
		await index.Settled;
		Assert.Equal(1, favouritesAsked); // the count (none)
	}

	// --- items 5, 6 and 8, and the caps -------------------------------------------------------------------------------------------

	// a Shows library of n shows beside Movies, in the index; their played episodes and counts as the marks' fakes answer them
	private Guid ShowsLibrary(int n)
	{
		var showsId = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000191");
		for (var i = 0; i < n; i++)
		{
			_shows.Add(new BaseItemDto { Id = Guid.Parse($"bbbbbbbb-0191-0000-0000-{i:D12}"), Name = $"Show {i:D3}", SortName = $"show {i:D3}", Type = BaseItemDto_Type.Series, UserData = new UserItemDataDto { UnplayedItemCount = 10 } });
		}

		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([Library(MoviesId, "Movies"), Library(showsId, "Shows", BaseItemDto_CollectionType.Tvshows)]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, int start, int limit, bool total, CancellationToken _) =>
			{
				Interlocked.Increment(ref _pages);
				var titles = library.Id == showsId ? _shows : _films;
				return new BaseItemDtoQueryResult { Items = [.. titles.Skip(start).Take(limit)], TotalRecordCount = total ? titles.Count : null };
			});
		_client.Setup(x => x.GetPlayedEpisodesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		_client.Setup(x => x.GetLibraryTitleCount(It.IsAny<BaseItemDto>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, CancellationToken _) => library.Id == showsId ? _shows.Count : _films.Count);
		return showsId;
	}

	// item 5: a 401 or 403 on the marks ends them: nothing applied, the recounts left cancelled, and none asked again that run
	[Theory]
	[InlineData("played lately")]
	[InlineData("a show's count")]
	public async Task AMarksRequestRefused_EndsTheMarks_AndNoneAreAskedAgainThisRun(string refused)
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		ShowsLibrary(6);
		var marks = Marks();
		var counts = 0;
		if (refused == "played lately")
		{
			_client.Setup(x => x.GetPlayedLatelyPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IndexRefusedException(401));
		}
		else
		{
			_client.Setup(x => x.GetPlayedEpisodeCount(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
				.Returns(async (Guid _, CancellationToken token) =>
				{
					Interlocked.Increment(ref counts);
					await Task.Delay(20, token);
					throw new IndexRefusedException(403);
				});
		}

		await ReadAndSave(time);
		for (var i = 0; i < 6; i++)
		{
			Episode(_shows[i], i, time.Now + TimeSpan.FromMinutes(10 + i)); // an episode of each show played since
		}

		time.Now += TimeSpan.FromHours(1);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		var shown = await index.GetAsync();
		await index.Checking;
		await index.Settled;
		Assert.Same(shown, index.Current); // nothing applied
		Assert.Contains(log.Entries, x => x.Message.StartsWith($"Library index: the server didn't answer the marks as asked (the server refused them (HTTP {(refused == "played lately" ? 401 : 403)}))", StringComparison.Ordinal));
		if (refused != "played lately")
		{
			Assert.InRange(counts, 1, 2); // the two in flight at most: the four left never asked
		}

		var asked = (marks.Value, counts);
		await index.RefreshAsync(); // R: what changed, no marks
		await index.Settled;
		Assert.Equal(asked, (marks.Value, counts));
	}

	// the caps: at most 5 pages of 100 titles played since (the newest 500 applied, the rest at the day's read), and at most 50
	// shows counted again (one request each)
	[Fact]
	public async Task TheMarks_ReadAtMost5PagesOfPlays_AndCountAtMost50Shows()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		ShowsLibrary(60);
		var marks = Marks();
		var pagesAsked = 0;
		var showsCounted = 0;
		_client.Setup(x => x.GetPlayedLatelyPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((int start, int limit, CancellationToken _) =>
			{
				Interlocked.Increment(ref pagesAsked);
				var played = _films.Concat(_episodesPlayed).Where(x => x.UserData?.Played == true).OrderByDescending(x => x.UserData!.LastPlayedDate).ToList();
				return new BaseItemDtoQueryResult { Items = [.. played.Skip(start).Take(limit)] };
			});
		_client.Setup(x => x.GetPlayedEpisodeCount(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((Guid _, CancellationToken _) =>
			{
				Interlocked.Increment(ref showsCounted);
				return 1;
			});
		await ReadAndSave(time);
		for (var i = 0; i < 60; i++)
		{
			Episode(_shows[i], i, time.Now + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(i * 10)); // 60 shows played, the newest first
		}

		for (var i = 0; i < 600; i++)
		{
			_films[i].UserData = PlayedAt(time.Now + TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(i * 2)); // 600 films after them
		}

		time.Now += TimeSpan.FromHours(1);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		await index.GetAsync();
		await WaitForIndex(index, x => x.Entries.Count(e => e.Played) > 0);
		await index.Settled;
		Assert.Equal(5, pagesAsked); // 500 plays, the newest: every one a film
		Assert.Equal(500, index.Current!.Entries.Count(x => x.Played));
		Assert.Equal(0, showsCounted); // the shows' plays were older than the 500th: at the day's read
		Assert.Contains(log.Entries, x => x.Message.Contains("(more played since: the rest at the day's read)", StringComparison.Ordinal));

		// the shows alone played since: 50 counted again of 60, the most recent
		foreach (var film in _films)
		{
			film.UserData = new UserItemDataDto { Played = false };
		}

		_episodesPlayed.Clear();
		for (var i = 0; i < 60; i++)
		{
			Episode(_shows[i], i, time.Now + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(i * 10));
		}

		time.Now += TimeSpan.FromHours(1);
		await index.RefreshAsync(); // R
		await index.Settled;
		Assert.Equal(50, showsCounted);
		Assert.Equal(50, index.Current!.SeriesPlays.Count);
		Assert.Contains(_shows[59].Id!.Value, index.Current.SeriesPlays.Keys); // the latest played
		Assert.DoesNotContain(_shows[0].Id!.Value, index.Current.SeriesPlays.Keys);
	}

	// item 6 and a publish of marks alone: a read of the marks that changed some publishes the index (its titles, people and
	// dates as they were) once; one that changed none keeps its new time quietly (no page made again); MarksAt moves only when
	// a read of the marks came whole
	[Fact]
	public async Task APublishOfMarksAlone_KeepsTheTitles_AndMarksAtMovesOnlyWhenTheMarksCame()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		Marks();
		var first = await ReadAndSave(time);
		var (people, byId) = (_peoplePages, _peopleById);
		_films[7].UserData = PlayedAt(time.Now + TimeSpan.FromMinutes(30));
		time.Now += TimeSpan.FromHours(1);
		var startedAt = time.Now;
		using (var index = Index(time))
		{
			var published = 0;
			index.Changed += (_, _) => Interlocked.Increment(ref published);
			await index.GetAsync();
			await WaitForIndex(index, x => x.Entries.Single(e => e.Id == _films[7].Id).Played);
			await index.Settled;
			var now = index.Current!;
			Assert.Equal(2, published); // the saved one, then the marks
			Assert.Equal((first.Entries.Count, first.ReadAt, first.TitlesAt, first.Check), (now.Entries.Count, now.ReadAt, now.TitlesAt, now.Check));
			Assert.All(now.Entries, x => Assert.Single(x.People)); // the people as they were
			Assert.Equal((people, byId), (_peoplePages, _peopleById)); // none asked
			Assert.Equal(startedAt, now.MarksAt);
		}

		await WaitForSaved(x => x.Contains($"\"MarksAt\":\"{startedAt:yyyy-MM-ddTHH:mm:ss}", StringComparison.Ordinal)); // the offset's + is written \u002B

		// the next start: the marks fail (the server didn't answer): MarksAt stays
		_client.Setup(x => x.GetPlayedLatelyPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		time.Now += TimeSpan.FromHours(1);
		using (var failing = Index(time))
		{
			await failing.GetAsync();
			await failing.Checking;
			await failing.Settled;
			Assert.Equal(startedAt, failing.Current!.MarksAt);
		}

		// and the one after: they came, none changed: a quiet keep of the new time, no page made again
		Marks();
		time.Now += TimeSpan.FromHours(1);
		using var quiet = Index(time);
		var told = 0;
		quiet.Changed += (_, _) => Interlocked.Increment(ref told);
		await quiet.GetAsync();
		await quiet.Checking;
		await quiet.Settled;
		Assert.Equal(1, told); // the saved one only
		Assert.Equal(time.Now, quiet.Current!.MarksAt);
		await WaitForSaved(x => x.Contains($"\"MarksAt\":\"{time.Now:yyyy-MM-ddTHH:mm:ss}", StringComparison.Ordinal));
	}

	// the review: a saved time after now (this PC's clock moved back) asked what changed, and the marks, from the future: nothing
	// came until the clock passed it. Asked from now (less the margin)
	[Fact]
	public async Task ASavedTimeAfterNow_AsksWhatChangedAndTheMarksFromNow()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		SavedSince();
		Marks();
		await ReadAndSave(time);
		await WaitForSaved(x => x.Contains("\"TitlesAt\":\"2026-11-22T08:00:00", StringComparison.Ordinal));
		var file = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single();
		var text = await File.ReadAllTextAsync(file);
		await File.WriteAllTextAsync(file, System.Text.RegularExpressions.Regex.Replace(text, "\"(TitlesAt|MarksAt)\":\"[^\"]*\"", m => $"\"{m.Groups[1].Value}\":\"2026-11-25T08:00:00.0000000Z\"")); // three days ahead

		_films.Add(Added(2500, time.Now + TimeSpan.FromMinutes(30), time.Now + TimeSpan.FromMinutes(30)));
		_films[7].UserData = PlayedAt(time.Now + TimeSpan.FromMinutes(40));
		time.Now += TimeSpan.FromHours(2);
		using var index = Index(time);
		Assert.Equal(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero), (await index.GetAsync())!.TitlesAt);
		await index.RefreshAsync(); // R: what changed and the marks
		await index.Settled;
		Assert.Contains(index.Current!.Entries, x => x.Name == "Film 02500");
		Assert.True(index.Current.Entries.Single(x => x.Id == _films[7].Id).Played);
	}

	// item 9: the marks' requests log a failure's type, never its message (an HttpRequestException's carries the server's
	// host:port), as the requests by id do
	[Fact]
	public void TheMarksRequests_LogAFailuresType_NotItsMessage()
	{
		var client = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin.Core", "Services", "JellyfinClient_Index.cs"));
		var marks = client[client.IndexOf("// 1.9 (the index's load on a server that isn't the user's): the user's marks", StringComparison.Ordinal)..];
		marks = marks[..marks.IndexOf("// end 1.9", StringComparison.Ordinal)];
		Assert.Contains("ex.GetType().Name", marks);
		Assert.DoesNotContain("ex.Message", marks);
		Assert.Equal(3, marks.Split("throw new IndexRefusedException(ex.ResponseStatusCode)").Length - 1); // item 5: the three kinds of request
	}

	// item 8: played episodes that didn't come with a read are asked again by R (they waited for the hour's retry)
	[Fact]
	public async Task R_AsksAgainThePlayedEpisodesThatDidntCome()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var came = false;
		var asked = MoviesAndShows(() => came);
		SavedSince();
		var first = await ReadAndSave(time);
		Assert.True(first.PlaysDue);
		came = true;
		time.Now += TimeSpan.FromMinutes(10); // within the hour of the read: no retry of its own yet
		using var index = Index(time);
		await index.GetAsync();
		await index.Checking;
		await index.Settled;
		Assert.True(index.Current!.PlaysDue);
		var before = asked.Value;

		await index.RefreshAsync(); // R
		await index.Settled;
		Assert.True(asked.Value > before);
		Assert.False(index.Current!.PlaysDue);
		Assert.Equal(10, index.Current.SeriesPlays.Count);
	}}
