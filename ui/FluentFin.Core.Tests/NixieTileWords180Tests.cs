using System.Globalization;
using System.Text.Json;
using FluentFin.Core.Outside;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-4, stream t): the shared model of a Made by Nixie tile's words: the titles (<see cref="NixieTitles"/>), the
/// reason line (<see cref="ReasonLine"/>, <see cref="ReasonCount"/>), the award badges (<see cref="AwardFacts"/>) and how a
/// builder sets them (<c>Make.Of(context, …)</c>). Streams l, a and s write to this model.
/// </summary>
public sealed class NixieTileWords180Tests
{
	private static readonly DateTimeOffset Monday = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

	private static IndexEntry Film(string name, string? tmdb = null, bool played = false) =>
		new() { Id = Guid.NewGuid(), Name = name, Kind = IndexKind.Movie, Tmdb = tmdb, Played = played, PrimaryTag = "p" };

	private static IndexEntry Show(string name, bool played = false) => new() { Id = Guid.NewGuid(), Name = name, Kind = IndexKind.Series, Played = played };

	private static OutsideList Award(string id, string title, params string[] tmdb) =>
		new(id, OutsideSourceId.Wikidata, OutsideListKind.Award, title, $"{title}, from Wikidata, by year", [.. tmdb.Select(x => new OutsideListItem(new OutsideRef(Tmdb: x, Kind: IndexKind.Movie)))]);

	private static T InCulture<T>(string name, Func<T> run)
	{
		var was = CultureInfo.CurrentCulture;
		CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
		try
		{
			return run();
		}
		finally
		{
			CultureInfo.CurrentCulture = was;
		}
	}

	// ---------- the count

	[Theory]
	[InlineData(12, 3, "12 films, 3 unwatched")]
	[InlineData(12, 12, "12 films, none watched")]
	[InlineData(12, 0, "12 films, all watched")]
	[InlineData(1, 1, "1 film, not watched")]
	[InlineData(1, 0, "1 film, watched")]
	public void The_count_says_how_many_and_how_many_are_unwatched(int count, int unwatched, string said) =>
		Assert.Equal(said, InCulture("en-GB", () => ReasonCount.Titles(count, IndexKind.Movie, unwatched).Text));

	[Fact]
	public void The_count_has_three_forms_and_says_films_shows_or_titles() => InCulture("en-GB", () =>
	{
		Assert.Equal("1,234 titles", ReasonCount.Titles(1234, null).Text);
		Assert.Equal("3 shows", ReasonCount.Titles(3, IndexKind.Series).Text);
		Assert.Equal("5 more films, none watched", ReasonCount.More(5, IndexKind.Movie, 5).Text);
		Assert.Equal("1 more show", ReasonCount.More(1, IndexKind.Series).Text);
		Assert.Equal("2 of 7 films watched", ReasonCount.Watched(2, 7, IndexKind.Movie).Text);
		return 0;
	});

	[Fact]
	public void The_count_of_titles_counts_the_unwatched_and_says_the_kind()
	{
		Assert.Equal("3 films, 2 unwatched", ReasonCount.Of([Film("A"), Film("B", played: true), Film("C")]).Text);
		Assert.Equal("2 titles, 1 unwatched", ReasonCount.Of([Film("A"), Show("B", played: true)]).Text);
		Assert.Equal("2 shows", ReasonCount.OfAll([Show("A"), Show("B")]).Text);
		Assert.Equal("0 titles", ReasonCount.OfAll([]).Text);
	}

	// ---------- the reason line

	[Fact]
	public void A_reason_line_is_its_facts_then_its_count_then_its_end() => InCulture("en-GB", () =>
	{
		var line = ReasonLine.Of("Director", ReasonCount.Titles(8, IndexKind.Movie, 8)) with { Until = new DateOnly(2026, 11, 29) };

		Assert.Equal("Director · 8 films, none watched · until Sun 29 Nov", line.Text);
		Assert.Equal("Director, 8 films, none watched, until Sun 29 Nov", line.Spoken);
		Assert.Equal("Director", line.FactsText);
		Assert.Equal("8 films, none watched · until Sun 29 Nov", line.CountText);
		Assert.Equal("Next: Heat (1995) · Michael Mann · 2 of 7 films watched",
			ReasonLine.Of(ReasonFacts.Next("Heat", 1995), "Michael Mann", ReasonCount.Watched(2, 7, IndexKind.Movie)).Text);
		return 0;
	});

	[Fact]
	public void The_end_date_says_the_day_in_the_users_culture_and_never_goes_stale() =>
		Assert.Equal("until Sun Nov 29", InCulture("en-US", () => (ReasonLine.Of("x", ReasonCount.Titles(1, null)) with { Until = new DateOnly(2026, 11, 29) }).UntilText));

	[Fact]
	public void An_empty_fact_leaves_no_double_separator() =>
		Assert.Equal("4 films", ReasonLine.Of(" ", ReasonCount.Titles(4, IndexKind.Movie)).Text);

	// ---------- the titles

	[Fact]
	public void The_slot_titles_front_load_their_hook()
	{
		Assert.Equal("Next in Mission: Impossible", NixieTitles.NextIn(" Mission: Impossible "));
		Assert.Equal("More like Heat", NixieTitles.MoreLike("Heat"));
		Assert.Equal("More from Denis Villeneuve", NixieTitles.MoreFrom("Denis Villeneuve"));
		Assert.Equal("Spotlight: Jean-Pierre Melville", NixieTitles.Spotlight("Jean-Pierre Melville"));
		foreach (var title in new[] { NixieTitles.NextIn("Alien"), NixieTitles.MoreLike("Heat"), NixieTitles.MoreFrom("Agnès Varda"), NixieTitles.Spotlight("A24") })
		{
			Assert.Null(NixieTitles.Check(title));
		}
	}

	[Theory]
	[InlineData("Your films from 1980", "starts with “Your”")]
	[InlineData("your picks", "starts with “your”")]
	[InlineData("You might like", "starts with “You”")]
	[InlineData("Because you watched Heat", "speaks of the viewer (“you”)")]
	[InlineData("Picked for you", "speaks of the viewer (“you”)")]
	[InlineData("", "empty")]
	public void A_title_about_the_viewer_breaks_the_rule(string title, string why) => Assert.Equal(why, NixieTitles.Check(title));

	[Fact]
	public void Reason_facts_say_what_was_done_with_a_title()
	{
		Assert.Equal("You finished Arrival", ReasonFacts.Finished("Arrival"));
		Assert.Equal("You watched Arrival twice", ReasonFacts.Rewatched("Arrival"));
		Assert.Equal("A favourite: Arrival", ReasonFacts.Favourite("Arrival"));
		Assert.Equal("Arrival", ReasonFacts.Named("Arrival", null));
	}

	// ---------- the award badges

	[Fact]
	public void A_winner_is_not_its_awards_nominee_too_and_badges_count_each_title_once()
	{
		var won = Film("Won", "1");
		var nominated = Film("Nominated", "2");
		var palme = Film("Palme", "3");
		var index = IndexSnapshot.Empty with { Entries = [won, nominated, palme, Film("Nothing", "9")] };
		var lists = new[]
		{
			Award("award-best-picture", "Best Picture winners", "1"),
			Award("award-best-picture-nominees", "Best Picture nominees", "1", "2"),
			Award("award-palme-dor", "Palme d'Or winners", "3", "1"),
		};
		var facts = AwardFacts.Of(lists, new OutsideMatcher(index));

		Assert.Equal([("Best Picture", true), ("Palme d'Or", true)], facts.Of(won.Id).Select(x => (x.Award, x.Won)));
		Assert.Equal([("Best Picture", false)], facts.Of(nominated.Id).Select(x => (x.Award, x.Won)));
		Assert.Empty(facts.Of(Guid.NewGuid()));

		var all = facts.BadgesFor([won.Id, nominated.Id, palme.Id, won.Id]);
		Assert.Equal(["Best Picture winner", "2 Palme d'Or winners"], all.Select(x => x.Text));
		Assert.Equal(AwardFacts.MostBadges, all.Count); // the nominee badge is the third: not shown

		Assert.Equal(["2 Palme d'Or winners"], facts.BadgesFor([won.Id, nominated.Id, palme.Id], except: "Best Picture").Select(x => x.Text));
		Assert.Equal(["Best Picture nominee"], facts.BadgesFor([nominated.Id]).Select(x => x.Text));
		Assert.Empty(AwardFacts.Empty.BadgesFor([won.Id]));
	}

	[Fact]
	public void Badge_words() => InCulture("en-GB", () =>
	{
		Assert.Equal("Golden Lion winner", AwardFacts.Words("Golden Lion", won: true, 1));
		Assert.Equal("1,200 Best Picture nominees", AwardFacts.Words("Best Picture", won: false, 1200));
		return 0;
	});

	// ---------- how a builder sets them

	[Fact]
	public void A_builder_sets_the_title_the_reason_and_the_badges_through_Make()
	{
		var heat = Film("Heat", "949");
		var other = Film("Thief", "11", played: true);
		var index = IndexSnapshot.Empty with { Entries = [heat, other] };
		var context = BuildContext.For(index, Monday) with { Outside = OutsideListsSnapshot.Empty with { Lists = [Award("award-palme-dor", "Palme d'Or winners", "949")] } };

		var made = Make.Of(context, "morelike:heat", SmartCollectionKind.MoreLike, NixieTitles.MoreLike("Heat"),
			ReasonLine.Of(ReasonFacts.Finished("Heat"), ReasonCount.Of([heat, other])), hasOwnOrder: false, [heat, other]);

		Assert.Equal("More like Heat", made.Title);
		Assert.Equal("You finished Heat · 2 films, 1 unwatched", made.ReasonText);
		Assert.Equal("You finished Heat", made.Why); // the Collection page's line: the facts (its count is the page's own)
		Assert.Equal(["Palme d'Or winner"], made.Badges.Select(x => x.Text));
		Assert.Equal("More like Heat, made by Nixie, more like a title, You finished Heat, 2 films, 1 unwatched, Palme d'Or winner", made.TileAutomationName);
		Assert.Equal("More like Heat, made by Nixie, more like a title, 2 films", made.AutomationName); // a preset chip's: the name and count
		Assert.Equal("Made by Nixie · More like a title", made.Eyebrow);
		Assert.Same(context.Awards, context.Awards); // made once a context
	}

	[Fact]
	public void Without_Wikidatas_lists_no_badges_and_an_explicit_why_is_kept()
	{
		var film = Film("Heat", "949");
		var context = BuildContext.For(IndexSnapshot.Empty with { Entries = [film] }, Monday);

		var made = Make.Of(context, "x", SmartCollectionKind.Spotlight, NixieTitles.Spotlight("Michael Mann"), ReasonLine.Of("Director", ReasonCount.Of([film])),
			hasOwnOrder: false, [film], why: "Films directed by Michael Mann, the best first");

		Assert.Empty(made.Badges);
		Assert.Same(AwardFacts.Empty, context.Awards);
		Assert.Equal("Films directed by Michael Mann, the best first", made.Why);
		Assert.Equal("Director · 1 film, not watched", made.ReasonText);
	}

	// ---------- Home's saved page keeps them

	[Fact]
	public void The_reason_and_the_badges_survive_Homes_saved_page_and_an_older_page_says_the_count()
	{
		var made = new SmartCollection("spotlight:x", SmartCollectionKind.Spotlight, "Spotlight: X", "why", false, [Guid.NewGuid(), Guid.NewGuid()])
		{
			Only = IndexKind.Movie,
			Reason = ReasonLine.Of("Director", ReasonCount.Titles(2, IndexKind.Movie, 1)) with { Until = new DateOnly(2026, 11, 29) },
			Badges = [new CollectionBadge("Golden Lion winner", "Golden Lion", true, 1)],
		};

		var back = JsonSerializer.Deserialize<SmartCollection>(JsonSerializer.Serialize(made))!;

		Assert.Equal(made.ReasonText, back.ReasonText);
		Assert.Equal(made.Reason, back.Reason with { Facts = made.Reason!.Facts }); // the facts are a new list, the same words
		Assert.Equal(made.Reason.Facts, back.Reason.Facts);
		Assert.Equal(made.Badges, back.Badges);
		Assert.Equal(SmartCollectionKind.Spotlight, back.Kind);

		// 1.7.x's saved page: no reason, no badges; the tile says the count, as it did
		var older = JsonSerializer.Deserialize<SmartCollection>("""{"Id":"pick:highly-rated","Kind":8,"Title":"Acclaimed, not seen yet","Why":"w","HasOwnOrder":false,"ItemIds":["00000000-0000-0000-0000-000000000001"],"Only":0}""")!;
		Assert.Null(older.Reason);
		Assert.Empty(older.Badges);
		Assert.Equal("1 film", older.ReasonText);
		Assert.Equal("Acclaimed, not seen yet, made by Nixie, acclaimed, 1 film", older.TileAutomationName);
	}
}
