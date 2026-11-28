using System.Globalization;
using System.Text;

namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>Nixie 1.8 (P1-3): who or what a Spotlight is of.</summary>
public enum SpotlightSubject
{
	Director,
	Actor,
	Studio,
}

/// <summary>
/// Nixie 1.8 (P1-3): the week a Spotlight runs: the ISO week of the local date, Monday to Sunday. Its seed (<see cref="Seed"/>,
/// "2026-W48") is what the pick is drawn by, so the same Spotlight shows all week and the next one on Monday.
/// </summary>
public readonly record struct SpotlightWeek(int Year, int Week, DateOnly Monday, DateOnly Sunday)
{
	/// <summary>The week of <paramref name="now"/>'s own date (the PC's local date: <see cref="SmartCollectionCatalog.Now"/>).</summary>
	public static SpotlightWeek Of(DateTimeOffset now) => Of(DateOnly.FromDateTime(now.DateTime));

	public static SpotlightWeek Of(DateOnly day)
	{
		var date = day.ToDateTime(TimeOnly.MinValue);
		var year = ISOWeek.GetYear(date);
		var week = ISOWeek.GetWeekOfYear(date);
		var monday = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday));
		return new SpotlightWeek(year, week, monday, monday.AddDays(6));
	}

	/// <summary>The week <paramref name="weeks"/> before this one.</summary>
	/// <remarks>Review (Minor 6): a clock near the calendar's start stays on its first week rather than throw.</remarks>
	public SpotlightWeek Before(int weeks) => Monday.DayNumber >= 7L * weeks + 7 ? Of(Monday.AddDays(-7 * weeks)) : this;

	/// <summary>"2026-W48".</summary>
	public string Seed => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-W{Week:D2}");

	/// <summary>Weeks since Monday 1 January 2001: the turn of three or fewer candidates.</summary>
	/// <remarks>Review (Minor 6): negative for a clock set before 2001 (the turn takes it modulo, <see cref="SpotlightBuilder.Turn"/>).</remarks>
	public long Number => (Monday.DayNumber - new DateOnly(2001, 1, 1).DayNumber) / 7;
}

/// <summary>
/// Nixie 1.8 (P1-3): one who passed every Spotlight gate: their id ("spotlight:director:jeanpierremelville"), name, their titles
/// here in the Spotlight's order (the best-scored unwatched first, watched at the end), how many are unwatched, and their
/// coherence (the mean similarity of their best 12 on everything but the defining name).
/// </summary>
public sealed record SpotlightCandidate(string Id, SpotlightSubject Subject, string Name, IReadOnlyList<IndexEntry> Titles, int Unwatched, double Coherence)
{
	/// <summary>
	/// 1.9 (the owner's library, fix 8): how near the candidate's titles are to what the user watched (<see cref="SmartCollections.Taste"/>),
	/// <see cref="SpotlightBuilder.TasteLeast"/> to <see cref="SpotlightBuilder.TasteMost"/>, 1 for the candidates' average (and
	/// for everyone, with no history): the week's draw is weighted by it.
	/// </summary>
	public double Taste { get; init; } = 1;

	// 1.9 (Phase 3, R5)
	/// <summary>
	/// The likeness <see cref="Taste"/> is made of, as measured: the mean nearness of the candidate's best titles to what the user
	/// watched, before the shrink and the clamp (so the nearest stay apart where their tastes are all <see cref="SpotlightBuilder.TasteMost"/>);
	/// null with no history.
	/// </summary>
	public double? Likeness { get; init; }

	/// <summary>
	/// Among the nearest by <see cref="Likeness"/> (<see cref="SpotlightBuilder.NearestMost"/> to <see cref="SpotlightBuilder.NearestLeast"/> of the candidates): when any candidate is, the week's
	/// draw takes only these. None is with no history (the draw is then 1.8's), nor on a library whose candidates are all the
	/// nearest anyway.
	/// </summary>
	public bool Nearest { get; init; }
	// end 1.9
}

/// <summary>
/// Nixie 1.8 (P1-3, the report's "one weekly director or actor spotlight", "a short curated list of house-style studios
/// feeds Spotlight"): one Spotlight a week, chosen on Monday and shown to Sunday with its end date. It treats a person or a
/// house-style studio as a short retrospective rather than one row per credit. A candidate passes, in order:
/// <list type="number">
/// <item>notability by count: a director of <see cref="DirectorFilms"/> or more films, an actor billed in the first
/// <see cref="TopBilled"/> of <see cref="ActorTitles"/> or more titles, a curated studio (<see cref="SpotlightStudios"/>)
/// with <see cref="StudioTitles"/> or more;</item>
/// <item><see cref="LeastUnwatched"/> or more of them unwatched (never fewer: the brief);</item>
/// <item>notability by quality: their best <see cref="BestTitles"/> on the shrunk score (<see cref="QualityScore"/>) each at or above
/// the catalogue's 70th percentile for its kind (films, shows apart);</item>
/// <item>coherence: the mean pairwise similarity of their best <see cref="CoherenceOf"/>, on every feature but the defining
/// one (genres, keywords, decade, countries, studios, people; each weighted by its rarity here), drops the bottom third of
/// each subject's candidates: a filmography that shares nothing but the name isn't a retrospective.</item>
/// </list>
/// The week's pick is drawn by the week's seed among those left (highest random weight, so a candidate joining or leaving
/// elsewhere in the library doesn't move it), never last week's while others remain (<see cref="Pick"/>); 1.9: with the user's
/// history, among the nearest to what they watched only (<see cref="SpotlightCandidate.Nearest"/>). A
/// person who passes as director and actor is a director. Counts only in the census, never a name.
/// </summary>
/// <param name="picks">
/// 1.8 (the review's Minor 3): the weeks' picks kept on this PC (<see cref="ISpotlightPicks"/>: "2026-W48" → its id); null: none
/// kept (the pick is then the index's and the date's alone, <see cref="Pick"/>'s fallback).
/// </param>
/// <param name="onDemand">
/// 1.8 (the review's Major 2): the catalog's own (<see cref="SmartCollectionCatalog.LaterBuilders"/>), for a Spotlight asked for by
/// its id (<see cref="SmartCollectionCatalog.Find"/>: a pin): every candidate's retrospective, with no end date (which week's is
/// the kept pick's to say, <see cref="SmartCollectionCatalog.BuildLaterAsync"/>).
/// </param>
public sealed class SpotlightBuilder(IReadOnlyDictionary<string, string>? picks = null, bool onDemand = false) : ISmartCollectionBuilder
{
	public const int DirectorFilms = 6;
	public const int ActorTitles = 8;
	public const int StudioTitles = 6;
	public const int TopBilled = 3;
	public const int LeastUnwatched = 5;
	public const int BestTitles = 6;
	public const double Percentile = 0.70;
	public const int CoherenceOf = 12;

	/// <summary>The most titles a Spotlight holds (a short retrospective; MUBI's shelf was about 30): the best first.</summary>
	public const int Most = 30;

	// 1.9 (the owner's library, fix 8)
	/// <summary>The least a candidate's taste weighs in the draw (one sharing nothing with what the user watched).</summary>
	public const double TasteLeast = 0.25;

	/// <summary>The most a candidate's taste weighs in the draw.</summary>
	public const double TasteMost = 4;

	// Phase 3 (R5: "the Spotlight from their taste, not random")
	/// <summary>The share of the candidates the week's draw takes, the nearest first, with the least evidence.</summary>
	public const double NearestMost = 0.05;

	/// <summary>The share of the candidates the week's draw takes with the most evidence.</summary>
	public const double NearestLeast = 0.01;

	/// <summary>
	/// The fewest the draw takes from: more than the weeks kept (<see cref="SmartCollectionStore.SpotlightWeeksKept"/>), so none of
	/// them comes back while it is kept and there is still a choice. A library with no more candidates than this draws from all.
	/// </summary>
	public const int NearestFewest = 12;

	/// <summary>
	/// 1.9 (the rebuild's blocker 3): inside the nearest, a candidate's weight in the draw halves about every this many places
	/// by likeness (<c>exp(-place / NearestDecay)</c>): the 20 most alike get most of the weeks (on the owner's 394 nearest a draw
	/// by taste gave them 7 % of a week, the tastes all 2.4 to 4).
	/// </summary>
	public const double NearestDecay = 10;
	// end 1.9

	public SmartCollectionKind Kind => SmartCollectionKind.Spotlight;

	/// <summary>
	/// The week's Spotlight, or none. None for <see cref="BuildContext.Whole"/>: that is a library's presets strip (every
	/// published builder over one library's titles: a Spotlight of one library's titles is no chip, and costs a build a library)
	/// or a saved filter's preset (the strip never offered one); pins of the week's are the published one's.
	/// </summary>
	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var week = SpotlightWeek.Of(context.Now);
		if (context.Whole)
		{
			context.Gates.Count(Kind, 0, 0, 0);
			context.Gates.Note(Kind, $"{week.Seed}: not for a library's strip");
			return [];
		}

		var watch = System.Diagnostics.Stopwatch.StartNew();
		var counts = new SpotlightCounts();
		var candidates = Candidates(context, counts);
		var pick = onDemand ? null : Pick(candidates, week, picks, id => Held(context, id));
		var made = new List<SmartCollection>();
		if (onDemand)
		{
			made.AddRange(candidates.Select(x => Collection(context, x, week: null)));
		}
		else if (pick is not null)
		{
			made.Add(Collection(context, pick, week));
		}

		context.Gates.Count(Kind, counts.Considered, candidates.Count, made.Count);
		context.Gates.Note(Kind, $"{counts.Census(week, pick is not null)} in {(int)watch.Elapsed.TotalMilliseconds} ms"); // rule 10: its own time, in the census line
		return made;
	}

	/// <summary>The words a Spotlight's tile and page say of its subject.</summary>
	public static string Role(SpotlightSubject subject) => subject switch
	{
		SpotlightSubject.Director => "Director",
		SpotlightSubject.Actor => "Actor",
		_ => "Studio",
	};

	/// <summary>
	/// The Collection page's line under its summary: what the titles are and their order, and the end date when it is the week's:
	/// "Films directed by Jean-Pierre Melville, the best rated not watched yet first, until Sun 29 Nov".
	/// </summary>
	public static string Why(SpotlightSubject subject, string name, DateOnly? until)
	{
		var what = subject switch
		{
			SpotlightSubject.Director => $"Films directed by {name}",
			SpotlightSubject.Actor => $"Titles with {name} among the first three billed",
			_ => $"Titles from {name}",
		};
		return until is { } day ? $"{what}, the best rated not watched yet first, until {ReasonLine.UntilDay(day)}" : $"{what}, the best rated not watched yet first";
	}

	// the collection of a candidate (stream t's model): "Spotlight: Jean-Pierre Melville", "Director · 8 films, 6 unwatched · until
	// Sun 29 Nov", its titles in the Spotlight's order (kept: InListOrder)
	private static SmartCollection Collection(BuildContext context, SpotlightCandidate candidate, SpotlightWeek? week)
	{
		IReadOnlyList<IndexEntry> titles = [.. candidate.Titles.Take(Most)];
		// review (Major 1): unwatched as the anchors count it (a show 90 % watched is watched), not the server's Played; the
		// unwatched come first, so the first Most hold that many of them
		var reason = ReasonLine.Of(Role(candidate.Subject), ReasonCount.Titles(titles.Count, ReasonCount.OnlyOf(titles), Math.Min(candidate.Unwatched, titles.Count)))
			with { Until = week?.Sunday };
		return Make.Of(context, candidate.Id, SmartCollectionKind.Spotlight, NixieTitles.Spotlight(candidate.Name), reason, hasOwnOrder: false, titles,
			why: Why(candidate.Subject, candidate.Name, week?.Sunday)) with { InListOrder = true };
	}

	// ---- the gates ---------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Everyone who passes every gate, in id order; <paramref name="counts"/> says how many passed each. Made once an index (and
	/// its outside lists): Home, the Collections page and the Libraries page each build the same index, and only the first pays.
	/// </summary>
	public static IReadOnlyList<SpotlightCandidate> Candidates(BuildContext context, SpotlightCounts? counts = null)
	{
		if (s_made.TryGetValue(context.Index, out var made) && ReferenceEquals(made.Outside, context.Outside))
		{
			counts?.Take(made.Counts);
			return made.Candidates;
		}

		var fresh = new SpotlightCounts();
		var held = new Dictionary<string, Func<SpotlightCandidate>>(StringComparer.Ordinal);
		var candidates = Gate(context, fresh, held);
		s_made.AddOrUpdate(context.Index, new Made(context.Outside, candidates, fresh, held));
		counts?.Take(fresh);
		return candidates;
	}

	/// <summary>
	/// 1.8 (the review's Minor 3): one who may keep the week's Spotlight they already have: past the count and with
	/// <see cref="LeastUnwatched"/> or more unwatched now (the people in), whatever the quality and coherence gates say of them
	/// this time (they rank against the others, which come and go); null otherwise.
	/// </summary>
	public static SpotlightCandidate? Held(BuildContext context, string id)
	{
		_ = Candidates(context);
		return s_made.TryGetValue(context.Index, out var made) && made.Held.TryGetValue(id, out var make) ? make() : null;
	}

	// the candidates made of an index, with the outside lists they were scored with
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IndexSnapshot, Made> s_made = new();

	private sealed record Made(global::FluentFin.Core.Outside.OutsideListsSnapshot Outside, IReadOnlyList<SpotlightCandidate> Candidates, SpotlightCounts Counts,
		IReadOnlyDictionary<string, Func<SpotlightCandidate>> Held);

	/// <param name="held">Filled with those past the count and the unwatched gates, made when asked (<see cref="Held"/>).</param>
	private static IReadOnlyList<SpotlightCandidate> Gate(BuildContext context, SpotlightCounts counts, Dictionary<string, Func<SpotlightCandidate>> held)
	{
		var titles = context.Titles.Entries;
		var quality = context.Quality;

		// each title's score, and the catalogue's 70th percentile for films and for shows
		var scores = new Dictionary<Guid, double>(titles.Count);
		var ofKind = new Dictionary<IndexKind, List<double>>();
		foreach (var title in titles)
		{
			if (quality.Of(title) is { } q)
			{
				scores[title.Id] = q;
				(ofKind.TryGetValue(title.Kind, out var list) ? list : ofKind[title.Kind] = []).Add(q);
			}
		}

		var bar = ofKind.ToDictionary(x => x.Key, x => NearestRank(x.Value, Percentile));

		// review (Major 1): watched as stream a's anchors say it (AnchorFinder.Finished): a film played, a show with 90 % of its
		// episodes played (any copy's). The server's Played alone counted a show at 30 of 31 episodes as unwatched
		var finished = titles.Where(x => Anchors.AnchorFinder.Finished(x, context.Titles, context.Index.SeriesPlays)).Select(x => x.Id).ToHashSet();

		// 1. by count: directors (films), actors (the first three billed), the curated studios
		// 1.8 (stream a's PeopleCovered): directors and actors only once the people of 98 % of the titles are in; a people step
		// cut short, or its keep a minute in, has a part of them, and a Spotlight of those would change as the rest came.
		// Studios don't wait (the titles read has them)
		var people = context.Index.PeopleCovered;
		counts.PeopleWaiting = !people;
		var catalogue = new Catalogue(titles, people);
		var passed = new List<(Gathered Group, string Key, int Unwatched)>();
		foreach (var group in catalogue.Groups)
		{
			var least = group.Subject switch { SpotlightSubject.Director => DirectorFilms, SpotlightSubject.Actor => ActorTitles, _ => StudioTitles };
			if (group.Titles.Count < least || Make.Key(group.Name) is not { Length: > 0 } key)
			{
				continue;
			}

			counts.Add(group.Subject, SpotlightGate.Count);

			// 2. five or more unwatched
			var unwatched = group.Titles.Count(x => !finished.Contains(x.Id));
			if (unwatched < LeastUnwatched)
			{
				continue;
			}

			counts.Add(group.Subject, SpotlightGate.Unwatched);
			var (heldGroup, heldKey, heldUnwatched) = (group, key, unwatched);
			held.TryAdd(IdOf(group.Subject, key), () => new SpotlightCandidate(IdOf(heldGroup.Subject, heldKey), heldGroup.Subject, heldGroup.Name, Ordered(heldGroup.Titles, scores, finished), heldUnwatched, double.NaN));

			// 3. their best six each at or above the 70th percentile of its kind
			var best = group.Titles.Where(x => scores.ContainsKey(x.Id)).OrderByDescending(x => scores[x.Id]).Take(BestTitles).ToList();
			if (best.Count < BestTitles || best.Any(x => !bar.TryGetValue(x.Kind, out var line) || scores[x.Id] < line))
			{
				continue;
			}

			counts.Add(group.Subject, SpotlightGate.Notable);
			passed.Add((group, key, unwatched));
		}

		// 4. coherence: the bottom third of each subject's go
		var tops = passed.ToDictionary(x => x.Group, x => Top(x.Group.Titles, scores), ReferenceEqualityComparer.Instance);
		// 1.9 (fix 8): the titles the user watched are measured too: each candidate's taste is its likeness to them
		var taste = Taste.Of(context.Titles, context.Index.SeriesPlays);
		// 1.9 (the Phase 3 review, item 8): a show counts toward the nearness from three episodes played, or finished (as it
		// anchors More like): one sampled episode weighed a third of a binge, and the shows of a few voice actors tried once each
		// put all of them among the nearest
		var watched = taste.Any ? titles.Where(x => taste.WeightOf(x.Id) > 0
			&& (x.Kind != IndexKind.Series || Anchors.AnchoredSlots.AnchorsMoreLike(x, context.Titles, context.Index.SeriesPlays))).ToList() : [];
		var similarity = new Similarity(catalogue, tops.Values.SelectMany(x => x).Concat(watched));
		var coherent = new List<(SpotlightCandidate Candidate, List<IndexEntry> Top)>();
		foreach (var subject in passed.GroupBy(x => x.Group.Subject))
		{
			var scored = subject
				.Select(x => (Candidate: x, Coherence: similarity.Mean(tops[x.Group], similarity.Defining(x.Group, catalogue))))
				.OrderBy(x => x.Coherence).ThenBy(x => x.Candidate.Key, StringComparer.Ordinal)
				.ToList();
			foreach (var ((group, key, unwatched), coherence) in scored.Skip(scored.Count / 3))
			{
				counts.Add(group.Subject, SpotlightGate.Coherent);
				coherent.Add((new SpotlightCandidate(IdOf(group.Subject, key), group.Subject, group.Name, Ordered(group.Titles, scores, finished), unwatched, coherence), tops[group]));
			}
		}

		var weighed = Weighed(coherent, similarity, watched, taste);

		// a person who passes as director and actor is a director; two names with one key ("Jean-Pierre", "Jean Pierre") are one,
		// the one with more titles
		var directors = weighed.Where(x => x.Subject == SpotlightSubject.Director).Select(x => x.Id["spotlight:director:".Length..]).ToHashSet(StringComparer.Ordinal);
		var made = Nearest([.. weighed.Where(x => x.Subject != SpotlightSubject.Actor || !directors.Contains(x.Id["spotlight:actor:".Length..]))
			.GroupBy(x => x.Id, StringComparer.Ordinal).Select(g => g.OrderByDescending(x => x.Titles.Count).ThenBy(x => x.Name, StringComparer.Ordinal).First())
			.OrderBy(x => x.Id, StringComparer.Ordinal)], taste);
		counts.Nearest = made.Count(x => x.Nearest); // 1.9 (R5): the census says how many the draw takes from
		return made;
	}

	// 1.9 (Phase 3, R5: "the Spotlight from their taste, not random"): the week's draw takes only the nearest by likeness, the
	// top 5 % of the candidates narrowing to 1 % as the evidence grows (the share Taste's shrink leaves, Smoothing / (evidence +
	// Smoothing): 3 % for the owner's), at least NearestFewest. Weighting alone wasn't enough on a big library with thin
	// metadata: the owner's 11,812 candidates gave the 983 with a taste of 2 or more only a quarter of the draw, and the weeks
	// went to a 1970s anime voice actor and a 1990s kickboxing actor. The likeness is a mean over each candidate's best
	// CoherenceOf titles (Weighed), so a subject of 300 titles doesn't win on volume. With no history none is the nearest, and
	// the draw is 1.8's
	private static List<SpotlightCandidate> Nearest(List<SpotlightCandidate> candidates, Taste taste)
	{
		if (!taste.Any || !candidates.Any(x => x.Likeness is not null))
		{
			return candidates;
		}

		var share = Math.Clamp(Taste.Smoothing / (taste.Total + Taste.Smoothing), NearestLeast, NearestMost);
		var take = Math.Max(NearestFewest, (int)Math.Ceiling(share * candidates.Count));
		if (take >= candidates.Count)
		{
			return candidates; // a library whose candidates are all among the nearest: 1.9's weighted draw over them all
		}

		var nearest = candidates.OrderByDescending(x => x.Likeness ?? 0).ThenBy(x => x.Id, StringComparer.Ordinal).Take(take).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
		return [.. candidates.Select(x => nearest.Contains(x.Id) ? x with { Nearest = true } : x)];
	}

	// 1.9 (the owner's library, fix 8: "Give the Spotlight some taste"): each candidate's taste is how like what the user watched
	// its best titles are, over the candidates' mean, so the average candidate weighs 1. Little evidence counts little (shrunk
	// toward 1 by evidence / (evidence + Taste.Smoothing)). With no history every candidate weighs 1 and the draw is 1.8's. On
	// the owner's library, Ealing (British films of the 1940s and 50s, nothing near what they watch) was drawn over an anime studio
	// whose show they had watched 46 episodes of, by chance.
	// Phase 3 (R5): the likeness is the mean of its best titles' nearness to the watched title each is most like (each
	// feature weighted by its rarity here, the defining name included: a studio whose show they watched 46 episodes of is
	// near; Similarity.Nearness), not fix 8's cosine against all they watched at once. That one profile leaned to the kinds
	// sharing the most genres: on the owner-shaped test library (SpotlightTop190Tests) none of its nearest 43 was horror or
	// prestige drama, 23 of the 87 shows played. A mean, not a sum: a subject of 300 titles doesn't win on volume
	private static List<SpotlightCandidate> Weighed(List<(SpotlightCandidate Candidate, List<IndexEntry> Top)> coherent, Similarity similarity, List<IndexEntry> watched, Taste taste)
	{
		if (!taste.Any || coherent.Count == 0)
		{
			return [.. coherent.Select(x => x.Candidate)];
		}

		var near = similarity.Nearness([.. watched.Select(x => (x, taste.WeightOf(x.Id)))]);
		var likeness = coherent.Select(x => x.Top.Count == 0 ? 0 : x.Top.Average(near)).ToList();
		var mean = likeness.Average();
		if (mean <= 0)
		{
			return [.. coherent.Select(x => x.Candidate)];
		}

		var shrink = taste.Total / (taste.Total + Taste.Smoothing);
		return [.. coherent.Select((x, i) => x.Candidate with { Taste = Math.Clamp(1 + (((likeness[i] / mean) - 1) * shrink), TasteLeast, TasteMost), Likeness = likeness[i] })]; // 1.9 (R5): the likeness kept
	}

	/// <summary>A Spotlight's id: "spotlight:director:jeanpierremelville", "spotlight:studio:studioghibli" (never a retired kind's prefix).</summary>
	public static string IdOf(SpotlightSubject subject, string key) => $"spotlight:{subject.ToString().ToLowerInvariant()}:{key}";

	/// <summary>
	/// The week's pick. 1.8 (the review's Minor 3) with the weeks' picks kept on this PC (<paramref name="picks"/>): this week's
	/// kept one holds all week while it still has <see cref="LeastUnwatched"/> unwatched (<paramref name="held"/> makes it when
	/// the other gates rank it out meanwhile); else the candidate with the highest weight drawn by the week's seed and its id
	/// (rendezvous hashing), never last week's kept pick (none when it is the only one). The caller keeps what this returns.
	/// <para>
	/// With nothing kept (<paramref name="picks"/> null: a first week, a test, the TV's same lineup for the same index and date)
	/// the pick is the index's and the date's alone: the same draw, never the one last week's draw could have been
	/// (<see cref="Rounds"/> rounds back; about 1 week in 180 repeats with four candidates, as the review measured), three or
	/// fewer by turns. It can move mid-week when the candidates change; kept picks can't.
	/// </para>
	/// <para>
	/// 1.9 (Phase 3, R5): with the user's history, both draw only among the nearest by taste (<see cref="SpotlightCandidate.Nearest"/>),
	/// and with picks kept none of the weeks kept comes again while one of the nearest is left. This week's kept pick still holds,
	/// wherever it ranks now. With no history it is all as above. 1.9 (the rebuild's blocker 3): among the nearest a candidate
	/// weighs by its place by likeness (<see cref="NearestDecay"/>), not its taste, so the most alike get most of the weeks.
	/// </para>
	/// </summary>
	public static SpotlightCandidate? Pick(IReadOnlyList<SpotlightCandidate> candidates, SpotlightWeek week, IReadOnlyDictionary<string, string>? picks = null,
		Func<string, SpotlightCandidate?>? held = null)
	{
		// 1.9 (Phase 3, R5): the nearest by taste only, when the user's history marks any (SpotlightCandidate.Nearest); with no
		// history all, as 1.8
		var nearest = candidates.Any(x => x.Nearest) ? candidates.Where(x => x.Nearest).ToList() : null;
		var drawn = nearest ?? candidates;
		var weight = WeightsOf(nearest); // 1.9 (the rebuild's blocker 3): by place among the nearest; null: each one's taste
		if (picks is not null)
		{
			var kept = picks.GetValueOrDefault(week.Seed);
			if (kept is not null && (candidates.FirstOrDefault(x => x.Id == kept) ?? held?.Invoke(kept)) is { } holds)
			{
				return holds; // the week's kept pick holds, wherever it ranks now
			}

			var last = picks.GetValueOrDefault(week.Before(1).Seed);
			if (nearest is not null)
			{
				// 1.9 (R5): a draw from the few nearest would come back within weeks: none of the weeks kept on this PC comes
				// again while one of the nearest is left (NearestFewest is more than the weeks kept)
				var recent = Enumerable.Range(1, SmartCollectionStore.SpotlightWeeksKept).Select(w => picks.GetValueOrDefault(week.Before(w).Seed)).Append(kept)
					.OfType<string>().ToHashSet(StringComparer.Ordinal);
				var fresh = nearest.Where(x => !recent.Contains(x.Id)).ToList();
				if (fresh.Count > 0)
				{
					return fresh.MaxBy(x => Drawn(week.Seed, x, weight), DrawOrder.Instance);
				}
			}

			var pool = drawn.Where(x => x.Id != last && x.Id != kept).ToList();
			return pool.Count == 0 ? null : pool.MaxBy(x => Drawn(week.Seed, x, weight), DrawOrder.Instance);
		}

		if (drawn.Count == 0)
		{
			return null;
		}

		if (drawn.Count <= 3)
		{
			var turns = drawn.OrderByDescending(x => Weight("turns", x.Id)).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
			return turns[Turn(week.Number, turns.Count)];
		}

		return Round(drawn, week, Rounds, weight);
	}

	// 1.9 (the rebuild's blocker 3): each of the nearest's weight in the draw by its place among them by likeness, the most alike
	// first (exp(-place / NearestDecay)); null with no history (each one's taste weighs, as 1.8 and fix 8)
	private static Dictionary<string, double>? WeightsOf(List<SpotlightCandidate>? nearest) => nearest is null ? null
		: nearest.OrderByDescending(x => x.Likeness ?? 0).ThenBy(x => x.Id, StringComparer.Ordinal)
			.Select((x, place) => (x.Id, Weight: Math.Exp(-place / NearestDecay))).ToDictionary(x => x.Id, x => x.Weight, StringComparer.Ordinal);

	/// <summary>A week's turn among <paramref name="count"/>: 0 to count - 1 whatever the week's number (one before 2001 is negative).</summary>
	internal static int Turn(long number, int count) => (int)(((number % count) + count) % count);

	/// <summary>How many weeks back the draw looks, each round leaving out the picks the round before could have made.</summary>
	public const int Rounds = 4;

	// round r: the week's draw leaving out last week's rounds 0 to r-1 (round 0: the plain draw)
	private static SpotlightCandidate Round(IReadOnlyList<SpotlightCandidate> candidates, SpotlightWeek week, int round, Dictionary<string, double>? weight = null)
	{
		var left = candidates;
		if (round > 0)
		{
			var before = week.Before(1);
			var out_ = new HashSet<string>(StringComparer.Ordinal);
			for (var r = 0; r < round; r++)
			{
				out_.Add(Round(candidates, before, r, weight).Id);
			}

			var fresh = candidates.Where(x => !out_.Contains(x.Id)).ToList();
			left = fresh.Count > 0 ? fresh : candidates;
		}

		return left.MaxBy(x => Drawn(week.Seed, x, weight), DrawOrder.Instance)!;
	}

	/// <summary>The draw's weight of an id in a week: FNV-1a (64-bit) of "2026-W48|spotlight:…" in UTF-8, the same on every PC and the TV.</summary>
	public static ulong Weight(string seed, string id)
	{
		var hash = 14695981039346656037UL;
		foreach (var b in Encoding.UTF8.GetBytes($"{seed}|{id}"))
		{
			hash ^= b;
			hash *= 1099511628211UL;
		}

		// FNV's last bytes mix weakly into the high bits; a final avalanche (splitmix64's) spreads them
		hash ^= hash >> 30;
		hash *= 0xbf58476d1ce4e5b9UL;
		hash ^= hash >> 27;
		hash *= 0x94d049bb133111ebUL;
		hash ^= hash >> 31;
		return hash;
	}

	/// <summary>
	/// 1.9 (the owner's library, fix 8): a candidate's place in a week's draw, highest first: weighted rendezvous hashing,
	/// <c>ln(U) / taste</c> with U its <see cref="Weight"/>'s top 53 bits read as a number in (0, 1]: an exponential race, so of two
	/// candidates in a plain draw one of twice the taste wins twice as often (the picks kept on this PC and the rounds that leave
	/// out last week's change the shares), and one joining or leaving that isn't the pick doesn't move it; ties go by the weight
	/// and the id, as 1.8's draw. With every taste 1 (no history) the order is 1.8's: ln(U) never falls as the weight rises, and
	/// equal keys (weights alike in their top 53 bits) go by the weight.
	/// </summary>
	/// <param name="weights">1.9 (the rebuild's blocker 3): the nearest's weights by place (<see cref="NearestDecay"/>) in place of the taste; null: the taste.</param>
	internal static (double Key, ulong Weight, string Id) Drawn(string seed, SpotlightCandidate candidate, IReadOnlyDictionary<string, double>? weights = null)
	{
		var weight = Weight(seed, candidate.Id);
		var u = ((weight >> 11) + 1) * (1.0 / (1UL << 53));
		var by = weights is not null && weights.TryGetValue(candidate.Id, out var placed) ? placed : Math.Clamp(candidate.Taste, TasteLeast, TasteMost);
		return (Math.Log(u) / by, weight, candidate.Id);
	}

	private sealed class DrawOrder : IComparer<(double Key, ulong Weight, string Id)>
	{
		public static DrawOrder Instance { get; } = new();

		public int Compare((double Key, ulong Weight, string Id) x, (double Key, ulong Weight, string Id) y)
		{
			var byKey = x.Key.CompareTo(y.Key);
			if (byKey != 0)
			{
				return byKey;
			}

			var byWeight = x.Weight.CompareTo(y.Weight);
			return byWeight != 0 ? byWeight : string.CompareOrdinal(y.Id, x.Id); // a tie: the first id wins
		}
	}

	// the Spotlight's order: the best-scored unwatched first, then the watched ones (best first too); in each, the unrated last,
	// then by rating, name and id (Best.Of's order, from the scores already worked out). 1.9 (fix 8): the score reads the critics'
	// view (QualityScore.RatingOf), so two 7.6s with critics' 100 and 34 no longer tie; no tie on the critics' value itself (it
	// put a title without critics last among equals: missing data ranked low, rule 4)
	private static List<IndexEntry> Ordered(IEnumerable<IndexEntry> titles, Dictionary<Guid, double> scores, HashSet<Guid> finished) =>
		[.. titles.OrderBy(x => finished.Contains(x.Id)).ThenBy(x => scores.ContainsKey(x.Id) ? 0 : 1).ThenByDescending(x => scores.GetValueOrDefault(x.Id))
			.ThenByDescending(x => x.Rating ?? 0).ThenBy(x => x.OrderName, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id)];

	// their best twelve by score (the unrated after): what coherence measures
	private static List<IndexEntry> Top(List<IndexEntry> titles, Dictionary<Guid, double> scores) =>
		[.. titles.OrderBy(x => scores.ContainsKey(x.Id) ? 0 : 1).ThenByDescending(x => scores.GetValueOrDefault(x.Id)).ThenBy(x => x.OrderName, StringComparer.Ordinal).ThenBy(x => x.Id).Take(CoherenceOf)];

	/// <summary>The nearest-rank percentile of <paramref name="values"/> (0.70: the value 70 % of them are at or under).</summary>
	internal static double NearestRank(List<double> values, double percentile)
	{
		values.Sort();
		var rank = (int)Math.Ceiling(percentile * values.Count);
		return values[Math.Clamp(rank - 1, 0, values.Count - 1)];
	}

	/// <summary>A subject's titles here, gathered by name (a person's names that differ only in case are one).</summary>
	private sealed class Gathered(SpotlightSubject subject, string name)
	{
		public SpotlightSubject Subject { get; } = subject;

		public string Name { get; } = name;

		public List<IndexEntry> Titles { get; } = [];

		// the titles come one at a time, each once (the canonical titles): a name twice in one title (directed and directed, two
		// of a studio's names) is that title again, the last one added
		public void Add(IndexEntry title)
		{
			if (Titles.Count == 0 || !ReferenceEquals(Titles[^1], title))
			{
				Titles.Add(title);
			}
		}
	}

	/// <summary>
	/// One pass over the catalogue: the titles of each director (films), actor (the first three billed) and curated studio, each
	/// title once, and how many titles have each genre, keyword, decade, country and studio name (coherence's rarity: no second
	/// pass). Names are matched as they are, case aside: no key is made for each of a big library's 150,000 credits (that took
	/// most of a second), only for those who pass the count.
	/// </summary>
	private sealed class Catalogue
	{
		private readonly Dictionary<string, int> _directed = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, int> _billed = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, Gathered> _directors = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, Gathered> _actors = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<SpotlightStudio, Gathered> _curated = new(ReferenceEqualityComparer.Instance);
		private readonly Dictionary<string, StudioName> _studios = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, int> _genres = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, int> _keywords = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, int> _countries = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, int> _decades = new(StringComparer.Ordinal);

		/// <param name="people">The index's people are in (<see cref="IndexSnapshot.PeopleCovered"/>): else no director or actor is counted.</param>
		public Catalogue(IReadOnlyList<IndexEntry> titles, bool people)
		{
			Count = titles.Count;
			People = people;

			// first how many titles each person has (most of a big library's people are in one or two: nothing is gathered for
			// them), then the titles of those with enough
			foreach (var title in people ? titles : [])
			{
				Credits(title, (directed, name) =>
				{
					ref var n = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(directed ? _directed : _billed, name, out _);
					n++;
				});
			}

			foreach (var (name, n) in _directed)
			{
				if (n >= DirectorFilms)
				{
					_directors[name] = new Gathered(SpotlightSubject.Director, name);
				}
			}

			foreach (var (name, n) in _billed)
			{
				if (n >= ActorTitles)
				{
					_actors[name] = new Gathered(SpotlightSubject.Actor, name);
				}
			}

			foreach (var title in titles)
			{
				if (_directors.Count > 0 || _actors.Count > 0)
				{
					Credits(title, (directed, name) =>
					{
						if ((directed ? _directors : _actors).TryGetValue(name, out var group))
						{
							group.Add(title);
						}
					});
				}

				foreach (var name in title.Studios ?? [])
				{
					if (string.IsNullOrWhiteSpace(name))
					{
						continue;
					}

					if (!_studios.TryGetValue(name, out var seen))
					{
						_studios[name] = seen = new StudioName(SpotlightStudios.Of(name));
					}

					seen.Titles++;
					if (seen.Curated is { } studio)
					{
						(_curated.TryGetValue(studio, out var group) ? group : _curated[studio] = new Gathered(SpotlightSubject.Studio, studio.Name)).Add(title);
					}
				}

				Tally(_genres, title.Genres);
				Tally(_keywords, title.Tags);
				Tally(_countries, title.Countries);
				if (Feature.DecadeOf(title) is { } decade)
				{
					_decades[decade] = _decades.GetValueOrDefault(decade) + 1;
				}
			}
		}

		/// <summary>The catalogue's titles.</summary>
		public int Count { get; }

		/// <summary>The people are in: directors and actors are counted, and coherence reads them.</summary>
		public bool People { get; }

		/// <summary>Every director, actor and curated studio found.</summary>
		public IEnumerable<Gathered> Groups => _directors.Values.Concat(_actors.Values).Concat(_curated.Values);

		/// <summary>The curated studio a studio name is (names the pass never saw are looked up).</summary>
		public SpotlightStudio? CuratedOf(string name) => _studios.TryGetValue(name, out var seen) ? seen.Curated : SpotlightStudios.Of(name);

		/// <summary>How many of the catalogue's titles have the feature.</summary>
		public int Titles(Feature feature) => feature.Kind switch
		{
			FeatureKind.Genre => _genres.GetValueOrDefault(feature.Value),
			FeatureKind.Keyword => _keywords.GetValueOrDefault(feature.Value),
			FeatureKind.Country => _countries.GetValueOrDefault(feature.Value),
			FeatureKind.Decade => _decades.GetValueOrDefault(feature.Value),
			FeatureKind.Studio => _studios.TryGetValue(feature.Value, out var seen) ? seen.Titles : 0,
			_ => PersonTitles(feature.Value),
		};

		// a person's titles as director or one of the first three billed, a title they are both in counted once (where both lists
		// were gathered; else the two counts)
		private int PersonTitles(string name)
		{
			var directed = _directors.GetValueOrDefault(name)?.Titles;
			var acted = _actors.GetValueOrDefault(name)?.Titles;
			return directed is not null && acted is not null
				? directed.Concat(acted).Distinct(ReferenceEqualityComparer.Instance).Count()
				: _directed.GetValueOrDefault(name) + _billed.GetValueOrDefault(name);
		}

		// a title's credits the Spotlight counts: its directors (a film's) and its first three billed actors, each name trimmed
		private static void Credits(IndexEntry title, Action<bool, string> credit)
		{
			var billed = 0;
			foreach (var person in title.People ?? [])
			{
				if (person?.Name is not { } raw || string.IsNullOrWhiteSpace(raw))
				{
					continue;
				}

				if (person.Kind == "Director" && title.Kind == IndexKind.Movie)
				{
					credit(true, raw.Trim());
				}
				else if (person.Kind == "Actor" && billed++ < TopBilled)
				{
					credit(false, raw.Trim());
				}
			}
		}

		private static void Tally(Dictionary<string, int> counts, string[]? values)
		{
			foreach (var value in values ?? [])
			{
				if (!string.IsNullOrEmpty(value))
				{
					counts[value] = counts.GetValueOrDefault(value) + 1;
				}
			}
		}

		private sealed class StudioName(SpotlightStudio? curated)
		{
			public SpotlightStudio? Curated { get; } = curated;

			public int Titles { get; set; }
		}
	}

	// ---- coherence: a rarity-weighted overlap of everything but the defining name ----------------------------------------------

	private enum FeatureKind : byte
	{
		Genre,
		Keyword,
		Decade,
		Country,
		Studio,
		Person,
	}

	/// <summary>A title's feature: its kind and its value, the value matched case aside.</summary>
	private readonly record struct Feature(FeatureKind Kind, string Value)
	{
		private static readonly string[] s_decades = [.. Enumerable.Range(188, 25).Select(d => (d * 10).ToString(CultureInfo.InvariantCulture))];

		/// <summary>A title's decade as a feature's value ("1960"); null with no year, or one out of range.</summary>
		public static string? DecadeOf(IndexEntry title) => title.Year is >= 1880 and < 2130 and var year ? s_decades[(year / 10) - 188] : null;

		public static IEqualityComparer<Feature> Comparer { get; } = new FeatureComparer();

		private sealed class FeatureComparer : IEqualityComparer<Feature>
		{
			public bool Equals(Feature x, Feature y) => x.Kind == y.Kind && string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);

			public int GetHashCode(Feature f) => HashCode.Combine(f.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(f.Value));
		}
	}

	/// <summary>
	/// The similarity of two titles: the weight of the features they share over the weight of all either has (a weighted
	/// Jaccard), each feature weighted by its rarity among the catalogue's titles, log(N / n): "Drama" counts for almost
	/// nothing, a keyword or a supporting actor's name for a lot. Each feature of the measured titles is numbered once, and a
	/// title is its sorted numbers, so a pair is one merge (a big library's 377 candidates took a fifth of a second as sets).
	/// </summary>
	private sealed class Similarity
	{
		private readonly Dictionary<Feature, int> _number = new(Feature.Comparer);
		private readonly List<Feature> _features = [];
		private readonly List<int> _studios = [];
		private readonly Dictionary<Guid, int[]> _titles = [];
		private readonly double[] _weight;
		private readonly bool _people;

		public Similarity(Catalogue catalogue, IEnumerable<IndexEntry> measured)
		{
			_people = catalogue.People;
			var numbers = new List<int>(16);
			foreach (var title in measured)
			{
				if (_titles.ContainsKey(title.Id))
				{
					continue;
				}

				numbers.Clear();
				Each(title, f =>
				{
					if (f.Kind == FeatureKind.Person && !_people)
					{
						return; // a part of the people only: no person's rarity is known
					}

					if (!_number.TryGetValue(f, out var n))
					{
						_number[f] = n = _features.Count;
						_features.Add(f);
						if (f.Kind == FeatureKind.Studio)
						{
							_studios.Add(n);
						}
					}

					numbers.Add(n);
				});
				numbers.Sort();
				_titles[title.Id] = [.. numbers.Distinct()];
			}

			// each feature's rarity: how many of the catalogue's titles have it (counted by the catalogue's one pass)
			var all = Math.Max(1, catalogue.Count);
			_weight = [.. _features.Select(f => Math.Log((double)all / Math.Max(1, catalogue.Titles(f))))];
		}

		/// <summary>The numbers of a subject's own features, left out of its coherence: the person, or any name of the studio.</summary>
		public int[] Defining(Gathered subject, Catalogue catalogue) => subject.Subject == SpotlightSubject.Studio
			? [.. _studios.Where(n => string.Equals(catalogue.CuratedOf(_features[n].Value)?.Name, subject.Name, StringComparison.Ordinal))]
			: _number.TryGetValue(new Feature(FeatureKind.Person, subject.Name), out var n) ? [n] : [];

		/// <summary>
		/// 1.9 (Phase 3, R5): how near each measured title is to what the user watched: its similarity (as <see cref="Mean"/>'s, the
		/// defining names included) to the watched title it is most like, times that title's weight over a binged show's
		/// (<see cref="Taste.ShowMost"/>: the shows with the most episodes played count most). A title watched is near itself.
		/// Each of the watched titles' features lists the watched titles that have it, so a title is one pass over its own features.
		/// </summary>
		public Func<IndexEntry, double> Nearness(IReadOnlyList<(IndexEntry Title, double Weight)> watched)
		{
			var sets = watched.Select(x => _titles.TryGetValue(x.Title.Id, out var set) ? set : []).ToArray();
			var totals = sets.Select(set => set.Sum(n => _weight[n])).ToArray();
			var factors = watched.Select(x => Math.Clamp(x.Weight / Taste.ShowMost, 0, 1)).ToArray();
			var postings = new List<int>?[_features.Count];
			for (var i = 0; i < sets.Length; i++)
			{
				foreach (var n in sets[i])
				{
					(postings[n] ??= []).Add(i);
				}
			}

			var shared = new double[sets.Length];
			var seen = new bool[sets.Length];
			var touched = new List<int>();
			var made = new Dictionary<Guid, double>();
			return title =>
			{
				if (made.TryGetValue(title.Id, out var near))
				{
					return near;
				}

				var set = _titles.TryGetValue(title.Id, out var features) ? features : [];
				var total = 0.0;
				foreach (var n in set)
				{
					total += _weight[n];
					foreach (var i in postings[n] ?? [])
					{
						if (!seen[i])
						{
							seen[i] = true;
							touched.Add(i);
						}

						shared[i] += _weight[n];
					}
				}

				near = 0;
				foreach (var i in touched)
				{
					var either = total + totals[i] - shared[i];
					if (either > 0)
					{
						near = Math.Max(near, shared[i] / either * factors[i]);
					}

					shared[i] = 0;
					seen[i] = false;
				}

				touched.Clear();
				made[title.Id] = near;
				return near;
			};
		}

		/// <summary>The mean similarity of every pair of <paramref name="titles"/>, leaving the features numbered <paramref name="defining"/> out; 0 for fewer than two.</summary>
		public double Mean(IReadOnlyList<IndexEntry> titles, int[] defining)
		{
			if (titles.Count < 2)
			{
				return 0;
			}

			var sets = titles.Select(x => _titles.TryGetValue(x.Id, out var set) ? set : []).ToList();
			var sum = 0.0;
			var pairs = 0;
			for (var i = 0; i < sets.Count; i++)
			{
				for (var j = i + 1; j < sets.Count; j++)
				{
					sum += Of(sets[i], sets[j], defining);
					pairs++;
				}
			}

			return sum / pairs;
		}

		// two sorted lists of numbers merged: the weight both have over the weight either has
		private double Of(int[] a, int[] b, int[] defining)
		{
			double shared = 0, either = 0;
			int i = 0, j = 0;
			while (i < a.Length || j < b.Length)
			{
				int n;
				var both = false;
				if (j >= b.Length || (i < a.Length && a[i] < b[j]))
				{
					n = a[i++];
				}
				else if (i >= a.Length || b[j] < a[i])
				{
					n = b[j++];
				}
				else
				{
					n = a[i++];
					j++;
					both = true;
				}

				if (Array.IndexOf(defining, n) >= 0)
				{
					continue;
				}

				either += _weight[n];
				shared += both ? _weight[n] : 0;
			}

			return either <= 0 ? 0 : shared / either;
		}

		// a title's features: its genres, keywords, decade, countries, studios, directors and first three billed
		private static void Each(IndexEntry title, Action<Feature> feature)
		{
			foreach (var genre in title.Genres ?? [])
			{
				feature(new Feature(FeatureKind.Genre, genre));
			}

			foreach (var tag in title.Tags ?? [])
			{
				feature(new Feature(FeatureKind.Keyword, tag));
			}

			if (Feature.DecadeOf(title) is { } decade)
			{
				feature(new Feature(FeatureKind.Decade, decade));
			}

			foreach (var country in title.Countries ?? [])
			{
				feature(new Feature(FeatureKind.Country, country));
			}

			foreach (var studio in title.Studios ?? [])
			{
				feature(new Feature(FeatureKind.Studio, studio));
			}

			var billed = 0;
			foreach (var person in title.People ?? [])
			{
				if (person?.Name is { Length: > 0 } name && ((person.Kind == "Director" && title.Kind == IndexKind.Movie) || (person.Kind == "Actor" && billed++ < TopBilled)))
				{
					feature(new Feature(FeatureKind.Person, name.Trim()));
				}
			}
		}
	}
}

/// <summary>
/// Nixie 1.8 (the review's Minor 3): where the weeks' Spotlight picks are kept on this PC, per server and user
/// (<see cref="SmartCollectionStore"/>), so a week's holds all week and the next week's is another.
/// </summary>
public interface ISpotlightPicks
{
	/// <summary>
	/// The picks kept, by ISO week ("2026-W48" → its id). 1.8.1 (BH-180-q1): <paramref name="of"/>, an index's
	/// <see cref="IndexSnapshot.Key"/>: the picks of that server and user only (none when another sign-in's are kept now);
	/// null: the signed-in one's.
	/// </summary>
	Task<IReadOnlyDictionary<string, string>> SpotlightPicksAsync(string? of = null);

	/// <summary>Keeps the week's pick; 1.8.1 (BH-180-q1): for <paramref name="of"/> only (nothing when another sign-in's are kept now).</summary>
	Task SaveSpotlightPick(string week, string id, string? of = null);
}

/// <summary>The Spotlight's gates, in order (<see cref="SpotlightCounts"/>).</summary>
public enum SpotlightGate
{
	Count,
	Unwatched,
	Notable,
	Coherent,
}

/// <summary>
/// Nixie 1.8 (P1-3): how many directors, actors and studios passed each Spotlight gate in a build: the census line says them
/// (counts only, never a name: "Report a problem" may attach the log).
/// </summary>
public sealed class SpotlightCounts
{
	private readonly Dictionary<(SpotlightSubject, SpotlightGate), int> _counts = [];

	public void Add(SpotlightSubject subject, SpotlightGate gate) => _counts[(subject, gate)] = Of(subject, gate) + 1;

	public int Of(SpotlightSubject subject, SpotlightGate gate) => _counts.GetValueOrDefault((subject, gate));

	/// <summary>The index's people weren't all in (<see cref="IndexSnapshot.PeopleCovered"/>): no director or actor was weighed.</summary>
	public bool PeopleWaiting { get; set; }

	/// <summary>1.9 (Phase 3, R5): how many the week's draw takes from, the nearest to what the user watched; 0: all (no history, or few).</summary>
	public int Nearest { get; set; }

	/// <summary>Another build's counts as this one's (the candidates made once an index).</summary>
	public void Take(SpotlightCounts other)
	{
		foreach (var (key, count) in other._counts)
		{
			_counts[key] = count;
		}

		PeopleWaiting = other.PeopleWaiting;
		Nearest = other.Nearest;
	}

	/// <summary>Those notable by count, every subject: the census's candidates.</summary>
	public int Considered => Enum.GetValues<SpotlightSubject>().Sum(x => Of(x, SpotlightGate.Count));

	/// <summary>
	/// "2026-W48: director 12>9>4>3, actor 30>22>6>4, studio 5>5>3>2; a pick" (count, unwatched, notable, coherent); while the
	/// people aren't all in, "…, studio 5>5>3>2, people not all in; a pick". 1.9 (R5): with the draw among the nearest only,
	/// "…, studio 5>5>3>2, 43 nearest; a pick".
	/// </summary>
	public string Census(SpotlightWeek week, bool picked) =>
		$"{week.Seed}: {string.Join(", ", Enum.GetValues<SpotlightSubject>().Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.ToString().ToLowerInvariant()} {Of(s, SpotlightGate.Count)}>{Of(s, SpotlightGate.Unwatched)}>{Of(s, SpotlightGate.Notable)}>{Of(s, SpotlightGate.Coherent)}")))}{(PeopleWaiting ? ", people not all in" : "")}{(Nearest > 0 ? string.Create(CultureInfo.InvariantCulture, $", {Nearest} nearest") : "")}; {(picked ? "a pick" : "no pick")}";
}
