using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the owner's library, fix 9): the quality score's caution for titles of the last 12 months and for concert films
/// (their confidence halved), and the critics' value folded into the rating it reads.
/// </summary>
public sealed class QualityScore190Tests
{
	// Wednesday 25 November 2026 in California, the report's "now"
	private static readonly DateTimeOffset Now = new(2026, 11, 25, 20, 0, 0, TimeSpan.FromHours(-8));
	private static int s_next;

	private static IndexEntry Film(string name, double rating = 8.0, int year = 2010, string? premiere = null, float? critic = null, int tags = 10, string[]? tagged = null) => new()
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000199"),
		Name = name,
		Kind = IndexKind.Movie,
		Year = year,
		PremiereDate = premiere,
		Rating = rating,
		Critic = critic,
		TagCount = tags,
		Tags = tagged ?? [],
		PrimaryTag = "p",
	};

	// films of median confidence 1 (ten tags), rated 6.0 to 7.4: the mean the scores shrink to
	private static List<IndexEntry> Others() => [.. Enumerable.Range(0, 30).Select(i => Film($"Other {i:D2}", rating: 6.0 + (i % 15 / 10.0)))];

	[Fact]
	public void ATitleOfTheLast12Months_HasItsConfidenceHalved_ScoresLower_AndNoneIsNewWithoutADay()
	{
		var fresh = Film("Released in October", premiere: "2026-10-02");
		var year = Film("Dated this year only", year: 2026);
		var old = Film("Released in 2010", premiere: "2010-10-02");
		var titles = CanonicalTitles.Of([fresh, year, old, .. Others()]);

		var quality = QualityScore.Of(titles, now: Now);
		var undated = QualityScore.Of(titles);

		Assert.Equal((true, true, false), (quality.IsNew(fresh), quality.IsNew(year), quality.IsNew(old)));
		Assert.Equal(0.5, quality.Confidence(fresh));
		Assert.Equal(1.0, quality.Confidence(old));
		Assert.True(quality.Of(fresh) < quality.Of(old)); // the same rating and backing: the new one is shrunk more
		Assert.Equal(quality.Of(old)!.Value, undated.Of(fresh)!.Value, 9); // without a day nothing is new (1.8.1's score)
		Assert.False(undated.IsNew(fresh));
	}

	[Theory]
	[InlineData("2025-11-26", true)]  // 364 days before the day
	[InlineData("2025-11-25", false)] // 12 months before
	[InlineData("2027-11-25", true)]  // dated ahead, up to 12 months
	[InlineData("2027-11-26", false)]
	[InlineData(null, false)]         // a year only (2010): 1 July of it
	public void TheWindow_IsTheTwelveMonthsBeforeTheDay(string? premiere, bool isNew)
	{
		var film = Film("Edge", premiere: premiere);

		Assert.Equal(isNew, QualityScore.Of(CanonicalTitles.Of([film]), now: Now).IsNew(film));
	}

	[Fact]
	public void AConcertFilm_HasItsConfidenceHalved_ANewOneQuartered()
	{
		var concert = Film("Live at the Bowl", tagged: ["concert film"]);
		var newConcert = Film("The 2026 tour", premiere: "2026-10-02", tagged: ["concert film", "music"]);
		var live = Film("A live performance", tagged: ["live performance"]) with { Genres = ["Documentary", "Music"] };
		var titles = CanonicalTitles.Of([concert, newConcert, live, .. Others()]);

		var quality = QualityScore.Of(titles, now: Now);

		Assert.Equal((0.5, 0.25, 0.5), (quality.Confidence(concert), quality.Confidence(newConcert), quality.Confidence(live)));
		Assert.True(QualityScore.IsConcert(concert));
		Assert.False(QualityScore.IsConcert(Film("A musical", tagged: ["musical"])));
	}

	[Fact]
	public void TheCriticsValue_IsFoldedIntoTheRating_TwoTied76sAreToldApart()
	{
		// the report: Imagine Me & You (2006) and Kind Hearts and Coronets, both 7.6, both with critics and ten tags, tied, and
		// the name chose. The critics' 34 and 100 now tell them apart
		var imagine = Film("Imagine Me & You", rating: 7.6, year: 2006, critic: 34);
		var kindHearts = Film("Kind Hearts and Coronets", rating: 7.6, year: 1949, critic: 100);
		var titles = CanonicalTitles.Of([imagine, kindHearts, .. Others()]);
		var quality = QualityScore.Of(titles, now: Now);

		// the review's Major 2: each departs from the films' usual gap ((10 - 7.6) + (3.4 - 7.6)) / 2 = -0.9 by a third of its own
		Assert.Equal(-0.9, quality.GapOf(IndexKind.Movie), 9);
		Assert.Equal(7.6 + ((10.0 - 7.6 + 0.9) / 3), quality.RatingOf(kindHearts)!.Value, 9);
		Assert.Equal(7.6 + ((3.4 - 7.6 + 0.9) / 3), quality.RatingOf(imagine)!.Value, 9);
		Assert.True(quality.Of(kindHearts) > quality.Of(imagine));
		Assert.Equal([kindHearts.Id, imagine.Id], Best.Of([imagine, kindHearts], quality).Select(x => x.Id));
		Assert.Equal(quality.Confidence(imagine), quality.Confidence(kindHearts)); // its presence still counts once, as before

		// in plain mode (nothing in the index backs a rating) the rating stands alone, as before
		Assert.Equal(7.6, QualityScore.Of(titles, plain: true, now: Now).RatingOf(imagine));
		Assert.Null(quality.RatingOf(Film("Unrated", rating: 0)));
	}

	[Fact]
	public void Acclaimed_IsLedByTheKnownFilms_NotA2026ConcertFilmRated10()
	{
		// the report's Acclaimed: a 2026 concert film rated 10 with critics' 100 first, 2026 titles in five of the first eight
		var concert = Film("A 2026 concert film", rating: 10, premiere: "2026-10-02", critic: 100, tags: 6, tagged: ["concert film"]);
		var newDrama = Film("A 2026 drama", rating: 9.6, premiere: "2026-03-01", critic: 90, tags: 8);
		var goodfellas = Film("GoodFellas", rating: 8.5, year: 1990, critic: 96, tags: 30);
		var interstellar = Film("Interstellar", rating: 8.4, year: 2014, critic: 73, tags: 30);
		var index = new IndexSnapshot([], [concert, newDrama, goodfellas, interstellar, .. Others()], new Dictionary<Guid, SeriesPlays>(), false, Now);

		var acclaimed = Assert.Single(new HighlyRatedBuilder().Build(BuildContext.For(index, Now))).ItemIds.ToList();

		Assert.Equal(goodfellas.Id, acclaimed[0]); // 1.8.1: the concert film, then the 2026 drama
		Assert.True(acclaimed.IndexOf(concert.Id) > acclaimed.IndexOf(newDrama.Id));
	}
}
