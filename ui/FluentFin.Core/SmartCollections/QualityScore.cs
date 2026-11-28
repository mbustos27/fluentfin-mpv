namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.7.3 (P0-4): how good a title is, with how much backs its rating taken into account, so a high rating that little
/// backs up doesn't lead (IMDb's weighted rating): <c>Q = k/(k+m)·R + m/(k+m)·C</c>.
/// <list type="bullet">
/// <item>R: the title's community rating (0–10); a title with none (or 0) has no score.</item>
/// <item>C: the mean rating of the titles of its kind here (films, shows apart).</item>
/// <item>k: the confidence, 0 to 4: a critics' rating, two or more copies or libraries, on an outside list (an award, a
/// genre list), and its tags before the allowlist (ten or more count whole).</item>
/// <item>m: the median k of the rated titles, at least 1: a title of median confidence gets half its own rating.</item>
/// </list>
/// The servers the owner uses keep no critics' ratings; there the other signals decide. Counts the canonical titles
/// (<see cref="CanonicalTitles"/>), so copies count once.
/// <para>
/// 1.7.3 (M1's review, Major): artwork no longer counts. Nearly every rated title has a poster, so it lifted an obscure 9.8
/// with two tags (8.65) over an 8.6 with 24 (8.28), and such titles took all 20 of Acclaimed's places. A title also needs
/// some backing to be acclaimed at all (<see cref="IsBacked"/>).
/// </para>
/// <para>
/// 1.9 (the owner's library, fix 9): brand-new titles and concert films led the rankings. A title of the 12 months around the
/// day (<see cref="IsNew"/>) and a concert film (<see cref="IsConcert"/>) each have their confidence halved: their ratings come
/// from few and keen voters (a 2026 concert film rated 10 with critics' 100 led Acclaimed, with 2026 titles in five of its
/// first eight places). And the critics' view is folded into the rating (<see cref="RatingOf"/>), not only whether there is
/// one: two 7.6s with critics' 100 and 34 tied, and the name chose. Only a title's departure from its kind's usual gap counts
/// (the review's Major 2), so a kind without critics (the shows there) isn't ranked below one with them.
/// </para>
/// </summary>
public sealed class QualityScore
{
	/// <summary>Tags a title needs for that signal to count whole (fewer count in part).</summary>
	public const int TagsWhole = 10;

	// 1.9 (fix 9)
	/// <summary>A title released this many months before the day (or up to as many after) is new: its confidence is halved.</summary>
	public const int NewMonths = 12;

	/// <summary>What the confidence of a new title, and of a concert film, is multiplied by.</summary>
	public const double Caution = 0.5;

	/// <summary>The critics' share of the rating a score reads (<see cref="RatingOf"/>); the community's is the rest.</summary>
	public const double CriticShare = 1.0 / 3;

	/// <summary>
	/// The tags that make a title a concert film on their own (lower case, as the index keeps them: the matchable keywords of
	/// the index's revision 4; an index without them has none, and no title is a concert film there).
	/// </summary>
	public static IReadOnlySet<string> ConcertTags { get; } = new HashSet<string>(StringComparer.Ordinal) { "concert film", "rock concert" };

	/// <summary>
	/// The review's item 9: the tags that make a title a concert film only with a Music or Documentary genre, as the catalogue's
	/// "Concert Films" asks: a drama about a pianist carries "concert", a stage drama "live performance".
	/// </summary>
	public static IReadOnlySet<string> ConcertTagsWithMusic { get; } = new HashSet<string>(StringComparer.Ordinal) { "concert", "live performance" };
	// end 1.9

	private readonly CanonicalTitles _titles;
	private readonly IReadOnlySet<Guid> _listed;
	private readonly Dictionary<IndexKind, double> _mean;
	private readonly Dictionary<IndexKind, double> _gap; // 1.9 (the review's Major 2): each kind's usual critics' gap
	private readonly bool _plain;
	private readonly (DateOnly After, DateOnly Until)? _new; // 1.9: the days a new title came out in (after, up to)

	private QualityScore(CanonicalTitles titles, IReadOnlySet<Guid> listed, bool plain, DateTimeOffset? now)
	{
		_titles = titles;
		_listed = listed;
		_plain = plain;
		_new = now is { } day && DateOnly.FromDateTime(day.Date) is var today && today.Year is > 1 and < 9999 ? (today.AddMonths(-NewMonths), today.AddMonths(NewMonths)) : null;
		// 1.9 (the review's Major 2): each kind's usual gap between the critics' value (read as 0 to 10) and the community's rating,
		// over its titles that have both: only a title's own departure from it is folded in
		_gap = plain ? [] : titles.Entries.Where(x => R(x) is not null && x.Critic is > 0).GroupBy(x => x.Kind)
			.ToDictionary(g => g.Key, g => g.Average(x => (Math.Min(x.Critic!.Value, 100) / 10.0) - R(x)!.Value));
		var rated = titles.Entries.Where(x => RatingOf(x) is not null).ToList();
		_mean = rated.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => g.Average(x => RatingOf(x)!.Value));
		var ks = rated.Select(Confidence).Order().ToList();
		M = Math.Max(1, ks.Count == 0 ? 1 : ks.Count % 2 == 1 ? ks[ks.Count / 2] : (ks[(ks.Count / 2) - 1] + ks[ks.Count / 2]) / 2);
	}

	/// <param name="listed">The titles (canonical ids) on any outside list; none when no outside source is on.</param>
	/// <param name="plain">
	/// 1.7.3 (BH-173-M1, m1): the index has nothing that backs a rating (<see cref="RatingBacking.Any"/>): the confidence is the
	/// copies and the outside lists only (a few tags, which every title of such a server has or lacks alike, don't count).
	/// </param>
	/// <param name="now">1.9: the day the titles are ranked on (the pages' local date): what is new (<see cref="IsNew"/>); null: nothing is.</param>
	public static QualityScore Of(CanonicalTitles titles, IReadOnlySet<Guid>? listed = null, bool plain = false, DateTimeOffset? now = null) =>
		new(titles, listed ?? new HashSet<Guid>(), plain, now);

	/// <summary>The median confidence of the rated titles, at least 1: the weight the mean gets.</summary>
	public double M { get; }

	/// <summary>The mean rating of the rated titles of a kind (C); 0 when none of that kind is rated.</summary>
	public double MeanOf(IndexKind kind) => _mean.TryGetValue(kind, out var c) ? c : 0;

	/// <summary>The title's score (Q, 0–10); null when it has no rating.</summary>
	public double? Of(IndexEntry title)
	{
		if (RatingOf(title) is not { } r)
		{
			return null;
		}

		var k = Confidence(title);
		return ((k * r) + (M * MeanOf(title.Kind))) / (k + M);
	}

	// 1.9 (the owner's library, fix 9)
	/// <summary>
	/// The rating a score reads (R): the community's, with the critics' view folded in when there is one. The review's Major 2:
	/// only how far the title's critics depart from its kind's usual gap (<see cref="GapOf"/>) counts, for
	/// <see cref="CriticShare"/>: <c>R' = r + s·((c/10 − r) − Δ̄)</c>. A critics' rating is a share of good reviews (the
	/// tomatometer), not a 0-10 score: read as one, every film with critics gained about half a point over the shows, which the
	/// servers the owner uses rate without critics (missing data ranked low, rule 4). With a kind's usual gap 0, a 7.6 with
	/// critics' 100 reads 8.4 and one with 34 reads 6.2. Not in plain mode (nothing in the index backs a rating). Null for none
	/// (0 is some servers' "none").
	/// </summary>
	public double? RatingOf(IndexEntry title) =>
		R(title) is not { } r ? null
		: title.Critic is float critic && critic > 0 && !_plain ? r + (CriticShare * ((Math.Min(critic, 100) / 10.0) - r - GapOf(title.Kind)))
		: r;

	/// <summary>
	/// 1.9 (the review's Major 2): a kind's usual gap, the mean of (critics' value / 10 − rating) over its titles that have
	/// both; 0 for a kind none of whose titles has critics.
	/// </summary>
	public double GapOf(IndexKind kind) => _gap.TryGetValue(kind, out var gap) ? gap : 0;

	/// <summary>
	/// Whether the title came out in the <see cref="NewMonths"/> months before the day, or is dated up to as many after it (the
	/// premiere date; with a year only, 1 July of it). Nothing is new for a score made without a day, nor a title with no date.
	/// </summary>
	public bool IsNew(IndexEntry title) =>
		_new is { } window && ReleaseOf(title) is { } released && released > window.After && released <= window.Until;

	/// <summary>
	/// Whether the title is a concert film: tagged one (<see cref="ConcertTags"/>), or tagged a concert or a live performance
	/// with a Music or Documentary genre (<see cref="ConcertTagsWithMusic"/>).
	/// </summary>
	public static bool IsConcert(IndexEntry title)
	{
		var music = (title.Genres ?? []).Any(x => string.Equals(x, "Music", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "Documentary", StringComparison.OrdinalIgnoreCase));
		foreach (var tag in title.Tags ?? [])
		{
			if (tag is not null && (ConcertTags.Contains(tag) || (music && ConcertTagsWithMusic.Contains(tag))))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>A title's release day: its premiere date ("2026-10-02"), else 1 July of its year; null for neither.</summary>
	public static DateOnly? ReleaseOf(IndexEntry title) =>
		title.PremiereDate is { Length: >= 10 } premiere
			&& DateOnly.TryParseExact(premiere.AsSpan(0, 10), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) ? day
		: title.Year is int year && year is >= 1 and <= 9999 ? new DateOnly(year, 7, 1)
		: null;
	// end 1.9

	/// <summary>Tags (before the allowlist) that back a title on their own (<see cref="IsBacked"/>).</summary>
	public const int TagsBacking = 5;

	/// <summary>
	/// 1.7.3 (M1's review): whether something beyond its own rating backs the title: a critics' rating, <see cref="TagsBacking"/>
	/// or more tags, or an outside list. "Acclaimed, not seen yet" takes only these.
	/// </summary>
	public bool IsBacked(IndexEntry title) => title.Critic is > 0 || title.TagCount >= TagsBacking || _listed.Contains(title.Id);

	/// <summary>
	/// How much backs the title's rating (k, 0–4). 1.9 (fix 9): halved for a new title (<see cref="IsNew"/>), and again for a
	/// concert film (<see cref="IsConcert"/>).
	/// </summary>
	public double Confidence(IndexEntry title)
	{
		var k = Backing(title);
		if (k > 0 && IsNew(title))
		{
			k *= Caution;
		}

		if (k > 0 && IsConcert(title))
		{
			k *= Caution;
		}

		return k;
	}

	// what backs the rating (1.7.3's k)
	private double Backing(IndexEntry title)
	{
		var k = 0.0;
		if (title.Critic is > 0 && !_plain)
		{
			k += 1;
		}

		if (_titles.CopiesOf(title.Id) >= 2 || (title.LibraryIds ?? []).Distinct().Count() >= 2)
		{
			k += 1;
		}

		if (_listed.Contains(title.Id))
		{
			k += 1;
		}

		if (!_plain)
		{
			k += Math.Min(Math.Max(title.TagCount, 0), TagsWhole) / (double)TagsWhole;
		}

		return k; // 1.7.3 (M1's review): not the artwork any more
	}

	// the community rating; 0 (some servers' "none") is no rating
	private static double? R(IndexEntry title) => title.Rating is > 0 and var r ? r : null;
}

/// <summary>
/// Nixie 1.7.3 fix round (BH-173-M1, BH-173-m1): what in an index can back a rating, counted over its titles: a critics' rating,
/// <see cref="QualityScore.TagsBacking"/> or more tags, an outside list. Acclaimed takes only backed titles when the index has
/// any title with a critics' rating or that many tags (<see cref="Any"/>); otherwise the rating alone ranks (rule 4: missing
/// data is not a decision). That is so on a server whose titles keep neither (the throwaway's 25,736 films), and for an index
/// saved before 1.7.3 (<see cref="IndexSnapshot.BackingKnown"/>: it never kept them, and every title read as "nothing backs it").
/// Counts only: the census line says them, never a title.
/// </summary>
public sealed record RatingBacking(bool Known, int Critic, int Tags, int Listed)
{
	/// <summary>Something in the index can back a rating: the backing gate applies.</summary>
	public bool Any => Known && (Critic > 0 || Tags > 0);

	/// <summary>The census's words for it: the mode, why, and the counts.</summary>
	public string Census => string.Create(System.Globalization.CultureInfo.InvariantCulture,
		$"{(Any ? "backed" : Known ? "rating only, no title has a critics' rating or 5+ tags" : "rating only, an index saved before 1.7.3")}: critic {Critic}, tags5+ {Tags}, listed {Listed}");

	/// <summary>The counts over <paramref name="titles"/>; <paramref name="known"/>: the index keeps critics' ratings and tag counts.</summary>
	public static RatingBacking Of(IReadOnlyList<IndexEntry> titles, IReadOnlySet<Guid> listed, bool known) =>
		new(known, titles.Count(x => x.Critic is > 0), titles.Count(x => x.TagCount >= QualityScore.TagsBacking), titles.Count(x => listed.Contains(x.Id)));
}
