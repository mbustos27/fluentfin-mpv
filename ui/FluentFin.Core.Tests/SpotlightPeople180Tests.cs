using System.Diagnostics;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Anchors;
using FluentFin.Core.SmartCollections.Builders;
using Jellyfin.Sdk.Generated.Models;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-3 with stream a's people step): the Spotlight on an index whose people came the way the app reads them
/// (the titles read, then <see cref="PeoplePass.Billed"/> of each title merged by <see cref="PeoplePass.With"/>), at real
/// shape: 25,000 titles; a few hundred directors with 6 or more films and actors billed in the first three of 8 or more;
/// most people in one or two. It picks someone, that someone passes every gate, the long tail never does, and the pick is
/// timed.
/// </summary>
public sealed class SpotlightPeople180Tests(ITestOutputHelper output)
{
	// Tuesday 24 November 2026: ISO week 48 (Monday 23 to Sunday 29)
	private static readonly DateTimeOffset Tuesday = new(2026, 11, 24, 20, 0, 0, TimeSpan.FromHours(-8));
	private static readonly Guid Movies = Guid.Parse("00000000-0000-0000-0000-00000000f11d");
	private static readonly Guid Shows = Guid.Parse("00000000-0000-0000-0000-00000000510d");

	private sealed record Shape(IndexSnapshot Index, IReadOnlySet<string> Tail, IReadOnlyDictionary<string, int> Directed, IReadOnlyDictionary<string, int> TopBilled);

	// 23,000 films and 2,000 shows. 300 directors with 6 to 20 films each (a home genre, a decade, a regular lead) and the
	// rest one or two films a director; 400 actors billed in the first three of 8 to 24 titles, the other first-three
	// billings and every fourth and fifth billing from a long tail of people in one or two; a writer on each (never counted);
	// ratings, critics' ratings for 40 %, tags, 2 % played; curated studios on 2 % of the films
	private static Shape RealShape()
	{
		var random = new Random(180);
		string[] genres = ["Drama", "Comedy", "Thriller", "Action", "Crime", "Horror", "Romance", "Documentary", "Animation", "Science Fiction",
			"Adventure", "Family", "Fantasy", "Mystery", "War", "Western"];
		string[] tags = ["heist", "time travel", "revenge", "road trip", "heist", "serial killer", "small town", "based on novel or book"];
		var curated = SpotlightStudios.All.Select(x => x.Name).ToArray();
		var tail = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var tailNext = 0;
		string Tail() { var name = $"Tail Person {tailNext++ / 2:D6}"; tail.Add(name); return name; } // each in two titles at most

		// the notable directors' films, and the stars' slots
		var directorFor = new List<(string Name, string Genre, int Decade, string Lead)>();
		for (var d = 0; d < 300; d++)
		{
			var films = 6 + random.Next(15);
			var who = ($"Notable Director {d:D3}", genres[random.Next(genres.Length)], 1940 + (10 * random.Next(9)), $"Star {random.Next(400):D3}");
			directorFor.AddRange(Enumerable.Repeat(who, films));
		}

		var stars = Enumerable.Range(0, 400).SelectMany(s => Enumerable.Repeat($"Star {s:D3}", 8 + random.Next(17))).OrderBy(_ => random.Next()).ToList();
		var starAt = 0;
		var dtos = new List<(BaseItemDto Dto, Guid Library)>();
		for (var i = 0; i < 25_000; i++)
		{
			var film = i < 23_000;
			var directed = film && i < directorFor.Count ? directorFor[i] : default;
			var genre = directed.Name is not null ? directed.Genre : genres[random.Next(genres.Length)];
			var year = directed.Name is not null ? directed.Decade + random.Next(10) : 1930 + random.Next(95);
			var people = new List<BaseItemPerson>();
			if (film)
			{
				people.Add(new BaseItemPerson { Name = directed.Name ?? Tail(), Type = BaseItemPerson_Type.Director });
			}

			// first three billed: the director's regular lead first, then stars while they last, else the long tail
			var billed = new List<string>();
			if (directed.Lead is { } lead && random.NextDouble() < 0.5)
			{
				billed.Add(lead);
			}

			while (billed.Count < 3)
			{
				billed.Add(starAt < stars.Count && random.NextDouble() < 0.3 ? stars[starAt++] : Tail());
			}

			people.AddRange(billed.Distinct().Select(n => new BaseItemPerson { Name = n, Type = BaseItemPerson_Type.Actor }));
			people.Add(new BaseItemPerson { Name = Tail(), Type = BaseItemPerson_Type.Actor }); // fourth and fifth billed: never counted
			people.Add(new BaseItemPerson { Name = Tail(), Type = BaseItemPerson_Type.Actor });
			people.Add(new BaseItemPerson { Name = $"Writer {i}", Type = BaseItemPerson_Type.Writer });
			var titleTags = random.NextDouble() < 0.3 ? new List<string> { tags[random.Next(tags.Length)], "other", "more", "words", "here", "too" } : new List<string> { "one" };
			dtos.Add((new BaseItemDto
			{
				Id = Guid.Parse($"{i:D8}-0000-0000-0000-000000000180"),
				Name = $"{(film ? "Film" : "Show")} {i:D5}",
				Type = film ? BaseItemDto_Type.Movie : BaseItemDto_Type.Series,
				ProductionYear = year,
				CommunityRating = (float)Math.Round(Math.Clamp(6.2 + (directed.Name is not null ? 1.2 : 0) + ((random.NextDouble() - 0.5) * 3), 1, 10), 1),
				CriticRating = random.NextDouble() < 0.4 ? random.Next(20, 100) : null,
				Genres = [genre, genres[random.Next(genres.Length)]],
				Tags = titleTags,
				Studios = [new NameGuidPair { Name = film && random.NextDouble() < 0.02 ? curated[random.Next(curated.Length)] : $"Company {random.Next(4000)}" }],
				ProductionLocations = [random.NextDouble() < 0.6 ? "United States of America" : "France"],
				UserData = new UserItemDataDto { Played = random.NextDouble() < 0.02 },
				ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = "p" } },
				People = people,
			}, film ? Movies : Shows));
		}

		// as the app reads it: the titles first (From keeps no people), then the people step's names merged in
		var titles = new IndexSnapshot([new IndexLibrary(Movies, "Movies", IndexKind.Movie), new IndexLibrary(Shows, "Shows", IndexKind.Series)],
			[.. dtos.Select(x => IndexEntry.From(x.Dto, x.Library)!)], new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Tuesday);
		var index = PeoplePass.With(titles, dtos.ToDictionary(x => x.Dto.Id!.Value, x => PeoplePass.Billed(x.Dto)));

		var directedCount = index.Entries.Where(x => x.Kind == IndexKind.Movie).SelectMany(x => x.People.Where(p => p.Kind == "Director").Select(p => p.Name).Distinct())
			.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
		var billedCount = index.Entries.SelectMany(x => x.People.Where(p => p.Kind == "Actor").Select(p => p.Name).Distinct())
			.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
		return new Shape(index, tail, directedCount, billedCount);
	}

	private static readonly Lazy<Shape> s_shape = new(RealShape);

	[Fact]
	public void AtRealShape_ThePeopleStepsIndex_GetsASpotlightOfSomeone_WhoPassesEveryGate()
	{
		var shape = s_shape.Value;
		Assert.True(shape.Index.HasPeople);
		Assert.All(shape.Index.Entries, x => Assert.True(x.People.Count(p => p.Kind == "Actor") <= 3)); // the step keeps the first three billed
		output.WriteLine($"people: {shape.Directed.Count:N0} directors ({shape.Directed.Count(x => x.Value >= 6)} with 6+ films), " +
			$"{shape.TopBilled.Count:N0} actors billed top 3 ({shape.TopBilled.Count(x => x.Value >= 8)} in 8+), {shape.Tail.Count:N0} in the long tail");

		var context = new BuildContext(shape.Index, Tuesday, new HashSet<string>()) { Outside = FluentFin.Core.Outside.OutsideListsSnapshot.Empty };
		_ = context.Titles; // the catalog's build collapses the copies once for every builder
		_ = context.Quality;
		var watch = Stopwatch.StartNew();
		var counts = new SpotlightCounts();
		var candidates = SpotlightBuilder.Candidates(context, counts);
		var gated = watch.Elapsed;
		var pick = SpotlightBuilder.Pick(candidates, SpotlightWeek.Of(Tuesday));
		var picked = watch.Elapsed;
		output.WriteLine($"Spotlight at 25,000 titles: gates {gated.TotalMilliseconds:0} ms, the pick {(picked - gated).TotalMilliseconds:0.0} ms; {counts.Census(SpotlightWeek.Of(Tuesday), pick is not null)}");

		// someone, of people: directors and actors pass the gates; nobody of the long tail ever does
		Assert.NotNull(pick);
		Assert.True(counts.Of(SpotlightSubject.Director, SpotlightGate.Coherent) > 0 && counts.Of(SpotlightSubject.Actor, SpotlightGate.Coherent) > 0, counts.Census(SpotlightWeek.Of(Tuesday), true));
		Assert.DoesNotContain(candidates, x => shape.Tail.Contains(x.Name));
		Assert.All(candidates.Where(x => x.Subject != SpotlightSubject.Studio), x => Assert.True(
			x.Subject == SpotlightSubject.Director ? shape.Directed[x.Name] >= SpotlightBuilder.DirectorFilms : shape.TopBilled[x.Name] >= SpotlightBuilder.ActorTitles, x.Name));
		Assert.All(candidates, x => Assert.True(x.Unwatched >= SpotlightBuilder.LeastUnwatched, x.Name));

		// the published build: one Spotlight, its end date, best unwatched first
		var spotlight = Assert.Single(SmartCollectionCatalog.BuildLaterAsync(shape.Index, Tuesday, outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty, picks: new KeptPicks()).Result);
		Assert.Equal(pick.Id, spotlight.Id);
		Assert.Equal($"Spotlight: {pick.Name}", spotlight.Title);
		Assert.Equal(new DateOnly(2026, 11, 29), spotlight.Reason?.Until);
		Assert.InRange(spotlight.ItemIds.Count, SpotlightBuilder.LeastUnwatched, SpotlightBuilder.Most);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"the gates and the pick took {picked.TotalMilliseconds:0} ms");
	}

	[Fact]
	public void AtRealShape_HomesFirstNixieRow_NoLongerWaitsForTheSpotlight()
	{
		// the review's Major 2: Home's first Nixie row waits on BuildAllAsync (the published builders). Steady rounds, each a new
		// read of the same index (as each refresh makes one): with the Spotlight among them (before) and without (after)
		var shape = s_shape.Value;
		var now = Tuesday;
		long Round(IReadOnlyList<ISmartCollectionBuilder> builders)
		{
			var fresh = shape.Index with { Key = "round:" + Guid.NewGuid() };
			var watch = Stopwatch.StartNew();
			_ = SmartCollectionCatalog.BuildAll(builders, fresh, now, null, null, FluentFin.Core.Outside.OutsideListsSnapshot.Empty, null);
			return watch.ElapsedMilliseconds;
		}

		IReadOnlyList<ISmartCollectionBuilder> before = [.. SmartCollectionCatalog.PublishedBuilders, new SpotlightBuilder()];
		_ = Round(before); // warm
		_ = Round(SmartCollectionCatalog.PublishedBuilders);
		var withIt = Enumerable.Range(0, 5).Select(_ => Round(before)).Order().ToList();
		var without = Enumerable.Range(0, 5).Select(_ => Round(SmartCollectionCatalog.PublishedBuilders)).Order().ToList();
		output.WriteLine($"Home's build at 25,000 titles with people, 5 steady rounds: before (with the Spotlight) {string.Join(", ", withIt)} ms; after {string.Join(", ", without)} ms");

		Assert.DoesNotContain(SmartCollectionCatalog.PublishedBuilders, x => x is SpotlightBuilder);
		Assert.True(without[2] < withIt[2], $"after's median {without[2]} ms, before's {withIt[2]} ms");
	}

	[Fact]
	public void AtRealShape_EightMondays_SpotlightPeople_NoneTwiceRunning()
	{
		var shape = s_shape.Value;
		var candidates = SpotlightBuilder.Candidates(new BuildContext(shape.Index, Tuesday, new HashSet<string>()) { Outside = FluentFin.Core.Outside.OutsideListsSnapshot.Empty });
		var monday = SpotlightWeek.Of(Tuesday).Monday;

		var picks = Enumerable.Range(0, 8).Select(w => SpotlightBuilder.Pick(candidates, SpotlightWeek.Of(monday.AddDays(7 * w)))!).ToList();
		output.WriteLine(string.Join(" | ", picks.Select(x => $"{x.Subject}: {x.Name}")));

		Assert.All(picks.Zip(picks.Skip(1)), x => Assert.NotEqual(x.First.Id, x.Second.Id));
		Assert.Contains(picks, x => x.Subject is SpotlightSubject.Director or SpotlightSubject.Actor);
	}
}
