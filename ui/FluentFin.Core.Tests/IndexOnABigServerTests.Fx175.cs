using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.7.5 (stream fx1): the library index's Minors from stream I's review of 1.7.3, and BH-171-m5.</summary>
public sealed partial class IndexOnABigServerTests
{
	// stream I's review: 1.7.3 forgot the older format when an upgrade read began, so one that failed wasn't tried again until
	// a restart (1.7.2's index, without LibraryIds, stayed till then or the day's read). Tried again after the hour now, in the
	// same run, and not at every visit before it
	[Fact]
	public async Task AFailedUpgradeRead_IsTriedAgainAfterTheHour_InTheSameRun_NotAtEveryVisitBefore()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		var folder = Path.Combine(_folder, "LibraryIndex");
		var file = Directory.GetFiles(folder, "*.json").Single();
		await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace(",\"Revision\":4", "", StringComparison.Ordinal)); // 1.7.2's index
		var note = Directory.GetFiles(Path.Combine(folder, "Reads"), "*.json").Single();
		await File.WriteAllTextAsync(note, (await File.ReadAllTextAsync(note)).Replace(",\"Revision\":4", "", StringComparison.Ordinal)); // and its note

		// the first start after the update: the upgrade read begins at once, and the server goes away part way
		time.Now += TimeSpan.FromMinutes(10);
		Pages(start => start == 0 ? Task.CompletedTask : Task.FromException(new HttpRequestException("the server went away")));
		var before = _pages;
		using var index = Index(time);
		Assert.NotNull(await index.GetAsync());
		for (var i = 0; i < 200 && _pages == before; i++)
		{
			await Task.Delay(10);
		}

		await index.Settled;
		Assert.True(_pages > before, "the upgrade read began");
		Assert.DoesNotContain("\"Revision\":4", await File.ReadAllTextAsync(file), StringComparison.Ordinal); // it never came in

		// pages opened within the hour: the old index shows, nothing is read (a failing server isn't asked at every visit)
		Pages(_ => Task.CompletedTask);
		var failed = _pages;
		foreach (var minutes in new[] { 1, 20, 49 })
		{
			time.Now = new DateTimeOffset(2026, 11, 22, 8, 10, 0, TimeSpan.Zero) + TimeSpan.FromMinutes(minutes);
			Assert.NotNull(await index.SavedAsync());
			await Task.Delay(50);
			await index.Settled;
			Assert.Equal(failed, _pages);
		}

		// past the hour of the failed read: read again behind the shown index, and it is in
		time.Now = new DateTimeOffset(2026, 11, 22, 9, 11, 0, TimeSpan.Zero);
		Assert.NotNull(await index.SavedAsync());
		for (var i = 0; i < 200 && _pages == failed; i++)
		{
			await Task.Delay(10);
		}

		await index.Settled;
		Assert.True(_pages > failed, "the upgrade read was tried again after the hour, without a restart");
		await WaitForSaved(x => x.Contains("\"Revision\":4", StringComparison.Ordinal));
		Assert.True(index.Current!.BackingKnown);

		// and once it is in, nothing more: the next visit reads nothing
		var done = _pages;
		time.Now += TimeSpan.FromMinutes(70);
		Assert.NotNull(await index.SavedAsync());
		await Task.Delay(50);
		await index.Settled;
		await index.Checking;
		Assert.Equal(done, _pages);
	}

	// stream I's review (reproduced there): played episodes that keep failing were asked on their own before the server's
	// check, at most once an hour, and that read counted as "read lately": every hour it won again, and a change the check
	// confirmed waited for the day's read (up to 24 h; 1.7.2 read it within the hour). The check comes first now
	[Fact]
	public async Task PlayedEpisodesThatKeepFailing_DontHoldBackALibraryChangeTheCheckConfirms()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var asked = MoviesAndShows(() => false); // the played episodes never come
		var since = SavedSince(); // 1.9: a change is read as what changed
		var first = await ReadAndSave(time);
		Assert.True(first.PlaysDue);
		await WaitForSaved(x => x.Contains("\"PlaysDue\":true", StringComparison.Ordinal));
		_check = "2501:newer"; // a title was added meanwhile
		var (pages, plays) = (_pages, asked.Value);

		// a start two hours later: the check first, which confirms the change, and the titles are read at once
		time.Now += TimeSpan.FromHours(2);
		using var later = Index(time);
		Assert.NotNull(await later.GetAsync());
		for (var i = 0; i < 400 && since.Value == 0; i++)
		{
			await Task.Delay(10);
		}

		await later.Checking;
		await later.Settled;
		Assert.True(since.Value > 0, "the change the check confirmed is read at once, not after the day's read");
		Assert.Equal("2501:newer", later.Current!.Check);
		Assert.Equal(first.ReadAt, later.Current.ReadAt); // a change's read keeps the day's date

		// no change: the played episodes are asked after the check, still at most once an hour
		using var next = Index(time);
		time.Now += TimeSpan.FromHours(1.5);
		var (pages2, plays2) = (_pages, asked.Value);
		Assert.NotNull(await next.GetAsync());
		for (var i = 0; i < 400 && asked.Value == plays2; i++)
		{
			await Task.Delay(10);
		}

		await next.Checking;
		await next.Settled;
		Assert.True(asked.Value > plays2, "the played episodes, after a check that found nothing");
		Assert.Equal(pages2, _pages); // no title asked
		Assert.True(asked.Value > plays);
	}

	// stream I's review (later, not a fault): LibraryIds cost about 1.8 MB on 34,000 titles, nearly every one in one library.
	// Saved only for a title in another library than its first now; the loaded index answers InLibrary as the read did
	[Fact]
	public async Task TheSavedIndex_KeepsLibraryIds_OnlyForTitlesInMoreThanOneLibrary()
	{
		var kidsId = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000175");
		List<BaseItemDto> kids = [_films[5], _films[10], _films[2400], Film(3000)]; // three shared, one of its own
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([Library(MoviesId, "Movies"), Library(kidsId, "Kids")]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, int start, int limit, bool total, CancellationToken _) =>
			{
				var films = library.Id == kidsId ? kids : _films;
				return new BaseItemDtoQueryResult { Items = [.. films.Skip(start).Take(limit)], TotalRecordCount = total ? films.Count : null };
			});

		var read = await ReadAndSave();
		await WaitForSaved(x => x.Contains("\"Revision\":4", StringComparison.Ordinal));
		var text = await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single());

		Assert.Equal(3, text.Split("\"LibraryIds\":").Length - 1); // the three shared titles only, of 2,501
		Assert.Contains($"\"LibraryIds\":[\"{MoviesId}\",\"{kidsId}\"]", text, StringComparison.Ordinal);
		using var restart = Index();
		var saved = (await restart.GetAsync())!;
		Assert.Equal(read.Entries.Count, saved.Entries.Count);
		foreach (var library in new[] { MoviesId, kidsId })
		{
			Assert.Equal(read.Entries.Where(x => x.InLibrary(library)).Select(x => x.Id), saved.Entries.Where(x => x.InLibrary(library)).Select(x => x.Id));
		}

		Assert.Equal([MoviesId, kidsId], saved.Entries.Single(x => x.Name == "Film 02400").LibraryIds);
		var own = saved.Entries.Single(x => x.Name == "Film 03000");
		Assert.Equal((kidsId, 0), (own.LibraryId, own.LibraryIds.Length)); // its LibraryId alone
		Assert.True(saved.BackingKnown); // still this revision: nothing read again for it
		// a library's presets made from the saved index: the same as from the read
		Assert.Equal(NixiePresets.For(read, kidsId, read.ReadAt, most: 50).Select(x => (x.Id, x.Items.Count)), NixiePresets.For(saved, kidsId, read.ReadAt, most: 50).Select(x => (x.Id, x.Items.Count)));
	}

	// --- BH-171-m5: a library change read as the titles saved since the last read ---------------------------------------------

	private static readonly DateTimeOffset FirstAdded = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

	// when the server last saved each title (DateLastSaved: when it was added or changed, whatever its DateCreated); every
	// film of the fixture was saved long before the first read
	private readonly Dictionary<Guid, DateTimeOffset> _savedAt = [];

	// every film dated as added a minute after the one before; the server's pages of the titles saved since a time
	// (MinDateLastSaved), counted. 1.8.1 (BH-175-m3; the review): and each indexed library's count now (Movies: the films; a
	// library the index skips, _elsewhere, never asked), counted too
	private System.Runtime.CompilerServices.StrongBox<int> SavedSince()
	{
		for (var i = 0; i < _films.Count; i++)
		{
			_films[i].DateCreated ??= FirstAdded.AddMinutes(i);
		}

		var asked = new System.Runtime.CompilerServices.StrongBox<int>();
		_client.Setup(x => x.GetLibraryIndexPageSavedSince(It.IsAny<BaseItemDto>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, DateTimeOffset since, int start, int limit, bool total, CancellationToken _) =>
			{
				Interlocked.Increment(ref asked.Value);
				Interlocked.Exchange(ref _savedSinceAsked, since.Ticks);
				List<BaseItemDto> saved = library.Id != MoviesId ? [] : _films.Where(x => _savedAt.GetValueOrDefault(x.Id!.Value, FirstAdded) >= since).OrderBy(x => x.SortName).ToList(); // 1.9: Movies' films only
				return new BaseItemDtoQueryResult { Items = [.. saved.Skip(start).Take(limit)], TotalRecordCount = total ? saved.Count : null };
			});
		_client.Setup(x => x.GetLibraryTitleCount(It.IsAny<BaseItemDto>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, CancellationToken _) =>
			{
				Interlocked.Increment(ref _counted);
				return library.Id == MoviesId ? _films.Count : 0;
			});
		return asked;
	}

	// 1.8.1 (BH-175-m3): the films of a library the index skips (a mixed library: never asked by the index), and the requests of
	// a library's count
	private readonly List<BaseItemDto> _elsewhere = [];
	private int _counted;
	private long _savedSinceAsked; // 1.9: the time the last page of the titles saved since asked from (its ticks)

	// a film added to the library at a scan an hour after the first read: saved then, dated as the server dates it
	private BaseItemDto Added(int i, DateTimeOffset dated, DateTimeOffset scanned)
	{
		var film = Film(i);
		film.DateCreated = dated;
		_savedAt[film.Id!.Value] = scanned;
		return film;
	}

	// BH-171-m5: a library that changes every day (most big servers) meant a read of every title about once a start (27
	// requests here; 28 and 9.5 s on the throwaway 25,736-film server). The titles saved since the last read are read
	// instead: one request. The 1.7.5 review: a title added with an older date (its file's, on a server that dates by the
	// file) is found too
	[Fact]
	public async Task ALibraryChange_OfTitlesAdded_ReadsOnlyThoseSavedSinceTheLastRead()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var asked = SavedSince();
		var first = await ReadAndSave(time);
		var pages = _pages;
		var scan = time.Now + TimeSpan.FromHours(1);
		_films.AddRange([Added(2500, scan, scan), Added(2501, FirstAdded.AddDays(-400), scan), Added(2502, FirstAdded.AddMinutes(2499), scan)]); // three added
		_films[10].Name = "Film 00010, the director's cut"; // and one changed (saved again)
		_savedAt[_films[10].Id!.Value] = scan;
		_check = "2503:newer";

		time.Now += TimeSpan.FromHours(2);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		await index.GetAsync();
		await WaitForIndex(index, x => x.Entries.Count == 2503);
		await index.Checking;
		await index.Settled;

		Assert.Equal(pages, _pages); // no page of every title
		Assert.Equal(1, asked.Value); // one page of the titles saved since
		Assert.Equal(1, _counted); // 1.8.1 (BH-175-m3): and Movies' count
		var now = index.Current!;
		Assert.Equal(["Film 02500", "Film 02501", "Film 02502"], now.Entries.Skip(2500).Select(x => x.Name));
		Assert.All(now.Entries.Skip(2500), x => Assert.Equal((MoviesId, true), (x.LibraryId, x.InLibrary(MoviesId))));
		Assert.Equal("Film 00010, the director's cut", now.Entries.Single(x => x.Id == _films[10].Id).Name); // as the server has it now
		Assert.Equal("2503:newer", now.Check); // what the next check is compared with
		Assert.Equal(first.ReadAt, now.ReadAt); // the day's read still comes at its time
		Assert.True(now.BackingKnown);
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: 3 titles added and 1 changed since the last read, read in 2 requests", StringComparison.Ordinal));
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: a library change read as the titles saved since the last read: 2503 titles, ", StringComparison.Ordinal));
		await WaitForSaved(x => x.Contains("\"Check\":\"2503:newer\"", StringComparison.Ordinal));
		using var restart = Index(time);
		Assert.Equal(2503, (await restart.GetAsync())!.Entries.Count); // and saved so

		// the next check: nothing changed since, nothing read
		await restart.Checking;
		await restart.Settled;
		Assert.Equal((pages, 1), (_pages, asked.Value));
	}

	// 1.9: what the titles saved since can't explain (1.7.5's and 1.8.1's read of every title): IndexOnABigServerTests.Load190.cs

	// 1.8.1 (BH-175-m3): a title added to a library the index skips (a mixed library) moves the server's check, but no indexed
	// library's count: nothing is read whole (1.7.5 read every title for it, 28 requests on the throwaway 25,736-film server),
	// and the index takes the check, with none of that library's titles
	[Fact]
	public async Task ATitleAddedInALibraryTheIndexSkips_IsSeen_AndNothingIsReadWhole()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var asked = SavedSince();
		await ReadAndSave(time);
		var pages = _pages;
		var scan = time.Now + TimeSpan.FromHours(1);
		_elsewhere.Add(Added(9002, scan, scan));
		_check = "2501:elsewhere";

		time.Now += TimeSpan.FromHours(2);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		await index.GetAsync();
		await WaitForIndex(index, x => x.Check == "2501:elsewhere");
		await index.Checking;
		await index.Settled;

		Assert.Equal(pages, _pages); // no page of every title
		Assert.Equal((1, 1), (asked.Value, _counted));
		Assert.Equal(2500, index.Current!.Entries.Count);
		Assert.DoesNotContain(index.Current.Entries, x => x.Name == "Film 09002");
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: 0 titles added and 0 changed since the last read, read in 2 requests (every title was not read)", StringComparison.Ordinal));
	}

	// 1.8.1 (the review of BH-175-m3's first fix): a title saved in a library the index skips within the day's margin before the
	// read (or only saved again since) counted as added there, so a film added to Movies was read as every title, again at each
	// check for up to a day (28 requests on a 25,800-film server). Each indexed library's own count says what changed: Movies
	// moved by its one film, read as the titles saved since
	[Fact]
	public async Task AFilmAddedToMovies_WithASaveInASkippedLibrary_IsReadAsTheTitlesSavedSince()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var asked = SavedSince();
		_elsewhere.Add(Added(9001, time.Now - TimeSpan.FromHours(2), time.Now - TimeSpan.FromHours(2))); // saved within the margin before the read
		_check = "2501:mixed"; // the server's check counts it: Movies' 2,500 and the mixed library's one
		await ReadAndSave(time);
		var pages = _pages;
		var scan = time.Now + TimeSpan.FromHours(1);
		_films.Add(Added(2500, scan, scan)); // a film added to Movies
		_savedAt[_elsewhere[0].Id!.Value] = scan; // and the mixed library's film saved again
		_check = "2502:newer";

		time.Now += TimeSpan.FromHours(2);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		await index.GetAsync();
		await WaitForIndex(index, x => x.Entries.Count == 2501);
		await index.Checking;
		await index.Settled;

		Assert.Equal(pages, _pages); // no page of every title
		Assert.Equal(1, asked.Value);
		Assert.Contains(index.Current!.Entries, x => x.Name == "Film 02500");
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: a library change read as the titles saved since the last read: 2501 titles", StringComparison.Ordinal));
	}
}
