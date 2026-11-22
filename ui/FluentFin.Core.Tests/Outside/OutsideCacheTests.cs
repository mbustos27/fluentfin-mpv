using System.Text;
using FluentFin.Core.Outside;
using static FluentFin.Core.Tests.Outside.OutsideKit;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 keyless: the cache of public lists (Core\Outside\OutsideCache.cs): apart from any user, with its fetch date, fresh for
/// each source's refresh time, and never used or kept past its maximum age (TMDb: 6 months at most).
/// </summary>
public sealed class OutsideCacheTests : IDisposable
{
	private readonly string _root = TempFolder();
	private readonly TestClock _clock = new(Noon);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private OutsideCache Cache => new(_root, _clock.Func);

	[Fact]
	public void A_list_comes_back_with_its_fetch_date_and_stays_fresh_for_its_sources_refresh_time()
	{
		Cache.Write(OutsideSources.Wikidata, "awards-best-picture", Encoding.UTF8.GetBytes("rows"));

		var entry = Cache.Read(OutsideSources.Wikidata, "awards-best-picture")!;
		Assert.Equal("rows", Encoding.UTF8.GetString(entry.Data));
		Assert.Equal(Noon, entry.FetchedAt);
		Assert.True(Cache.IsFresh(entry));

		_clock.Now = Noon.AddDays(6.9);
		Assert.True(Cache.IsFresh(Cache.Read(OutsideSources.Wikidata, "awards-best-picture")!));
		_clock.Now = Noon.AddDays(7);
		var stale = Cache.Read(OutsideSources.Wikidata, "awards-best-picture");
		Assert.NotNull(stale); // kept, to show until a refresh succeeds
		Assert.False(Cache.IsFresh(stale));
	}

	[Theory]
	[InlineData(OutsideSourceId.TmdbExports, 7, 182)]
	[InlineData(OutsideSourceId.Wikidata, 7, 60)]
	[InlineData(OutsideSourceId.Trakt, 0.25, 1)]
	[InlineData(OutsideSourceId.TmdbTrending, 1, 7)]
	public void Each_source_has_its_refresh_time_and_maximum_age(OutsideSourceId id, double refreshDays, double maxDays)
	{
		var info = OutsideSources.Get(id);
		Assert.Equal(TimeSpan.FromDays(refreshDays), info.RefreshAfter);
		Assert.Equal(TimeSpan.FromDays(maxDays), info.MaxAge);
	}

	[Fact]
	public void A_TMDb_list_is_never_used_past_6_months_and_is_deleted_then()
	{
		Assert.True(OutsideSources.TmdbExports.MaxAge <= TimeSpan.FromDays(183)); // the API terms: 6 months at most
		Assert.True(OutsideSources.TmdbTrending.MaxAge <= TimeSpan.FromDays(183));
		Cache.Write(OutsideSources.TmdbExports, "collections", [1, 2, 3]);

		_clock.Now = Noon + OutsideSources.TmdbExports.MaxAge;
		Assert.NotNull(Cache.Read(OutsideSources.TmdbExports, "collections"));
		_clock.Now = Noon + OutsideSources.TmdbExports.MaxAge + TimeSpan.FromMinutes(1);
		Assert.Null(Cache.Read(OutsideSources.TmdbExports, "collections"));
		Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "tmdbexports")));
	}

	[Fact]
	public void The_maximum_age_comes_from_todays_rules_not_from_the_file()
	{
		Cache.Write(OutsideSources.Trakt, "trending-movies", [1]);
		var meta = Path.Combine(_root, "trakt", "trending-movies.meta.json");
		File.WriteAllText(meta, File.ReadAllText(meta).Replace("}", ",\"MaxAgeDays\":9999}", StringComparison.Ordinal));

		_clock.Now = Noon.AddHours(25);

		Assert.Null(Cache.Read(OutsideSources.Trakt, "trending-movies"));
	}

	[Fact]
	public void A_list_dated_in_the_future_is_not_trusted()
	{
		Cache.Write(OutsideSources.Wikidata, "genres-heist", [1], fetchedAt: Noon.AddDays(30));

		Assert.Null(Cache.Read(OutsideSources.Wikidata, "genres-heist"));
	}

	[Fact]
	public void Purge_deletes_lists_past_their_age_and_strays_and_keeps_the_rest()
	{
		Cache.Write(OutsideSources.Trakt, "old", [1]);
		_clock.Now = Noon.AddDays(2);
		Cache.Write(OutsideSources.Trakt, "new", [2]);
		Cache.Write(OutsideSources.Wikidata, "awards", [3]);
		File.WriteAllText(Path.Combine(_root, "wikidata", "stray.txt"), "?");
		File.WriteAllText(Path.Combine(_root, "wikidata", "orphan.dat"), "?");

		var deleted = Cache.Purge();

		Assert.Equal(4, deleted); // old.dat, old.meta.json, stray.txt, orphan.dat
		Assert.Null(Cache.Read(OutsideSources.Trakt, "old"));
		Assert.NotNull(Cache.Read(OutsideSources.Trakt, "new"));
		Assert.NotNull(Cache.Read(OutsideSources.Wikidata, "awards"));
	}

	[Fact]
	public void Clear_empties_one_source_or_all()
	{
		Cache.Write(OutsideSources.Trakt, "a", [1]);
		Cache.Write(OutsideSources.Wikidata, "b", [2]);

		Cache.Clear(OutsideSourceId.Trakt);
		Assert.Null(Cache.Read(OutsideSources.Trakt, "a"));
		Assert.NotNull(Cache.Read(OutsideSources.Wikidata, "b"));
		Cache.Clear();
		Assert.Null(Cache.Read(OutsideSources.Wikidata, "b"));
	}

	[Theory]
	[InlineData("../escape")]
	[InlineData("..")]
	[InlineData("Upper")]
	[InlineData("a/b")]
	[InlineData("a\\b")]
	[InlineData("")]
	[InlineData("c:evil")]
	public void A_list_name_cannot_leave_the_cache_folder(string name)
	{
		Assert.False(OutsideCache.IsValidName(name));
		Assert.Throws<ArgumentException>(() => Cache.Write(OutsideSources.Trakt, name, [1]));
	}

	[Fact]
	public void The_cache_is_one_folder_under_Nixies_data_folder_shared_by_every_user()
	{
		var folders = new KnownFolders(_root);
		Assert.Equal(Path.Combine(_root, "Outside"), OutsideCache.For(folders).Root);
		// a list is named by its source and list only: no server, user or library part to its path
		OutsideCache.For(folders).Write(OutsideSources.Wikidata, "awards-palme-dor", [1]);
		Assert.True(File.Exists(Path.Combine(_root, "Outside", "wikidata", "awards-palme-dor.dat")));
	}
}
