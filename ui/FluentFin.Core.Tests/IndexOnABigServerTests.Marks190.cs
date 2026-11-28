using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the index's load, the coordinator's review): with a read of every title at most once a day, a play on another
/// device waited up to a day for the index (More like, Unfinished series, the watched filters), R included. The user's marks
/// come at each start and with each read of what changed now: the films and episodes played lately, latest first, the
/// favourites, and the shows played counted again (IndexRead.MarksAsync). MinDateLastSavedForUser, the request first
/// planned, follows the titles' own save date on 10.11.11 and 12.4.0 (tools\qa-servers\live-user-data-since.ps1).
/// </summary>
public sealed partial class IndexOnABigServerTests
{
	private readonly List<BaseItemDto> _shows = [];
	private readonly List<BaseItemDto> _episodesPlayed = [];
	private readonly Dictionary<Guid, int> _playedEpisodes = [];
	private bool _marksIgnored; // the server ignores the marks' filter and order: every film, as it comes

	// the server's answers to the marks' requests (played lately, latest first; the favourites; a show's played episodes; the
	// shows' user data), counted
	private System.Runtime.CompilerServices.StrongBox<int> Marks()
	{
		var asked = new System.Runtime.CompilerServices.StrongBox<int>();
		_client.Setup(x => x.GetPlayedLatelyPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((int start, int limit, CancellationToken _) =>
			{
				Interlocked.Increment(ref asked.Value);
				var played = _marksIgnored ? [.. _films] : _films.Concat(_episodesPlayed).Where(x => x.UserData?.Played == true)
					.OrderByDescending(x => x.UserData!.LastPlayedDate ?? DateTimeOffset.MinValue).ToList();
				return new BaseItemDtoQueryResult { Items = [.. played.Skip(start).Take(limit)] };
			});
		_client.Setup(x => x.GetFavoritesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((int start, int limit, CancellationToken _) =>
			{
				Interlocked.Increment(ref asked.Value);
				var favourites = _films.Concat(_shows).Where(x => _marksIgnored || x.UserData?.IsFavorite == true).ToList();
				return new BaseItemDtoQueryResult { Items = [.. favourites.Skip(start).Take(limit)], TotalRecordCount = favourites.Count };
			});
		_client.Setup(x => x.GetPlayedEpisodeCount(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((Guid show, CancellationToken _) =>
			{
				Interlocked.Increment(ref asked.Value);
				return _playedEpisodes.GetValueOrDefault(show);
			});
		_client.Setup(x => x.GetUserDataByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) =>
			{
				Interlocked.Increment(ref asked.Value);
				return (IReadOnlyList<BaseItemDto>?)[.. _shows.Where(x => ids.Contains(x.Id!.Value))];
			});
		return asked;
	}

	private static UserItemDataDto PlayedAt(DateTimeOffset when, int count = 1) => new() { Played = true, PlayCount = count, LastPlayedDate = when };

	// at the next start, after the server's check (which saw no library change): the film played on another device and the
	// favourite, in two requests; not asked again in the run; and a play while Nixie is open shows after R, with what changed
	// and no read of every title
	[Fact]
	public async Task APlayOnAnotherDevice_ShowsAtTheNextStart_AndAfterR_WithoutAWholeRead()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var since = SavedSince();
		var marks = Marks();
		var first = await ReadAndSave(time);
		var pages = _pages;
		var (watched, favourite, later) = (_films[7].Id!.Value, _films[9].Id!.Value, _films[8].Id!.Value);
		Assert.False(first.Entries.Single(x => x.Id == watched).Played);
		Assert.Equal(0, marks.Value); // the read of every title brought them

		_films[7].UserData = PlayedAt(time.Now + TimeSpan.FromMinutes(30)); // played on another device
		_films[9].UserData = new UserItemDataDto { Played = false, IsFavorite = true }; // and made a favourite there
		time.Now += TimeSpan.FromHours(1);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		await index.GetAsync();
		await WaitForIndex(index, x => x.Entries.Single(e => e.Id == watched).Played);
		await index.Checking;
		await index.Settled;
		var entry = index.Current!.Entries.Single(x => x.Id == watched);
		Assert.Equal((true, 1, (DateTimeOffset?)(time.Now - TimeSpan.FromMinutes(30))), (entry.Played, entry.PlayCount, entry.LastPlayed));
		Assert.True(index.Current.Entries.Single(x => x.Id == favourite).Favorite);
		Assert.Equal((pages, 0, 3), (_pages, since.Value, marks.Value)); // no title read: the played lately, the favourites' count and their page
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: marks since the last read: 1 films and 0 shows played, favourites read, 2 titles changed, in 3 requests", StringComparison.Ordinal));
		await WaitForSaved(x => x.Contains("\"PlayCount\":1", StringComparison.Ordinal)); // and saved

		time.Now += TimeSpan.FromMinutes(10);
		await index.SavedAsync(); // another page, the same run
		await Task.Delay(50);
		await index.Settled;
		Assert.Equal(3, marks.Value);

		_films[8].UserData = PlayedAt(time.Now - TimeSpan.FromMinutes(1)); // played on another device while Nixie is open
		await index.RefreshAsync(); // R
		await index.Settled;
		Assert.True(index.Current!.Entries.Single(x => x.Id == later).Played);
		Assert.Equal(pages, _pages); // no read of every title
		Assert.Equal(1, since.Value); // what changed, and the marks with it
		Assert.Equal(6, marks.Value);
	}

	// an episode played on another device: its show's played episodes counted again and its own marks (Unfinished series,
	// More like), after R and with no read of every title; a show played before the time the index has isn't counted again
	[Fact]
	public async Task AnEpisodePlayedOnAnotherDevice_CountsItsShowAgain_AfterR()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var showsId = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000190");
		var show = new BaseItemDto { Id = Guid.Parse("bbbbbbbb-0190-0000-0000-000000000001"), Name = "Kestrel Bay", SortName = "kestrel bay", Type = BaseItemDto_Type.Series, UserData = new UserItemDataDto { UnplayedItemCount = 10 } };
		_shows.Add(show);
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
		var since = SavedSince();
		_client.Setup(x => x.GetLibraryTitleCount(It.IsAny<BaseItemDto>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, CancellationToken _) => library.Id == showsId ? _shows.Count : _films.Count);
		var marks = Marks();
		await ReadAndSave(time);
		var pages = _pages;
		time.Now += TimeSpan.FromHours(1);
		using var index = Index(time);
		await index.GetAsync();
		await index.Checking;
		await index.Settled; // the start's marks: nothing played

		Episode(show, 1, time.Now - TimeSpan.FromMinutes(20)); // played on another device
		show.UserData = new UserItemDataDto { UnplayedItemCount = 9 };
		_playedEpisodes[show.Id!.Value] = 1;
		var asked = marks.Value;
		await index.RefreshAsync(); // R
		await index.Settled;

		Assert.Equal(new SeriesPlays(1, time.Now - TimeSpan.FromMinutes(20)), index.Current!.SeriesPlays[show.Id.Value]);
		Assert.Equal(9, index.Current.Entries.Single(x => x.Id == show.Id).Unplayed);
		Assert.Equal(pages, _pages);
		Assert.Equal(4, marks.Value - asked); // played lately, the favourites' count (none), the show's count, its user data

		time.Now += TimeSpan.FromMinutes(5);
		asked = marks.Value;
		await index.RefreshAsync(); // R again, nothing new: the show isn't counted again
		await index.Settled;
		Assert.Equal(2, marks.Value - asked);
		Assert.True(since.Value >= 2);
	}

	private void Episode(BaseItemDto show, int number, DateTimeOffset played) =>
		_episodesPlayed.Add(new BaseItemDto { Id = Guid.Parse($"cccccccc-0190-0000-0000-{number:D12}"), Type = BaseItemDto_Type.Episode, SeriesId = show.Id, UserData = PlayedAt(played) });

	// a server that ignores the marks' filter and order (every film, as it comes) is caught at the first page: nothing applied,
	// no more marks asked this run (R reads what changed alone), and never a read of every title in disguise
	[Fact]
	public async Task AServerThatIgnoresTheMarksFilter_IsAskedOnce_NothingApplied_NeverEveryTitle()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var since = SavedSince();
		var marks = Marks();
		_marksIgnored = true;
		await ReadAndSave(time);
		var pages = _pages;
		time.Now += TimeSpan.FromHours(1);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(time, log);
		var shown = await index.GetAsync();
		await index.Checking;
		await index.Settled;
		Assert.Equal(1, marks.Value); // one page of the played lately, which held a film not played
		Assert.Same(shown, index.Current); // nothing applied, nothing published
		Assert.Contains(log.Entries, x => x.Message.StartsWith("Library index: the server didn't answer the marks as asked (the server didn't answer the titles played, latest first)", StringComparison.Ordinal));

		await index.RefreshAsync(); // R: what changed, no marks
		await index.Settled;
		Assert.Equal((1, pages, 1), (marks.Value, _pages, since.Value));
	}
}
