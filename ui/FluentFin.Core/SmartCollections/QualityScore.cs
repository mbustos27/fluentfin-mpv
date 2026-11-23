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
/// </summary>
public sealed class QualityScore
{
	/// <summary>Tags a title needs for that signal to count whole (fewer count in part).</summary>
	public const int TagsWhole = 10;

	private readonly CanonicalTitles _titles;
	private readonly IReadOnlySet<Guid> _listed;
	private readonly Dictionary<IndexKind, double> _mean;
	private readonly bool _plain;

	private QualityScore(CanonicalTitles titles, IReadOnlySet<Guid> listed, bool plain)
	{
		_titles = titles;
		_listed = listed;
		_plain = plain;
		var rated = titles.Entries.Where(x => R(x) is not null).ToList();
		_mean = rated.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => g.Average(x => R(x)!.Value));
		var ks = rated.Select(Confidence).Order().ToList();
		M = Math.Max(1, ks.Count == 0 ? 1 : ks.Count % 2 == 1 ? ks[ks.Count / 2] : (ks[(ks.Count / 2) - 1] + ks[ks.Count / 2]) / 2);
	}

	/// <param name="listed">The titles (canonical ids) on any outside list; none when no outside source is on.</param>
	/// <param name="plain">
	/// 1.7.3 (BH-173-M1, m1): the index has nothing that backs a rating (<see cref="RatingBacking.Any"/>): the confidence is the
	/// copies and the outside lists only (a few tags, which every title of such a server has or lacks alike, don't count).
	/// </param>
	public static QualityScore Of(CanonicalTitles titles, IReadOnlySet<Guid>? listed = null, bool plain = false) => new(titles, listed ?? new HashSet<Guid>(), plain);

	/// <summary>The median confidence of the rated titles, at least 1: the weight the mean gets.</summary>
	public double M { get; }

	/// <summary>The mean rating of the rated titles of a kind (C); 0 when none of that kind is rated.</summary>
	public double MeanOf(IndexKind kind) => _mean.TryGetValue(kind, out var c) ? c : 0;

	/// <summary>The title's score (Q, 0–10); null when it has no rating.</summary>
	public double? Of(IndexEntry title)
	{
		if (R(title) is not { } r)
		{
			return null;
		}

		var k = Confidence(title);
		return ((k * r) + (M * MeanOf(title.Kind))) / (k + M);
	}

	/// <summary>Tags (before the allowlist) that back a title on their own (<see cref="IsBacked"/>).</summary>
	public const int TagsBacking = 5;

	/// <summary>
	/// 1.7.3 (M1's review): whether something beyond its own rating backs the title: a critics' rating, <see cref="TagsBacking"/>
	/// or more tags, or an outside list. "Acclaimed, not seen yet" takes only these.
	/// </summary>
	public bool IsBacked(IndexEntry title) => title.Critic is > 0 || title.TagCount >= TagsBacking || _listed.Contains(title.Id);

	/// <summary>How much backs the title's rating (k, 0–4).</summary>
	public double Confidence(IndexEntry title)
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
