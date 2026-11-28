using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the index's load on a server that isn't the user's: the owner's has about 190,000 titles, so a read of every title
/// is about 190 requests of 1,000 titles). 1.7.5 and 1.8.1 read every title whenever a library change's read of the titles saved
/// since couldn't explain the counts: fewer titles in all, a library's count moved by more or less than its titles added, more
/// than about 4,100 saved since, or the libraries changed; and R read every title each time. On a big, busy server that was
/// nearly every start. Now a read of every title comes at most once a day per server and user; within the day only what
/// changed is read, paged as every title is; and titles removed meanwhile stay, said, until the day's read.
/// </summary>
public sealed partial class IndexOnABigServerTests
{
	// what the titles saved since can't explain is no longer read whole within the day: the titles added and changed are merged,
	// the removed one stays and the index says its removals are due (never taken as complete), and the day's read of every
	// title, 24 hours after the last, takes it out. The cases: a title removed (the count moved by less); the 1.7.5 review's,
	// one removed while one is added with an older date and one newest (the first build read newest first by DateCreated and
	// missed the older one); 1.8.1's, one removed while one is added in a library the index skips (the server's check moved by 0)
	[Theory]
	[InlineData("removed")]
	[InlineData("removed, one older and one newest added")]
	[InlineData("removed, one added in a skipped library")]
	public async Task WithinTheDay_ACountTheTitlesSavedSinceCantExplain_ReadsOnlyThem_AndTheDaysReadTakesTheRemovedOut(string change)
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var asked = SavedSince();
		_elsewhere.Add(Film(9001)); // a mixed library's film, there before the first read (counted by the check, never indexed)
		var first = await ReadAndSave(time);
		var pages = _pages;
		var removed = _films[10];
		var scan = time.Now + TimeSpan.FromHours(1);
		string[] added;
		_films.Remove(removed);
		switch (change)
		{
			case "removed":
				_films.Add(Added(2500, scan, scan));
				(added, _check) = (["Film 02500"], "2500:newer"); // one gone, one added: the count moved by 0, not 1
				break;
			case "removed, one older and one newest added":
				_films.AddRange([Added(2500, scan, scan), Added(2501, FirstAdded.AddDays(-400), scan)]);
				(added, _check) = (["Film 02500", "Film 02501"], "2501:newer"); // one gone, two added: by 1, not 2
				break;
			default:
				_elsewhere.Add(Added(9002, scan, scan));
				(added, _check) = ([], "2500:elsewhere"); // one gone from Movies, one added to the mixed library: by 0
				break;
		}

		time.Now += TimeSpan.FromHours(2); // the day's second start
		var log = new CoreListLogger<LibraryIndex>();
		using (var index = Index(time, log))
		{
			await index.GetAsync();
			await WaitForIndex(index, x => x.Check == _check);
			await index.Checking;
			await index.Settled;

			Assert.Equal(pages, _pages); // no page of every title
			Assert.Equal((1, 1), (asked.Value, _counted)); // one page of the titles saved since, and Movies' count
			var now = index.Current!;
			Assert.All(added, name => Assert.Contains(now.Entries, x => x.Name == name)); // every addition is seen
			Assert.Contains(now.Entries, x => x.Id == removed.Id); // the removed title stays until the day's read...
			Assert.True(now.RemovalsDue); // ...and the index says so: it isn't taken as complete
			Assert.Equal(first.ReadAt, now.ReadAt);
			Assert.DoesNotContain(now.Entries, x => x.Name is "Film 09001" or "Film 09002"); // never a skipped library's
			Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: titles were removed (library 1 of 1: ", StringComparison.Ordinal));
		}

		await WaitForSaved(x => x.Contains("\"RemovalsDue\":true", StringComparison.Ordinal)); // kept across a restart

		// a start 24 hours after the day's read: every title, once; the removed title goes
		time.Now = first.ReadAt + LibraryIndex.FreshFor + TimeSpan.FromMinutes(1);
		using var day = Index(time);
		Assert.Contains((await day.GetAsync())!.Entries, x => x.Id == removed.Id); // the saved one at once
		for (var i = 0; i < 400 && _pages == pages; i++)
		{
			await Task.Delay(10);
		}

		await day.Settled;
		Assert.True(_pages > pages, "the day's read of every title");
		Assert.DoesNotContain(day.Current!.Entries, x => x.Id == removed.Id);
		Assert.False(day.Current.RemovalsDue);
		Assert.All(added, name => Assert.Contains(day.Current.Entries, x => x.Name == name));
		Assert.Equal(time.Now, day.Current.ReadAt);
	}

	// R within the day reads what changed (each R was every title: about 190 requests on the owner's server), again at once if
	// pressed again, and never every title; past the day, R reads every title
	[Fact]
	public async Task R_WithinTheDay_ReadsWhatChanged_AndPastTheDay_EveryTitle()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var asked = SavedSince();
		var first = await ReadAndSave(time);
		var pages = _pages;
		_films.Add(Added(2500, time.Now + TimeSpan.FromMinutes(30), time.Now + TimeSpan.FromMinutes(30)));
		time.Now += TimeSpan.FromHours(1);
		using var index = Index(time);
		await index.GetAsync();
		await index.Checking;
		await index.Settled;
		Assert.Equal(0, asked.Value); // the check saw nothing: nothing read

		var fresh = await index.RefreshAsync(); // R
		await index.Settled;
		Assert.Equal(2501, fresh!.Entries.Count);
		Assert.Equal((pages, 1), (_pages, asked.Value));
		Assert.Equal(first.ReadAt, index.Current!.ReadAt);

		time.Now += TimeSpan.FromMinutes(1);
		await index.RefreshAsync(); // R again
		await index.Settled;
		Assert.Equal((pages, 2), (_pages, asked.Value));

		time.Now = first.ReadAt + LibraryIndex.FreshFor + TimeSpan.FromMinutes(1); // past the day
		await index.RefreshAsync();
		await index.Settled;
		Assert.True(_pages > pages, "R past the day reads every title");
		Assert.Equal(time.Now, index.Current!.ReadAt);
	}

	// the day's read is limited from when it began, finished or not, and the note of it outlives a restart: one that fails part
	// way (a slow server's pages running out) isn't begun again at each hour, as every older read was; a change is still read
	// as what changed meanwhile; and the next read of every title waits for 24 hours from the one that failed
	[Fact]
	public async Task ADaysReadThatFailsPartWay_IsNotBegunAgainWithinTheDay_EvenAfterARestart()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var asked = SavedSince();
		await ReadAndSave(time);
		time.Now += LibraryIndex.FreshFor + TimeSpan.FromHours(1); // the next day: its read begins, and the server goes away part way
		var failedAt = time.Now;
		Pages(start => start == 0 ? Task.CompletedTask : Task.FromException(new HttpRequestException("the server went away")));
		var before = _pages;
		using (var failing = Index(time))
		{
			await failing.GetAsync();
			for (var i = 0; i < 400 && _pages == before; i++)
			{
				await Task.Delay(10);
			}

			await failing.Settled;
		}

		Assert.True(_pages > before, "the day's read began");
		Pages(_ => Task.CompletedTask);
		var failed = _pages;

		// restarts every two hours for the rest of the day: no read of every title; a change in between, read as what changed
		foreach (var hours in new[] { 2, 4, 6, 12, 23 })
		{
			time.Now = failedAt + TimeSpan.FromHours(hours);
			if (hours == 6)
			{
				_films.Add(Added(2500, time.Now - TimeSpan.FromMinutes(30), time.Now - TimeSpan.FromMinutes(30)));
				_check = "2501:newer";
			}

			using var restart = Index(time);
			await restart.GetAsync();
			await Task.Delay(50);
			await restart.Checking;
			await restart.Settled;
			Assert.Equal(failed, _pages);
		}

		Assert.Equal(1, asked.Value);
		using (var shown = Index(time))
		{
			Assert.Equal(2501, (await shown.GetAsync())!.Entries.Count);
			await shown.Checking;
			await shown.Settled;
		}

		time.Now = failedAt + LibraryIndex.FreshFor + TimeSpan.FromMinutes(1); // 24 hours after the one that failed
		using var next = Index(time);
		await next.GetAsync();
		for (var i = 0; i < 400 && _pages == failed; i++)
		{
			await Task.Delay(10);
		}

		await next.Settled;
		Assert.True(_pages > failed, "the next read of every title, a day after the one that failed");
		Assert.Equal(time.Now, next.Current!.ReadAt);
	}

	// more than 4,100 titles saved since the last read (a metadata refresh on the server) made 1.7.5's read of what changed give up
	// and read every title; its pages come as every title's do now (IndexPages: a first page of 200 with the count, then 1,000 a
	// page, two at a time), and nothing is read whole. The review's Major 2: under the cap (a quarter of the index here)
	[Fact]
	public async Task MoreThan4100TitlesSavedSince_ArePagedAsWhatChanged_NotReadWhole()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		_films.AddRange(Enumerable.Range(2500, 17500).Select(Film)); // 20,000 films: at most 5,000 saved since read as what changed
		_check = "20000:newest";
		var asked = SavedSince();
		await ReadAndSave(time);
		var pages = _pages;
		var scan = time.Now + TimeSpan.FromHours(1);
		foreach (var film in _films.Take(4200))
		{
			_savedAt[film.Id!.Value] = scan; // saved again: their metadata refreshed
		}

		_films[10].Name = "Film 00010, refreshed";
		_films.Add(Added(20000, scan, scan)); // and one added
		_check = "20001:newer";

		time.Now += TimeSpan.FromHours(2);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		await index.GetAsync();
		await WaitForIndex(index, x => x.Check == "20001:newer");
		await index.Settled;

		Assert.Equal(pages, _pages); // no page of every title
		Assert.Equal(6, asked.Value); // 4,201 saved since: 200, then five pages of 1,000
		Assert.Equal(20001, index.Current!.Entries.Count);
		Assert.Equal("Film 00010, refreshed", index.Current.Entries.Single(x => x.Id == _films[10].Id).Name);
		Assert.False(index.Current.RemovalsDue);
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: 1 titles added and 4200 changed since the last read, read in 7 requests", StringComparison.Ordinal));
	}

	// a library added within the day is read whole on its own (its titles' save dates can be old: a library the user was just
	// given), the others as what changed; a library removed takes its titles with it, and a title it shared with another stays
	// there. 1.8.1 read every title of every library for either
	[Fact]
	public async Task ALibraryAddedOrRemovedWithinTheDay_IsReadOnItsOwn_NotEveryTitle()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var asked = SavedSince();
		var kidsId = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000190");
		List<BaseItemDto> kids = [_films[5], Film(3000), Film(3001)]; // one shared with Movies, two of its own, saved long ago
		var (moviesPages, kidsPages) = (0, 0);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, int start, int limit, bool total, CancellationToken _) =>
			{
				var films = library.Id == kidsId ? kids : _films;
				if (library.Id == kidsId)
				{
					Interlocked.Increment(ref kidsPages);
				}
				else
				{
					Interlocked.Increment(ref moviesPages);
				}

				return new BaseItemDtoQueryResult { Items = [.. films.Skip(start).Take(limit)], TotalRecordCount = total ? films.Count : null };
			});
		_client.Setup(x => x.GetLibraryTitleCount(It.IsAny<BaseItemDto>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, CancellationToken _) => library.Id == kidsId ? kids.Count : _films.Count);
		await ReadAndSave(time);
		var movies = moviesPages;

		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([Library(MoviesId, "Movies"), Library(kidsId, "Kids")]);
		_check = "2502:kids";
		time.Now += TimeSpan.FromHours(2);
		using (var added = Index(time))
		{
			await added.GetAsync();
			await WaitForIndex(added, x => x.Check == "2502:kids");
			await added.Settled;
			var now = added.Current!;
			Assert.Equal((movies, 1, 1), (moviesPages, kidsPages, asked.Value)); // Kids' one page, Movies' titles saved since
			Assert.Equal([MoviesId, kidsId], now.Libraries.Select(x => x.Id));
			Assert.Equal(2502, now.Entries.Count);
			Assert.True(now.Entries.Single(x => x.Id == _films[5].Id).InLibrary(kidsId));
			Assert.False(now.RemovalsDue);
		}

		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([Library(kidsId, "Kids")]); // Movies no longer the user's
		_check = "3:kids";
		time.Now += TimeSpan.FromHours(2);
		using var removed = Index(time);
		await removed.GetAsync();
		await WaitForIndex(removed, x => x.Check == "3:kids");
		await removed.Settled;
		var left = removed.Current!;
		Assert.Equal((movies, 1), (moviesPages, kidsPages)); // no page of every title
		Assert.Equal([kidsId], left.Libraries.Select(x => x.Id));
		Assert.Equal(["Film 00005", "Film 03000", "Film 03001"], left.Entries.Select(x => x.Name).Order());
		var shared = left.Entries.Single(x => x.Id == _films[5].Id);
		Assert.Equal((kidsId, true, false), (shared.LibraryId, shared.InLibrary(kidsId), shared.InLibrary(MoviesId)));
		Assert.False(left.RemovalsDue); // a library gone is a removal this read can name
	}

	// what changed is asked from the last read that brought the titles up to date (a read of every title, or of what changed),
	// less the day's margin; it asked from the last read of every title, so a busy server's every start read up to two days of
	// saves. And 1.8.1's index (revision 3) takes what changed within the day and stays revision 3 (its titles keep the
	// narrower tags until the day's read brings revision 4): 1.8.1 read it whole for any change
	[Fact]
	public async Task WhatChangedIsAskedFromTheLastRead_AndA181IndexTakesItAndStaysRevision3()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var asked = SavedSince();
		var first = await ReadAndSave(time);
		var pages = _pages;
		await WaitForSaved(x => x.Contains("\"Revision\":4", StringComparison.Ordinal));
		var file = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single();
		await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"Revision\":4", "\"Revision\":3", StringComparison.Ordinal)); // 1.8.1's

		_films.Add(Added(2500, time.Now + TimeSpan.FromHours(1), time.Now + TimeSpan.FromHours(1)));
		_check = "2501:newer";
		time.Now += TimeSpan.FromHours(2);
		var secondAt = time.Now;
		using (var second = Index(time))
		{
			Assert.False((await second.GetAsync())!.KeywordsKnown);
			await WaitForIndex(second, x => x.Check == "2501:newer");
			await second.Settled;
			Assert.Equal(pages, _pages);
			Assert.Equal(first.ReadAt - IndexRead.SavedSinceMargin, new DateTimeOffset(Interlocked.Read(ref _savedSinceAsked), TimeSpan.Zero));
			Assert.False(second.Current!.KeywordsKnown);
			Assert.Equal(secondAt, second.Current.TitlesAt);
		}

		await WaitForSaved(x => x.Contains("\"Check\":\"2501:newer\"", StringComparison.Ordinal) && x.Contains("\"Revision\":3", StringComparison.Ordinal));

		_films.Add(Added(2501, time.Now + TimeSpan.FromHours(1), time.Now + TimeSpan.FromHours(1)));
		_check = "2502:newer";
		time.Now += TimeSpan.FromHours(2);
		using var third = Index(time);
		await third.GetAsync();
		await WaitForIndex(third, x => x.Check == "2502:newer");
		await third.Settled;
		Assert.Equal(secondAt - IndexRead.SavedSinceMargin, new DateTimeOffset(Interlocked.Read(ref _savedSinceAsked), TimeSpan.Zero)); // from the second read
		Assert.Equal((pages, 2502), (_pages, third.Current!.Entries.Count));
	}
}
