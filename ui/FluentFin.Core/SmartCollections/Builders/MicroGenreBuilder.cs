using System.Globalization;

namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>Nixie 1.9: what became of one micro-genre on this library.</summary>
public enum MicroGenreVerdict
{
	/// <summary>Made: past the floor and under its kind's ceiling.</summary>
	Made,

	/// <summary>
	/// Its rule has a word list (<see cref="MicroGenreRule.TextAny"/>) and the summaries' pass hasn't read its kind's titles yet
	/// (<see cref="MicroGenreMatcher.TextCovers"/>): nothing is decided on it (rule 4).
	/// </summary>
	WaitsForText,

	/// <summary>No title fits it.</summary>
	MatchesNone,

	/// <summary>Fewer than <see cref="MicroGenreBuilder.Floor"/> unwatched titles.</summary>
	UnderFloor,

	/// <summary>More unwatched titles than its kind's ceiling: a filter, not a category.</summary>
	OverCeiling,

	/// <summary>
	/// 1.9 (stream wire): its rule names a keyword the index of an older revision didn't keep (beyond the allowlist and the six
	/// seasonal ones: <see cref="IndexSnapshot.KeywordsKnown"/>, <see cref="Keywords.KeptByRevision3"/>): unknown until the day's
	/// read brings them, never matched on what's missing.
	/// </summary>
	WaitsForKeywords,

	/// <summary>
	/// 1.9 (the review of wip/190-wire, Minor 4): a category of the seasonal family (<see cref="MicroGenreBuilder.SeasonalFamily"/>)
	/// out of its season (<see cref="SeasonalBuilder.InSeason"/>): no Christmas horror in June.
	/// </summary>
	OutOfSeason,
}

/// <summary>Nixie 1.9: one micro-genre's pool on this library (counts and the catalogue's id only, never a title).</summary>
public sealed record MicroGenrePool(string Id, IndexKind Kind, int Titles, int Unwatched, MicroGenreVerdict Verdict);

/// <summary>
/// Nixie 1.9: the micro-genres' census of one build: how many rules of each kind, each kind's titles and ceiling, and what
/// became of each rule. Counts and the catalogue's ids only, never a title or a name from the library (rule 10).
/// </summary>
public sealed record MicroGenreCensus
{
	public int Rules { get; init; }
	public int FilmRules { get; init; }
	public int ShowRules { get; init; }
	public int Films { get; init; }
	public int Shows { get; init; }
	public int FilmCeiling { get; init; }
	public int ShowCeiling { get; init; }
	public int WaitForText { get; init; }

	/// <summary>1.9 (stream wire): rules waiting for the index's read again (<see cref="MicroGenreVerdict.WaitsForKeywords"/>).</summary>
	public int WaitForKeywords { get; init; }

	/// <summary>1.9 (review Minor 4): seasonal rules out of their season (<see cref="MicroGenreVerdict.OutOfSeason"/>).</summary>
	public int OutOfSeason { get; init; }

	public int MatchNone { get; init; }
	public int UnderFloor { get; init; }
	public int OverCeiling { get; init; }
	public int Made { get; init; }

	/// <summary>Every rule's pool, in the catalogue's order.</summary>
	public IReadOnlyList<MicroGenrePool> Pools { get; init; } = [];

	/// <summary>Rules past the floor (made, or over the ceiling).</summary>
	public int PastFloor => Made + OverCeiling;

	// 1.9 (stream wire): the keyword tiers' tally, counts only (the cat stream's "pool unknown until the app's census": a
	// matchable keyword no title has, on a big library, is likely a name TMDb doesn't use)
	/// <summary>1.9: the index keeps the matchable keywords (its revision 4: <see cref="IndexSnapshot.KeywordsKnown"/>).</summary>
	public bool KeywordsKnown { get; init; } = true;

	/// <summary>1.9: the matchable keywords beyond the allowlist (the seasonal ones and the catalogue's list).</summary>
	public int MatchableKeywords { get; init; }

	/// <summary>1.9: of <see cref="MatchableKeywords"/>, how many at least one title has.</summary>
	public int MatchableSeen { get; init; }

	/// <summary>1.9: the tags the index kept, over every title (copies once).</summary>
	public int TagsKept { get; init; }

	/// <summary>1.9: the tags the server had on those titles before the tiers (<see cref="IndexEntry.TagCount"/>).</summary>
	public int TagsAll { get; init; }

	/// <summary>
	/// 1.9 (the pre-release fix B): the share of the films with a runtime, and of the shows with an episode length (0 to 1);
	/// under <see cref="MicroGenreMatcher.LengthKnownShare"/> a rule's length keeps the titles with none.
	/// </summary>
	public double FilmLengths { get; init; } = 1;

	/// <inheritdoc cref="FilmLengths"/>
	public double ShowLengths { get; init; } = 1;

	/// <summary>
	/// The census line's note (<see cref="BuildGates.Note"/>): "films 220 rules, ceiling 259; shows 80 rules, ceiling 50; text
	/// pass 5, none 12, under floor 40, over ceiling 20; keywords 241 of 298 seen, tags 60113 of 213402 kept". 1.9 (stream
	/// wire): on an index of an older revision, ", keywords to read again 76; keywords not kept yet".
	/// </summary>
	public string Note => string.Create(CultureInfo.InvariantCulture,
		$"films {FilmRules} rules, ceiling {FilmCeiling}; shows {ShowRules} rules, ceiling {ShowCeiling}; text pass {WaitForText}, none {MatchNone}, under floor {UnderFloor}, over ceiling {OverCeiling}")
		+ (WaitForKeywords > 0 ? string.Create(CultureInfo.InvariantCulture, $", keywords to read again {WaitForKeywords}") : "") // 1.9 (stream wire)
		+ (OutOfSeason > 0 ? string.Create(CultureInfo.InvariantCulture, $", out of season {OutOfSeason}") : "") // 1.9 (review Minor 4)
		// 1.9 (fix B): a kind most of whose titles have no length: its length bounds keep those (rule 4)
		+ (FilmLengths < MicroGenreMatcher.LengthKnownShare ? string.Create(CultureInfo.InvariantCulture, $", film runtimes {FilmLengths:P0} known (a length keeps those with none)") : "")
		+ (ShowLengths < MicroGenreMatcher.LengthKnownShare ? string.Create(CultureInfo.InvariantCulture, $", show episode lengths {ShowLengths:P0} known (a length keeps those with none)") : "")
		+ (KeywordsKnown
			? string.Create(CultureInfo.InvariantCulture, $"; keywords {MatchableSeen} of {MatchableKeywords} seen, tags {TagsKept} of {TagsAll} kept")
			: "; keywords not kept yet");
}

/// <summary>Nixie 1.9: what one build of the micro-genres made: the collections and the census.</summary>
public sealed record MicroGenreRun(IReadOnlyList<SmartCollection> Collections, MicroGenreCensus Census);

/// <summary>
/// Nixie 1.9 (Collections 2): a collection for each micro-genre of the catalogue (<see cref="MicroGenres"/>) the library has
/// enough of: <c>micro:&lt;id&gt;</c>, named as the catalogue names it, films or shows (never both), the titles the user
/// hasn't started first, then those started, then those finished (1.9's pre-release fix C: a show by its played episodes),
/// each part best first (<see cref="Best"/>, as the lineup orders a theme). A category is made when it has at least
/// <see cref="Floor"/> unwatched titles and no more than its kind's ceiling (<see cref="CeilingOf"/>: 1 % of that kind's
/// titles, so a show category is judged against the shows). It says its census (<see cref="MicroGenreCensus"/>) through
/// the build's gates.
/// </summary>
public sealed class MicroGenreBuilder : ISmartCollectionBuilder
{
	/// <summary>
	/// The kind the collections are of: <see cref="SmartCollectionKind.MicroGenre"/> (the wiring step, wip/190-wire). The name
	/// stays for the streams that used it as their seam before the kind existed.
	/// </summary>
	public const SmartCollectionKind PendingKind = SmartCollectionKind.MicroGenre;

	/// <summary>The fewest unwatched titles a category needs (the lineup's floor).</summary>
	public const int Floor = 8;

	/// <summary>A category's unwatched titles above this share of its kind's titles make a filter, not a category.</summary>
	public const double CeilingShare = 0.01;

	/// <summary>The ceiling is never under this (a small library's), as the lineup's theme ceiling.</summary>
	public const int CeilingLeast = 30;

	/// <summary>
	/// 1.9 (the review of wip/190-wire, Minor 4): the catalogue's family of holiday categories, made only in their season
	/// (<see cref="SeasonalBuilder.InSeason"/>: the seasonal row's own windows).
	/// </summary>
	public const string SeasonalFamily = "seasonal";

	private readonly MicroGenreCatalogue? _catalogue;

	/// <summary>The shipped catalogue's builder (<see cref="MicroGenres.Shipped"/>, read at the first build).</summary>
	public MicroGenreBuilder()
		: this(null, PendingKind)
	{
	}

	/// <param name="catalogue">The categories; null: the shipped catalogue.</param>
	/// <param name="kind">The collections' kind (<see cref="SmartCollectionKind.MicroGenre"/>; tests may give another).</param>
	public MicroGenreBuilder(MicroGenreCatalogue? catalogue, SmartCollectionKind kind = PendingKind)
	{
		_catalogue = catalogue;
		Kind = kind;
	}

	public SmartCollectionKind Kind { get; }

	// 1.9 (stream text)
	/// <summary>
	/// The summaries' matches the word lists are kept by; null: the app's, when they are the index's user's
	/// (<see cref="TextMatches.For"/>). Tests give their own.
	/// </summary>
	public TextMatches? Text { get; init; }
	// end 1.9 (stream text)

	/// <summary>The most unwatched titles a category of a kind with <paramref name="titlesOfKind"/> titles may have.</summary>
	public static int CeilingOf(int titlesOfKind) => Math.Max(CeilingLeast, (int)Math.Ceiling(CeilingShare * Math.Max(0, titlesOfKind)));

	public IEnumerable<SmartCollection> Build(BuildContext context) => Run(context).Collections;

	/// <summary>The collections and the census of <paramref name="context"/>'s titles; the census goes to its gates too.</summary>
	public MicroGenreRun Run(BuildContext context)
	{
		var catalogue = _catalogue ?? MicroGenres.Shipped;
		var matcher = MicroGenreMatcher.For(context.Titles);
		var films = matcher.TitlesOf(IndexKind.Movie);
		var shows = matcher.TitlesOf(IndexKind.Series);
		var filmCeiling = CeilingOf(films);
		var showCeiling = CeilingOf(shows);

		var made = new List<SmartCollection>();
		var pools = new List<MicroGenrePool>(catalogue.Categories.Count);
		var text = Text ?? TextMatches.For(context.Index); // 1.9 (stream text)
		var keywordsKnown = context.Index.KeywordsKnown; // 1.9 (stream wire): an index of revision 3 kept the allowlist's alone
		var plays = Anchors.IndexAnchorSource.For(context); // 1.9 (fix C)
		foreach (var category in catalogue.Categories)
		{
			var ceiling = category.Kind == IndexKind.Series ? showCeiling : filmCeiling;
			var match = matcher.Match(category, text);
			var verdict = category.Rule.WaitsForText && !matcher.TextCovers(category.Kind, text) ? MicroGenreVerdict.WaitsForText
				: OutOfSeason(category, context.Now) ? MicroGenreVerdict.OutOfSeason // 1.9 (review Minor 4)
				: !keywordsKnown && NamesUnkeptKeywords(category.Rule) ? MicroGenreVerdict.WaitsForKeywords
				: match.Count == 0 ? MicroGenreVerdict.MatchesNone
				: match.Unwatched < Floor ? MicroGenreVerdict.UnderFloor
				: match.Unwatched > ceiling ? MicroGenreVerdict.OverCeiling
				: MicroGenreVerdict.Made;
			pools.Add(new MicroGenrePool(category.Id, category.Kind, match.Count, match.Unwatched, verdict));
			if (verdict != MicroGenreVerdict.Made)
			{
				continue;
			}

			// 1.9 (the pre-release fix C): what the user hasn't started first, then what they started, then what they finished (a
			// show by its played episodes, as More like reads them), each part best first. The unwatched first by Played alone put
			// a show with 15 of 16 episodes watched first in This week's category, its tile saying "none watched"
			var (fresh, started, finished) = Watched(match.Titles, context, plays);
			List<IndexEntry> ordered = [.. Best.Of(fresh, context.Quality), .. Best.Of(started, context.Quality), .. Best.Of(finished, context.Quality)];
			var count = ReasonCount.Titles(ordered.Count, ReasonCount.OnlyOf(ordered), fresh.Count); // "41 shows, 38 unwatched": a started one isn't
			// 1.9 (stream text): one matched from summaries says so first ("From each title's summary"), then its facts
			var reason = category.Rule.WaitsForText ? ReasonLine.Of(TextPass.ReasonFact, category.Reason, count) : ReasonLine.Of(category.Reason, count);
			// 1.9 (Phase 3, R2): one holding titles that were played names two of them, the latest played first ("With “Cowboy
			// Bebop” and “Trigun” · 43 shows, 38 unwatched"), beside the one fact that says where it comes from: two facts, as every
			// reason line. The Phase 3 review: a category matched from summaries keeps "From each title's summary" first, so a
			// narrow tile (two lines of facts) still says it; the catalogue's reason goes after the names
			if (ReasonFacts.With(PlayedFirst(started, finished, context, plays)) is { Length: > 0 } with)
			{
				reason = category.Rule.WaitsForText ? ReasonLine.Of(TextPass.ReasonFact, with, count) : ReasonLine.Of(with, category.Reason, count);
			}
			made.Add(Make.Of(context, category.CollectionId, Kind, category.Name, reason, hasOwnOrder: false, ordered));
		}

		var census = new MicroGenreCensus
		{
			Rules = catalogue.Categories.Count,
			FilmRules = catalogue.Categories.Count(x => x.Kind == IndexKind.Movie),
			ShowRules = catalogue.Categories.Count(x => x.Kind == IndexKind.Series),
			Films = films,
			Shows = shows,
			FilmCeiling = filmCeiling,
			ShowCeiling = showCeiling,
			WaitForText = pools.Count(x => x.Verdict == MicroGenreVerdict.WaitsForText),
			WaitForKeywords = pools.Count(x => x.Verdict == MicroGenreVerdict.WaitsForKeywords), // 1.9 (stream wire)
			OutOfSeason = pools.Count(x => x.Verdict == MicroGenreVerdict.OutOfSeason), // 1.9 (review Minor 4)
			MatchNone = pools.Count(x => x.Verdict == MicroGenreVerdict.MatchesNone),
			UnderFloor = pools.Count(x => x.Verdict == MicroGenreVerdict.UnderFloor),
			OverCeiling = pools.Count(x => x.Verdict == MicroGenreVerdict.OverCeiling),
			Made = made.Count,
			Pools = pools,
			FilmLengths = matcher.LengthShare(IndexKind.Movie), // 1.9 (fix B)
			ShowLengths = matcher.LengthShare(IndexKind.Series),
		};
		census = WithKeywordTally(census, context, matcher);
		context.Gates.Count(Kind, census.Rules, census.PastFloor, census.Made);
		context.Gates.Note(Kind, census.Note);
		return new MicroGenreRun(made, census);
	}

	// 1.9 (the pre-release fix C): a category's titles as the user watched them: not started; started (a film begun, a show with
	// an episode played, any copy's: AnchorFinder.Started) but not finished; finished (played, or a show at 90 % of its
	// episodes: AnchorFinder.Finished, the anchors' and the slots' one rule)
	private static (List<IndexEntry> Fresh, List<IndexEntry> Started, List<IndexEntry> Finished) Watched(IReadOnlyList<IndexEntry> titles, BuildContext context, Anchors.IAnchorSource plays)
	{
		var (fresh, started, finished) = (new List<IndexEntry>(titles.Count), new List<IndexEntry>(), new List<IndexEntry>());
		var episodes = context.Index.SeriesPlays;
		foreach (var title in titles)
		{
			(Anchors.AnchorFinder.Finished(title, context.Titles, episodes) ? finished
				: Anchors.AnchorFinder.Started(title, context.Titles, episodes, plays) ? started
				: fresh).Add(title);
		}

		return (fresh, started, finished);
	}

	// 1.9 (Phase 3, R2): the names of a category's played titles (Anchors.AnchoredSlots.PlayOf: a show with an episode played, a
	// film played to its end; not a film only begun), the latest played first, then those with no date by name: what its reason
	// names (ReasonFacts.With takes two)
	private static IEnumerable<string> PlayedFirst(List<IndexEntry> started, List<IndexEntry> finished, BuildContext context, Anchors.IAnchorSource plays) =>
		started.Concat(finished)
			.Select(x => (Title: x, Play: Anchors.AnchoredSlots.PlayOf(x, context.Titles, context.Index.SeriesPlays, plays.PlaysOf(x).Last)))
			.Where(x => x.Play.Played)
			.OrderBy(x => x.Play.Last is null).ThenByDescending(x => x.Play.Last ?? DateTimeOffset.MinValue)
			.ThenBy(x => x.Title.OrderName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Title.Id)
			.Select(x => x.Title.Name);

	// 1.9 (stream wire): a rule that names a keyword an index of revision 3 didn't keep: beyond the allowlist and (the review's
	// Minor 4) the six seasonal ones 1.8.1 kept; Thanksgiving's came with revision 4. Its pool there is unknown, not empty: a
	// keywordsNone of one would let every title in
	private static bool NamesUnkeptKeywords(MicroGenreRule rule) =>
		rule.KeywordsAny.Any(x => !Keywords.KeptByRevision3(x)) || rule.KeywordsAll.Any(x => !Keywords.KeptByRevision3(x))
		|| rule.KeywordsNone.Any(x => !Keywords.KeptByRevision3(x));

	// 1.9 (the review of wip/190-wire, Minor 4): a holiday category out of its season (by the keywords its titles must have)
	private static bool OutOfSeason(MicroGenre category, DateTimeOffset now) =>
		string.Equals(category.Family, SeasonalFamily, StringComparison.OrdinalIgnoreCase)
		&& !SeasonalBuilder.InSeason([.. category.Rule.KeywordsAny, .. category.Rule.KeywordsAll], now);

	// 1.9 (stream wire): the census line's keyword tally: the matchable keywords beyond the allowlist and how many titles have
	// (the matcher's keys, no pass over the tags), and the tags kept against the server's count. Counts only (rule 10)
	private static readonly Lazy<int> s_beyondNamed = new(() => Keywords.Matchable.Count(x => !Keywords.IsNamed(x)));

	private static MicroGenreCensus WithKeywordTally(MicroGenreCensus census, BuildContext context, MicroGenreMatcher matcher)
	{
		if (!context.Index.KeywordsKnown)
		{
			return census with { KeywordsKnown = false };
		}

		var (kept, all) = (0, 0);
		foreach (var title in context.Titles.Entries)
		{
			if (title is not null)
			{
				kept += title.Tags.Length;
				all += Math.Max(title.TagCount, title.Tags.Length);
			}
		}

		return census with
		{
			MatchableKeywords = s_beyondNamed.Value,
			MatchableSeen = matcher.KeywordsSeen(x => !Keywords.IsNamed(x) && Keywords.Matchable.Contains(x.Trim().ToLowerInvariant())),
			TagsKept = kept,
			TagsAll = all,
		};
	}
}
