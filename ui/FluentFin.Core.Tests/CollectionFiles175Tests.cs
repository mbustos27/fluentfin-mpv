using System.Diagnostics;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Jellyfin.Sdk.WebSockets;
using Moq;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (stream fx1): the three collection files' Minors from 1.7.3's reviews, which 1.8's collection builders start
/// from: <see cref="SmartCollectionCatalog.Find"/> collapses an index once for a batch of pins (M1's review); the retired pins
/// go in one locked store call, and a library's presets strip is one build of the catalog's (M2's review).
/// </summary>
public sealed class CollectionFiles175Tests(ITestOutputHelper output) : IDisposable
{
	private static readonly DateTimeOffset November = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
	private static readonly Guid MoviesId = Guid.NewGuid();
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-fx175-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private HomeCache? _homeCache;

	public void Dispose()
	{
		_homeCache?.Dispose();
		TestFolder.Delete(_folder);
	}

	// the signed-in user's store of pins and saved filters, in this test's folder (a second one reads the same file)
	private SmartCollectionStore Store()
	{
		_homeCache ??= new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns("http://server");
		client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000175"));
		return new SmartCollectionStore(client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
	}

	// an index at the shape of the server the owner uses: films of six decades and four countries (Japan the most, its home),
	// genre pairs, a show a tenth
	private static IndexSnapshot BigIndex(int titles)
	{
		string[] genres = ["Comedy", "Drama", "Horror", "Action", "Romance", "Thriller"];
		string[] countries = ["France", "Japan", "Italy", "Mexico"];
		var entries = Enumerable.Range(0, titles).Select(i => new IndexEntry
		{
			Id = Guid.Parse($"{i:D8}-0175-0000-0000-000000000001"),
			Name = $"Title {i:D5}",
			Kind = i % 10 == 0 ? IndexKind.Series : IndexKind.Movie,
			Year = 1960 + (i % 60),
			Genres = [genres[i % 6], genres[(i / 6) % 6]],
			Countries = [countries[i % 4]],
			Rating = 5 + (i % 50) / 10.0,
			RunTimeTicks = TimeSpan.FromMinutes(80 + (i % 60)).Ticks,
			Tmdb = $"{i % (titles - 50)}", // the last 50 are copies of the first 50: there is something to collapse
			PrimaryTag = "p",
		}).ToList();
		return new IndexSnapshot([], entries, new Dictionary<Guid, SeriesPlays>(), HasPeople: false, November);
	}

	// how often BuildContext collapsed this index's own entries (other tests' collapses run beside it and aren't counted)
	private static int Collapses(IndexSnapshot index, Action act) => Collapses(entries => ReferenceEquals(entries, index.Entries), act);

	// how often BuildContext collapsed entries that mine says are this test's (a library's own: a list made inside the call)
	private static int Collapses(Func<IReadOnlyList<IndexEntry>, bool> mine, Action act)
	{
		var count = 0;
		void Count(IReadOnlyList<IndexEntry> entries)
		{
			if (mine(entries))
			{
				Interlocked.Increment(ref count);
			}
		}

		BuildContext.Collapsing += Count;
		try
		{
			act();
		}
		finally
		{
			BuildContext.Collapsing -= Count;
		}

		return count;
	}

	// --- SmartCollectionCatalog.Find: the index collapsed once a batch --------------------------------------------------------

	[Fact]
	public void ABatchOfPins_FoundByTheirIds_CollapsesTheIndexOnce_AndFindsWhatEachFoundAlone()
	{
		// M1's review: the Collections page's PinnedOnDemand asks Find once a pin, and each collapsed the whole index again
		// (about 170 ms cold a pin on 36,000 entries)
		var index = BigIndex(36_000);
		string[] pins = ["decade:movie:1980", "decade:movie:1990", "country:france", "country:italy", "genres:movie:comedy+drama", "genres:movie:horror+thriller", "decade:series:2000", "country:nowhere"];

		var watch = Stopwatch.StartNew();
		List<SmartCollection?> found = [];
		var collapses = Collapses(index, () => found.AddRange(pins.Select(id => SmartCollectionCatalog.Find(index, id, November, whole: true))));
		output.WriteLine($"{pins.Length} pins on {index.Entries.Count:N0} entries: {watch.ElapsedMilliseconds} ms, {collapses} collapse(s)");

		Assert.Equal(1, collapses);
		Assert.Equal(7, found.Count(x => x is not null)); // "country:nowhere" is none
		// the same collections as each made alone, on an index of its own (the collapse is the index's, not the call's)
		foreach (var (id, made) in pins.Zip(found))
		{
			var alone = SmartCollectionCatalog.Find(index with { }, id, November, whole: true);
			Assert.Equal(alone?.ItemIds, made?.ItemIds);
			Assert.Equal(alone?.Title, made?.Title);
		}

		// another index (a read again) is collapsed afresh: what a batch keeps is never another index's
		var next = BigIndex(500);
		Assert.Equal(1, Collapses(next, () => Assert.NotNull(SmartCollectionCatalog.Find(next, "decade:movie:1980", November))));
	}

	// --- RetiredCollections.ForgetAsync: one locked store call ------------------------------------------------------------

	[Fact]
	public async Task RetiredPins_RemovedByHomeAndTheCollectionsPageAtOnce_AreCountedAndSaidOnce_AndTheFileWrittenOnce()
	{
		// M2's review: Home's start-up call and the Collections page's can run together on a first visit; each listed the same
		// pins, counted them, and said "N removed", and the file was written once a pin
		var store = Store();
		foreach (var pin in new[] { "director:anareyes", "actor:claraquill", "studio:northline", "network:hbo", "franchise:0", "decade:movie:1990" })
		{
			await store.SetFavorite(pin, true);
		}

		var byAna = await store.SaveFilter("By Ana", new SavedFilter(MoviesId, "Movies", PresetId: "nixie:director:anareyes", PresetLabel: "Ana Reyes"));
		await store.SetFavorite(byAna.Id, true); // a pinned saved filter of a kind that went: its pin goes with it
		await store.SaveFilter("North Line, unwatched", new SavedFilter(MoviesId, "Movies", Unwatched: true, PresetId: "nixie:studio:northline", PresetLabel: "North Line"));
		await store.SaveFilter("Nineties", new SavedFilter(MoviesId, "Movies", PresetId: "nixie:decade:movie:1990", PresetLabel: "1990s"));
		var writes = 0;
		store.Changed += (_, _) => Interlocked.Increment(ref writes);
		var log = new CoreListLogger<CollectionFiles175Tests>();

		var both = await Task.WhenAll(Task.Run(() => RetiredCollections.ForgetAsync(store, log)), Task.Run(() => RetiredCollections.ForgetAsync(store, log)));

		Assert.Equal(6, both.Sum()); // four pins and two saved filters, once between the two calls
		Assert.Contains(0, both);
		Assert.Equal(1, writes);
		var said = Assert.Single(log.Entries, x => x.Message.Contains("were removed", StringComparison.Ordinal));
		Assert.Equal("Made by Nixie: 6 pinned or saved collections of directors, actors, studios or networks were removed (Nixie no longer makes them)", said.Message);
		Assert.Equal(new HashSet<string> { "franchise:0", "decade:movie:1990" }, await store.FavoritesAsync());
		Assert.Equal(["Nineties"], (await store.SavedAsync()).Select(x => x.Title));
		// as kept on this PC (another store reads the file), and nothing left for a later start
		var reread = Store();
		Assert.Equal(new HashSet<string> { "franchise:0", "decade:movie:1990" }, await reread.FavoritesAsync());
		Assert.Equal(["Nineties"], (await reread.SavedAsync()).Select(x => x.Title));
		Assert.Equal(0, await RetiredCollections.ForgetAsync(reread, log));
		Assert.Single(log.Entries, x => x.Message.Contains("were removed", StringComparison.Ordinal));
	}

	// --- NixiePresets.For: one build of the catalog's ----------------------------------------------------------------------

	[Fact]
	public void ALibrarysPresets_AreOneBuildOfTheCatalogs_TheLibrarysTitlesCollapsedOnce_TheKindsTurnedOffLeftOut()
	{
		// M2's review: the strip's decades, genre mixes and countries were built in a loop of their own (their own context, so
		// the library's titles were collapsed a second time; its own catch, and no gate counter)
		var index = BigIndex(2_000);
		bool Library(IReadOnlyList<IndexEntry> entries) => entries.Count == index.Entries.Count && entries.Count > 0 && entries[0].Id == index.Entries[0].Id && !ReferenceEquals(entries, index.Entries);

		IReadOnlyList<NixiePreset> presets = [];
		var collapses = Collapses(Library, () => presets = NixiePresets.For(index, Guid.Empty, November, most: 100));

		Assert.Equal(1, collapses);
		// the published kinds and the strip's own, the strip's order (one of each kind first)
		var kinds = presets.Select(x => x.Kind).Distinct().ToList();
		Assert.Contains(SmartCollectionKind.Decade, kinds);
		Assert.Contains(SmartCollectionKind.GenreMix, kinds);
		Assert.Contains(SmartCollectionKind.Country, kinds);
		Assert.Contains(SmartCollectionKind.ShortFilms, kinds);
		Assert.Equal(kinds.OrderBy(k => NixiePresets.Order.ToList().IndexOf(k)), presets.Take(kinds.Count).Select(x => x.Kind));
		Assert.DoesNotContain(presets, x => RetiredCollections.Gone.Contains(x.Kind));
		// as each kind's collections are on demand, whole (a saved filter of the chip keeps the same titles)
		var eighties = Assert.Single(presets, x => x.Id == "nixie:decade:movie:1980");
		Assert.True(eighties.Items.SetEquals(SmartCollectionCatalog.Find(index, "decade:movie:1980", November, whole: true)!.ItemIds));
		var mixes = presets.Count(x => x.Kind == SmartCollectionKind.GenreMix);
		Assert.True(mixes > GenreMixBuilder.Most, $"{mixes} genre mixes: the strip's are whole, past the published twelve");

		// a strip kind and a published kind turned off in Settings: left out, the others as they were
		var off = NixiePresets.For(index, Guid.Empty, November, kindsOff: new HashSet<SmartCollectionKind> { SmartCollectionKind.Country, SmartCollectionKind.ShortFilms }, most: 100);
		Assert.DoesNotContain(off, x => x.Kind is SmartCollectionKind.Country or SmartCollectionKind.ShortFilms);
		Assert.Equal(presets.Where(x => x.Kind is not (SmartCollectionKind.Country or SmartCollectionKind.ShortFilms)).Select(x => x.Id), off.Select(x => x.Id));
	}
}
