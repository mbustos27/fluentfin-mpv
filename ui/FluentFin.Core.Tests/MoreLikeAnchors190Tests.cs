using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Anchors;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the rebuild's blocker 2): on the owner's library a More like was This week's second tile though its show had
/// one episode played; its first seven posters shared only a streaming platform's studio (rare on a server of production
/// companies, so it let them in alone); and its reason named two actors one title had. A show anchors More like from three episodes (or
/// finished), a film finished; a studio or network lets a title in only beside another trait they share; a reason names only
/// what two or more of its titles share.
/// </summary>
public sealed class MoreLikeAnchors190Tests
{
	private static readonly DateTimeOffset Now = new(2026, 11, 27, 20, 0, 0, TimeSpan.FromHours(-8));
	private static int s_next;

	private static IndexEntry Show(string name, string[] genres, string? studio = null, string[]? cast = null, double rating = 7.0) => new()
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000194"),
		Name = name,
		Kind = IndexKind.Series,
		Year = 2020,
		Genres = genres,
		Studios = studio is null ? [] : [studio],
		Rating = rating,
		Unplayed = 20,
		PrimaryTag = "p",
		People = [.. (cast ?? []).Select(x => new IndexPerson(x, null, "Actor"))],
	};

	// shows no likeness picks out: each of its own production company, its own people, comedies and dramas like the rest
	private static IEnumerable<IndexEntry> Filler(int count) =>
		Enumerable.Range(0, count).Select(i => Show($"Filler {i}", [i % 2 == 0 ? "Comedy" : "Drama"], $"Filler Productions {i}", [$"Filler Lead {i}"]));

	private static IndexSnapshot Index(IEnumerable<IndexEntry> entries, Dictionary<Guid, SeriesPlays> plays) => new([], [.. entries], plays, HasPeople: true, Now);

	private static List<SmartCollection> MoreLikes(IndexSnapshot index) => [.. new MoreLikeBuilder().Build(BuildContext.For(index, Now))];

	[Fact]
	public void AShowTriedOnce_AnchorsNoMoreLike_ThreeEpisodesOrFinishedDo()
	{
		List<IndexEntry> Circle(string name) => [Show(name, ["Comedy"], $"{name} Company"), .. Enumerable.Range(0, 10).Select(i => Show($"{name} alike {i}", ["Comedy"], $"{name} Company"))];
		var once = Circle("Tried Once");
		var three = Circle("Three In");
		var plays = new Dictionary<Guid, SeriesPlays> { [once[0].Id] = new(1, Now.AddDays(-1)), [three[0].Id] = new(3, Now.AddDays(-2)) };

		var likes = MoreLikes(Index([.. once, .. three, .. Filler(300)], plays));

		Assert.Equal(["More like Three In"], likes.Select(x => x.Title));
		Assert.False(AnchoredSlots.AnchorsMoreLike(once[0], CanonicalTitles.Each([.. once]), plays));
		Assert.True(AnchoredSlots.AnchorsMoreLike(once[0] with { Played = true }, CanonicalTitles.Each([.. once]), plays)); // finished
	}

	[Fact]
	public void ARareStudioAlone_LetsNoTitleIn_TheReasonNamesWhatTheShownShare()
	{
		// science fiction on a streaming platform with two actors; the platform's other titles are documentaries and animation
		// that share nothing else with it, rated higher; the actors' other shows share them
		var anchor = Show("Quest Show", ["Sci-Fi & Fantasy", "Drama"], "Platform+", ["Actor One", "Actor Two", "Lead Three"]);
		var platform = Enumerable.Range(0, 12).Select(i => Show($"Platform documentary {i}", [i % 2 == 0 ? "Documentary" : "Animation"], "Platform+", rating: 8.5)).ToList();
		var first = Enumerable.Range(0, 5).Select(i => Show($"Actor One show {i}", ["Crime", "Thriller"], $"One Productions {i}", ["Actor One", $"Co-star {i}"])).ToList();
		var second = Enumerable.Range(0, 5).Select(i => Show($"Actor Two show {i}", ["Mystery"], $"Two Productions {i}", ["Actor Two", $"Partner {i}"])).ToList();
		var plays = new Dictionary<Guid, SeriesPlays> { [anchor.Id] = new(4, Now.AddDays(-1)) };

		var like = Assert.Single(MoreLikes(Index([anchor, .. platform, .. first, .. second, .. Filler(400)], plays)));

		Assert.DoesNotContain(like.ItemIds, platform.Select(x => x.Id).Contains); // the platform alone: no likeness
		Assert.Equal(10, like.ItemIds.Count);
		Assert.Matches("^Shares (Actor One and Actor Two|Actor Two and Actor One) · 10 shows$", like.ReasonText); // what the shown share, no studio
	}

	[Fact]
	public void ARareStudio_WithAGenreOrAPersonBesideIt_StillLetsATitleIn()
	{
		// the same studio and a shared genre: a production company's dramas are alike, as before
		var anchor = Show("Company Drama", ["Drama"], "Rare Company");
		var alike = Enumerable.Range(0, 9).Select(i => Show($"Company drama {i}", ["Drama"], "Rare Company")).ToList();
		var plays = new Dictionary<Guid, SeriesPlays> { [anchor.Id] = new(5, Now.AddDays(-1)) };

		var like = Assert.Single(MoreLikes(Index([anchor, .. alike, .. Filler(400)], plays)));

		Assert.Equal(alike.Select(x => x.Id).Order(), like.ItemIds.Order());
	}

	// the Phase 3 review: a server whose clock runs ahead dates the episode just played a few minutes "in the future", and the
	// title was neither fresh nor played lately: its More like was missing and its categories went last
	[Fact]
	public void APlayDatedAheadOfThisPCsClock_ByUpToADay_IsPlayedNow()
	{
		List<IndexEntry> Circle(string name) => [Show(name, ["Comedy"], $"{name} Company"), .. Enumerable.Range(0, 10).Select(i => Show($"{name} alike {i}", ["Comedy"], $"{name} Company"))];
		var ahead = Circle("Just Played");
		var earlier = Circle("Three Days Ago");
		var plays = new Dictionary<Guid, SeriesPlays> { [ahead[0].Id] = new(4, Now.AddMinutes(2)), [earlier[0].Id] = new(4, Now.AddDays(-3)) };

		var likes = MoreLikes(Index([.. ahead, .. earlier, .. Filler(300)], plays));

		Assert.Equal(["More like Just Played", "More like Three Days Ago"], likes.Select(x => x.Title));
		Assert.True(AnchoredSlots.Lately(Now.AddMinutes(2), Now));
		Assert.Equal(0, Hold.Recency(Now.AddHours(23), Now)); // fresh: the lineup ranks it first
		Assert.False(AnchoredSlots.Lately(Now.AddDays(2), Now)); // two days ahead is no date to trust
		Assert.Equal(1, Hold.Recency(Now.AddDays(2), Now));
	}

	[Fact]
	public void AReason_NamesOnlyWhatTwoOrMoreOfItsTitlesShare()
	{
		var anchor = Show("Anchor", ["Drama", "Mystery"], cast: ["Star One", "Star Two"]);
		var one = Show("Has Star One", ["Drama"], cast: ["Star One"]);
		var mysteries = Enumerable.Range(0, 3).Select(i => Show($"Mystery {i}", ["Mystery"])).ToList();
		var similarity = TitleSimilarity.Of([anchor, one, .. mysteries, .. Filler(200)], new AnchorsTests.Facts());

		var shared = similarity.Shared(anchor, [one, .. mysteries], 3).Select(x => x.Label).ToList();

		Assert.Equal(["mystery"], shared); // "Star One" is one title's: not said of all four
		Assert.Equal(["Star One", "drama"], similarity.Shared(anchor, [one], 3).Select(x => x.Label)); // of one title: what it shares
	}
}
