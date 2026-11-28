using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, step 1b, stream text): <see cref="MicroGenreMatcher"/>'s word-list hook. A category with a
/// summary word list keeps the titles whose summary has one of its words and fit the rest of its rule; a title not read or
/// with no summary is in none (rule 4); and nothing is made until the pass has read its kind, nor said as "matches none".
/// </summary>
public class TextMatchesInMatcherTests
{
	private static int s_next;

	private static IndexEntry Show(string name, string[]? genres = null, string? tmdb = null, bool played = false) => new()
	{
		Id = new Guid(Interlocked.Increment(ref s_next), 190, 0, new byte[8]),
		Name = name,
		Kind = IndexKind.Series,
		Genres = genres ?? ["Comedy"],
		Year = 2010,
		RunTimeTicks = TimeSpan.FromMinutes(22).Ticks,
		Tmdb = tmdb,
		Played = played,
	};

	private static readonly MicroGenre Workplace = new("tv-workplace", "Workplace TV Comedies", IndexKind.Series, "sitcom", null,
		new MicroGenreRule { GenresAll = ["Comedy"], TextAny = ["office", "boss"] }, "Comedy whose summary mentions work or an office");

	// what the pass found: each title's categories ([]: a summary with none of the words; null: no summary)
	private static TextMatches Matches(params (IndexEntry Title, string[]? Categories)[] titles) =>
		new("key", "fingerprint", DateTimeOffset.UnixEpoch, titles.ToDictionary(x => x.Title.Id, x => x.Categories));

	private static string[] Kept(IReadOnlyList<IndexEntry> entries, MicroGenre category, TextMatches text)
	{
		var titles = CanonicalTitles.Of(entries);
		var matched = MicroGenreMatcher.For(titles).Match(category, text).Titles.Select(x => x.Name).ToArray();
		// the plain rule says the same of each title (its copies' summaries too)
		Assert.Equal(titles.Entries.Where(x => MicroGenreMatcher.Fits(category, x, text, titles.CopyIds(x.Id))).Select(x => x.Name), matched);
		return matched;
	}

	[Fact]
	public void AWordList_KeepsTheTitlesWhoseSummaryHasAWord_AndFitTheRestOfTheRule()
	{
		var office = Show("Office comedy");
		var drama = Show("Office drama", genres: ["Drama"]);
		var farm = Show("Farm comedy");
		var silent = Show("No summary comedy");
		List<IndexEntry> titles = [office, drama, farm, silent];
		var text = Matches((office, [Workplace.Id]), (drama, [Workplace.Id]), (farm, []), (silent, null));

		Assert.Equal(["Office comedy"], Kept(titles, Workplace, text)); // the drama has the words, not the genre
		Assert.False(MicroGenreMatcher.Fits(Workplace, silent, text)); // no summary: in no category (rule 4)
		Assert.True(text.IsUnknown(silent.Id));
		Assert.False(text.IsKnown(silent.Id));
		Assert.Empty(Kept(titles, Workplace, TextMatches.Empty)); // nothing read: nothing kept
		Assert.Empty(MicroGenreMatcher.For(CanonicalTitles.Of(titles)).Match(Workplace).Titles); // no matches given
	}

	[Fact]
	public void ATitleInTwoLibraries_IsKept_WhenEitherCopysSummaryHasTheWords()
	{
		var first = Show("The Office", tmdb: "2316");
		var second = Show("The Office", tmdb: "2316") with { LibraryId = Guid.NewGuid() };
		var other = Show("Other");
		var text = Matches((first, []), (second, [Workplace.Id]), (other, []));

		Assert.Equal(["The Office"], Kept([first, second, other], Workplace, text));
	}

	[Fact]
	public void UntilThePassHasReadTheKind_ACategoryWaits_AndIsNotSaidToMatchNone()
	{
		var shows = Enumerable.Range(0, 100).Select(i => Show($"Show {i}")).ToList();
		var titles = CanonicalTitles.Of(shows);
		var matcher = MicroGenreMatcher.For(titles);
		// 97 read (8 with the words): under the 98 %, nothing yet
		var some = Matches([.. shows.Take(97).Select((x, i) => (x, i < 8 ? new[] { Workplace.Id } : []))]);
		var enough = Matches([.. shows.Take(98).Select((x, i) => (x, i < 8 ? new[] { Workplace.Id } : []))]);
		var noSummaries = Matches([.. shows.Select(x => (x, (string[]?)null))]);

		Assert.False(matcher.TextCovers(IndexKind.Series, some));
		Assert.Equal(0, matcher.Match(Workplace, some).Count);
		Assert.True(matcher.TextCovers(IndexKind.Series, enough));
		Assert.Equal(8, matcher.Match(Workplace, enough).Count);
		Assert.False(matcher.TextCovers(IndexKind.Series, noSummaries)); // a server with no summaries says nothing
		Assert.False(matcher.TextCovers(IndexKind.Movie, enough)); // the films weren't read

		var context = BuildContext.For(new IndexSnapshot([], shows, new Dictionary<Guid, SeriesPlays>(), false, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch);
		var catalogue = new MicroGenreCatalogue([Workplace], []);
		Assert.Equal(MicroGenreVerdict.WaitsForText, new MicroGenreBuilder(catalogue) { Text = some }.Run(context).Census.Pools.Single().Verdict);
		Assert.Equal(MicroGenreVerdict.WaitsForText, new MicroGenreBuilder(catalogue) { Text = noSummaries }.Run(context).Census.Pools.Single().Verdict);
		Assert.Equal(MicroGenreVerdict.Made, new MicroGenreBuilder(catalogue) { Text = enough }.Run(context).Census.Pools.Single().Verdict);
	}

	// the review of wip/190-text, probe G: 6 shows new since a complete pass, of 256, hid every summary category
	[Fact]
	public void OnceAKindWasReadWhole_TitlesNewSince_DontHideItsCategories()
	{
		var shows = Enumerable.Range(0, 256).Select(i => Show($"Show {i}")).ToList();
		var matcher = MicroGenreMatcher.For(CanonicalTitles.Of(shows));
		var read = shows.Take(250).ToDictionary(x => x.Id, x => (string[]?)[Workplace.Id]);
		var whole = new TextMatches("key", "fingerprint", DateTimeOffset.UnixEpoch, read) { CompleteFor = new HashSet<IndexKind> { IndexKind.Series } };
		var cut = new TextMatches("key", "fingerprint", DateTimeOffset.UnixEpoch, read); // a first pass cut short at 250
		var noSummaries = new TextMatches("key", "fingerprint", DateTimeOffset.UnixEpoch, shows.Take(250).ToDictionary(x => x.Id, x => (string[]?)null))
		{
			CompleteFor = new HashSet<IndexKind> { IndexKind.Series },
		};

		Assert.True(matcher.TextCovers(IndexKind.Series, whole)); // 250 of 256 (97.7 %): the 6 new ones wait for their read
		Assert.Equal(250, matcher.Match(Workplace, whole).Count);
		Assert.False(matcher.TextCovers(IndexKind.Series, cut)); // never read whole: the 98 % still holds
		Assert.False(matcher.TextCovers(IndexKind.Series, noSummaries)); // read whole, no summary: says nothing
		Assert.False(matcher.TextCovers(IndexKind.Movie, whole)); // the films weren't read
	}

	// the review of wip/190-text, item 6: the coverage cache was a struct of three fields, so a read beside a write could take one
	// matches' answer for another's
	[Fact]
	public void TheCoverageCache_IsOneObjectSwappedWhole_AndEachAskGetsItsOwnMatchesAnswer()
	{
		var field = typeof(MicroGenreMatcher).GetField("_covered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
		Assert.False(field.FieldType.IsValueType, field.FieldType.Name); // a reference: read and written whole

		var shows = Enumerable.Range(0, 10).Select(i => Show($"Show {i}")).ToList();
		var matcher = MicroGenreMatcher.For(CanonicalTitles.Of(shows));
		var covers = Matches([.. shows.Select(x => (x, new[] { Workplace.Id }))]);
		var short_ = Matches([.. shows.Take(5).Select(x => (x, new[] { Workplace.Id }))]); // half read: no
		var wrong = 0;
		Parallel.For(0, 40_000, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
		{
			var mine = i % 2 == 0 ? covers : short_;
			if (matcher.TextCovers(IndexKind.Series, mine) != (i % 2 == 0))
			{
				Interlocked.Increment(ref wrong);
			}
		});
		Assert.Equal(0, wrong);
	}

	[Fact]
	public void ACategoryMadeFromSummaries_SaysSoInItsReasonLine_ThenItsFacts()
	{
		var shows = Enumerable.Range(0, 40).Select(i => Show($"Show {i}")).ToList();
		var text = Matches([.. shows.Select((x, i) => (x, i < 10 ? new[] { Workplace.Id } : []))]);
		var context = BuildContext.For(new IndexSnapshot([], shows, new Dictionary<Guid, SeriesPlays>(), false, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch);

		var made = Assert.Single(new MicroGenreBuilder(new MicroGenreCatalogue([Workplace], [])) { Text = text }.Build(context));

		Assert.Equal([TextPass.ReasonFact, Workplace.Reason], made.Reason!.Facts);
		Assert.StartsWith("From each title's summary · Comedy whose summary mentions work or an office · 10 shows", made.Reason.Text, StringComparison.Ordinal);
		Assert.Equal(10, made.ItemIds.Count);
	}

	[Fact]
	public void TheAppsMatches_AreUsed_OnlyForTheirOwnUsersIndex()
	{
		var mine = new IndexSnapshot([], [], new Dictionary<Guid, SeriesPlays>(), false, DateTimeOffset.UnixEpoch) { Key = "key" };
		var theirs = mine with { Key = "another" };
		var text = Matches();

		Assert.Same(TextMatches.Empty, TextMatches.For(mine)); // none set
		Assert.Same(TextMatches.Empty, TextMatches.For(mine with { Key = null }));
		Assert.Equal("key", text.Key);
		Assert.Same(TextMatches.Empty, TextMatches.For(theirs));
	}
}
