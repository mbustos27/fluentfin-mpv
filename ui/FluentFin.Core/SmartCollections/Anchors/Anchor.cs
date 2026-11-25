using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>Nixie 1.8 (P1-2): why a title anchors the personal slots (a title can have several reasons).</summary>
[Flags]
public enum AnchorWhy
{
	None = 0,

	/// <summary>A film played to the end (the server marks a film played at 90 % by default), or 90 % of a show's episodes.</summary>
	Finished = 1,

	/// <summary>
	/// Finished, and started twice or more. Review (Major): the server adds one to a title's play count at every start, not
	/// every finish, so the count can't say "watched twice" (two sittings of one viewing count two); it never names a count.
	/// </summary>
	Rewatched = 2,

	/// <summary>A favourite.</summary>
	Favourite = 4,
}

/// <summary>
/// Nixie 1.8 (P1-2): a title the user finished (and maybe watched again) or made a favourite: a film, a show or an anime
/// alike. The personal slots ("Next in", "More like", "More from") hang on these, and say which one.
/// </summary>
/// <param name="When">When it was last played; null when not known (a favourite never played, an index without play dates).</param>
public sealed record Anchor(IndexEntry Title, AnchorWhy Why, DateTimeOffset? When, int Plays)
{
	/// <summary>
	/// The reason's fact (stream t's <see cref="ReasonFacts"/>): "You finished Arrival", "A favourite: Arrival". Review (Major):
	/// never "You watched Heat twice": the play count counts starts (<see cref="AnchorWhy.Rewatched"/>).
	/// </summary>
	public string Said => Why.HasFlag(AnchorWhy.Finished) ? ReasonFacts.Finished(Title.Name) : ReasonFacts.Favourite(Title.Name);
}

/// <summary>Nixie 1.8 (P1-2): the anchors of an index.</summary>
public static class AnchorFinder
{
	/// <summary>A show is finished when this share of its episodes is played.</summary>
	public const double FinishedShare = 0.9;

	/// <summary>The personal slots rotate among this many of the latest anchors (Jellyfin's own rows: the six latest played).</summary>
	public const int Recent = 6;

	/// <summary>
	/// Every anchor of the index's titles (copies collapsed), the latest played first; then those with no date, by name. A
	/// title is an anchor when it is finished (<see cref="Finished"/>) or a favourite; a finished one started twice or more is
	/// also rewatched. A title started and never finished is no anchor, however often it was started. None for an index
	/// with nothing finished or favoured.
	/// </summary>
	public static IReadOnlyList<Anchor> Of(BuildContext context, IAnchorSource source)
	{
		var titles = context.Titles;
		var plays = context.Index.SeriesPlays;
		var anchors = new List<Anchor>();
		foreach (var title in titles.Entries)
		{
			if (title is null || string.IsNullOrWhiteSpace(title.Name))
			{
				continue; // a damaged saved entry: no slot can name it
			}

			var played = source.PlaysOf(title);
			var why = Finished(title, titles, plays) ? AnchorWhy.Finished : AnchorWhy.None;
			DateTimeOffset? when = played.Last;
			if (title.Kind == IndexKind.Series && EpisodesOf(title, titles, plays) is { Played: > 0 } episodes && episodes.LastPlayed > DateTimeOffset.MinValue
				&& (when is null || episodes.LastPlayed > when))
			{
				when = episodes.LastPlayed;
			}

			// review (Major): a start counts, not a finish: a rewatch only of a title finished
			if (why.HasFlag(AnchorWhy.Finished) && played.Count >= 2)
			{
				why |= AnchorWhy.Rewatched;
			}

			if (title.Favorite)
			{
				why |= AnchorWhy.Favourite;
			}

			if (why != AnchorWhy.None)
			{
				anchors.Add(new Anchor(title, why, when, Math.Max(played.Count, title.Played ? 1 : 0)));
			}
		}

		return [.. anchors
			.OrderBy(x => x.When is null)
			.ThenByDescending(x => x.When ?? DateTimeOffset.MinValue)
			.ThenBy(x => x.Title.OrderName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => x.Title.Id)];
	}

	/// <summary>
	/// Whether the user finished the title: a film played (the server marks it played at 90 % by default); a show played, or
	/// with <see cref="FinishedShare"/> of its episodes played (any copy's, as one title). The one rule for the anchors and
	/// for what the slots count as watched (review, Minor: a show at 30 of 31 counted as unwatched in More from's floor).
	/// </summary>
	public static bool Finished(IndexEntry title, CanonicalTitles titles, IReadOnlyDictionary<Guid, SeriesPlays> plays)
	{
		if (title.Played)
		{
			return true;
		}

		return title.Kind == IndexKind.Series && EpisodesOf(title, titles, plays) is { Played: > 0 } e && title.Unplayed is { } left && left >= 0
			&& e.Played >= FinishedShare * (e.Played + left);
	}

	// a show in two libraries is one, its episodes the most of either copy's (UnfinishedSeriesBuilder's rule)
	private static SeriesPlays? EpisodesOf(IndexEntry show, CanonicalTitles titles, IReadOnlyDictionary<Guid, SeriesPlays> plays)
	{
		SeriesPlays? most = null;
		foreach (var id in titles.CopyIds(show.Id))
		{
			if (plays.TryGetValue(id, out var p) && (most is null || p.Played > most.Played))
			{
				most = p;
			}
		}

		return most ?? (plays.TryGetValue(show.Id, out var own) ? own : null);
	}
}
