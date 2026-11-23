using System.Globalization;

namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>What every builder reads: the index, the date (the seasonal row, "a month ago") and the franchises the admin boxed.</summary>
/// <param name="BoxedFranchises">TMDb collection ids an admin's box set already has (the franchise shows once, as the box set).</param>
public sealed record BuildContext(IndexSnapshot Index, DateTimeOffset Now, IReadOnlySet<string> BoxedFranchises)
{
	// 1.7 keyless
	/// <summary>1.7: the outside lists the builders may use (TMDb's names, Wikidata's lists); empty when every source is off.</summary>
	public global::FluentFin.Core.Outside.OutsideListsSnapshot Outside { get; init; } = global::FluentFin.Core.Outside.OutsideListsSnapshot.Empty;
	// end 1.7 keyless

	public static BuildContext For(IndexSnapshot index, DateTimeOffset now) => new(index, now, new HashSet<string>());

	// 1.7.3 (P0-1, P0-2b, P0-4): what the builders count (the titles, copies collapsed), the score they rank by, and what they
	// say they counted. Made once a context, at the first builder that asks; made again if the index is swapped (`with`)
	private (IndexSnapshot For, CanonicalTitles Titles)? _titles;
	private (CanonicalTitles For, global::FluentFin.Core.Outside.OutsideListsSnapshot Outside, global::FluentFin.Core.Outside.OutsideMatcher Matcher)? _matcher;
	private (CanonicalTitles For, global::FluentFin.Core.Outside.OutsideListsSnapshot Outside, QualityScore Score, RatingBacking Backing)? _quality;

	/// <summary>
	/// 1.7.3 fix round (BH-173-M2): no display cap. A saved filter's preset (and a pin made by its id) holds every title its rule
	/// keeps; the 20 of Acclaimed and Under 90 minutes, the 40 in season and the twelve genre mixes are what the published
	/// collections show. 1.7.2's saved filters on "Highly rated" or "Short films" kept every title, and filtered to 20 they
	/// changed meaning ("Comedy + Short films": 100 titles, then 0).
	/// </summary>
	public bool Whole { get; init; }

	/// <summary>
	/// 1.7.3 (P0-2b): the index's titles with copies collapsed (<see cref="CanonicalTitles"/>): every builder counts these, so a
	/// film in three files counts once. Should collapsing fail, each entry is its own title (a kind is never lost to it).
	/// </summary>
	public CanonicalTitles Titles
	{
		get
		{
			if (_titles is { } made && ReferenceEquals(made.For, Index))
			{
				return made.Titles;
			}

			CanonicalTitles titles;
			try
			{
				titles = CanonicalTitles.Of(Index.Entries);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				titles = CanonicalTitles.Each(Index.Entries);
			}

			_titles = (Index, titles);
			return titles;
		}
	}

	/// <summary>1.7.3: the outside lists' matcher over <see cref="Titles"/> (a list's title matches the title, not one of its copies).</summary>
	public global::FluentFin.Core.Outside.OutsideMatcher Matcher
	{
		get
		{
			var titles = Titles;
			if (_matcher is { } made && ReferenceEquals(made.For, titles) && ReferenceEquals(made.Outside, Outside))
			{
				return made.Matcher;
			}

			var matcher = new global::FluentFin.Core.Outside.OutsideMatcher(Index with { Entries = titles.Entries });
			_matcher = (titles, Outside, matcher);
			return matcher;
		}
	}

	/// <summary>
	/// 1.7.3 (P0-4): the titles' scores (<see cref="QualityScore"/>), an outside list's titles counted as better known. Fix round
	/// (BH-173-M1, m1): with nothing in the index that backs a rating (<see cref="Backing"/>), the copies and the lists only.
	/// </summary>
	public QualityScore Quality => Scored().Score;

	/// <summary>1.7.3 fix round (BH-173-M1, m1): what in the index can back a rating, and so whether Acclaimed takes only backed titles.</summary>
	public RatingBacking Backing => Scored().Backing;

	private (QualityScore Score, RatingBacking Backing) Scored()
	{
		var titles = Titles;
		if (_quality is { } made && ReferenceEquals(made.For, titles) && ReferenceEquals(made.Outside, Outside))
		{
			return (made.Score, made.Backing);
		}

		var listed = new HashSet<Guid>();
		if (Outside.Lists.Count > 0)
		{
			var matcher = Matcher;
			foreach (var list in Outside.Lists)
			{
				listed.UnionWith(matcher.MatchAll(list.Items.Select(x => x.Ref)).Select(x => x.Id));
			}
		}

		var backing = RatingBacking.Of(titles.Entries, listed, Index.BackingKnown);
		var score = QualityScore.Of(titles, listed, plain: !backing.Any);
		_quality = (titles, Outside, score, backing);
		return (score, backing);
	}

	/// <summary>1.7.3 (P0-1): what each builder counted: its candidates, those past its floor, those past its cap.</summary>
	public BuildGates Gates { get; init; } = new();
}

/// <summary>
/// Nixie 1.7.3 (P0-1): the gate counter: each builder says how many candidates it had, how many passed its size floor and how
/// many passed its cap. For a kind of many collections (franchises, keywords) these are collections; for a kind of one
/// (Acclaimed, Under 90 minutes) titles. Counts only: the census line says them, never a title.
/// </summary>
public sealed class BuildGates
{
	private readonly Dictionary<SmartCollectionKind, Gate> _counts = [];
	private readonly Lock _lock = new();

	/// <summary>One kind's counts.</summary>
	public readonly record struct Gate(int Candidates, int Floor, int Cap);

	/// <summary>Says a kind's counts (a second call for the kind replaces the first).</summary>
	public void Count(SmartCollectionKind kind, int candidates, int floor, int cap)
	{
		lock (_lock)
		{
			_counts[kind] = new Gate(candidates, floor, cap);
		}
	}

	/// <summary>A kind's counts; null when its builder said none.</summary>
	public Gate? Of(SmartCollectionKind kind)
	{
		lock (_lock)
		{
			return _counts.TryGetValue(kind, out var gate) ? gate : null;
		}
	}

	// 1.7.3 fix round (BH-173-m1): a kind's mode and why, beside its counts (Acclaimed: backed, or the rating alone)
	private readonly Dictionary<SmartCollectionKind, string> _notes = [];

	/// <summary>Says how a kind was made (counts and words only, never a title); the census line adds it to the kind's counts.</summary>
	public void Note(SmartCollectionKind kind, string note)
	{
		lock (_lock)
		{
			_notes[kind] = note;
		}
	}

	/// <summary>A kind's note; null when its builder said none.</summary>
	public string? NoteOf(SmartCollectionKind kind)
	{
		lock (_lock)
		{
			return _notes.TryGetValue(kind, out var note) ? note : null;
		}
	}
}

/// <summary>Nixie 1.6: one kind of Nixie collection (the plan's 1.3), built from the index alone. Thresholds are the plan's first guesses.</summary>
public interface ISmartCollectionBuilder
{
	SmartCollectionKind Kind { get; }

	IEnumerable<SmartCollection> Build(BuildContext context);
}

/// <summary>Helpers the builders share.</summary>
internal static class Make
{
	/// <summary>A collection of these titles, in this order, with its cover (the first four with a poster).</summary>
	public static SmartCollection Of(string id, SmartCollectionKind kind, string title, string why, bool hasOwnOrder, IReadOnlyList<IndexEntry> items) =>
		new(id, kind, title, why, hasOwnOrder, [.. items.Select(x => x.Id)])
		{
			Cover = [.. items.Where(x => x.PrimaryTag is not null).Concat(items.Where(x => x.PrimaryTag is null)).Take(4)],
			Only = items.Select(x => x.Kind).Distinct().Count() == 1 ? items[0].Kind : null,
		};

	/// <summary>By name, the way a library lists them.</summary>
	public static List<IndexEntry> ByName(IEnumerable<IndexEntry> items) =>
		[.. items.OrderBy(x => x.OrderName, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Year)];

	/// <summary>Oldest first: release order.</summary>
	public static List<IndexEntry> ByRelease(IEnumerable<IndexEntry> items) =>
		[.. items.OrderBy(x => x.PremiereDate ?? (x.Year is { } y ? $"{y:D4}" : "9999")).ThenBy(x => x.OrderName, StringComparer.CurrentCultureIgnoreCase)];

	/// <summary>A stable id part from a name: lower case, letters and digits only ("Walt Disney Pictures" → "waltdisneypictures").</summary>
	public static string Key(string name) => new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

	public static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

	public static string Noun(IndexKind kind, int count) => kind == IndexKind.Movie ? count == 1 ? "film" : "films" : count == 1 ? "show" : "shows";
}
