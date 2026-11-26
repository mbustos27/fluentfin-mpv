using System.Globalization;

namespace FluentFin.Core.SmartCollections;

// Nixie 1.8 (P1-4, stream t): the shared words of a Made by Nixie tile: its title (NixieTitles), its reason line
// (ReasonLine: one or two facts, then a count, then an end date) and its award badges (CollectionBadge, from AwardFacts).
// Streams l (the lineup), a (anchors) and s (Spotlight) write to these; HANDOFF.md of wip/180-t has the API.

/// <summary>
/// Nixie 1.8 (P1-4): a Nixie tile's reason line: one or two facts, then the count, then the end date when it has one:
/// "TMDb's “The Mummy Collection” · 4 films, 2 unwatched", "Director · 8 films, none watched · until Sun 30 Nov". It
/// describes the titles, never the viewer (the report's "Labels that tied personal traits to a topic made participants
/// uneasy"); the server is "this server", never "your" (rule 11). Kept in Home's saved page (System.Text.Json).
/// </summary>
public sealed record ReasonLine
{
	/// <summary>Between the parts of the line, as the eyebrow and the count chips write it.</summary>
	public const string Separator = " · ";

	/// <summary>The facts, one or two (<see cref="Of(string, ReasonCount)"/>): "TMDb keyword “heist”", "Next: Heat (1995)".</summary>
	public IReadOnlyList<string> Facts { get; init; } = [];

	/// <summary>The count: "12 films", "5 more films, none watched", "2 of 7 films watched".</summary>
	public ReasonCount Count { get; init; } = new();

	/// <summary>The last day the collection shows (a Spotlight's Sunday, a season's end); null: no end.</summary>
	public DateOnly? Until { get; init; }

	/// <summary>"TMDb keyword “heist”" (the facts alone: the Collection page's why line, under its own count).</summary>
	public string FactsText => string.Join(Separator, Facts.Where(x => !string.IsNullOrWhiteSpace(x)));

	/// <summary>"until Sun 30 Nov" ("" with no end): absolute, so a saved page shown a week later is never wrong.</summary>
	public string UntilText => Until is { } day ? $"until {UntilDay(day)}" : "";

	/// <summary>The line as the tile shows it: the facts, the count, the end date, each once, joined by " · ".</summary>
	public string Text => string.Join(Separator, new[] { FactsText, CountText }.Where(x => x.Length > 0));

	/// <summary>"8 films, none watched · until Sun 29 Nov": the count and the end date (a narrow tile's own line, never cut off).</summary>
	public string CountText => string.Join(Separator, new[] { Count.Text, UntilText }.Where(x => x.Length > 0));

	/// <summary>The line as Narrator hears it: its parts joined by commas.</summary>
	public string Spoken => Text.Replace(Separator, ", ", StringComparison.Ordinal);

	/// <summary>One fact and the count.</summary>
	public static ReasonLine Of(string fact, ReasonCount count) => new() { Facts = [fact], Count = count };

	/// <summary>Two facts and the count.</summary>
	public static ReasonLine Of(string fact, string second, ReasonCount count) => new() { Facts = [fact, second], Count = count };

	/// <summary>"Sun 30 Nov" in en-GB, "Sun Nov 30" in en-US: the culture's day and month-day, the month short.</summary>
	public static string UntilDay(DateOnly day)
	{
		var culture = CultureInfo.CurrentCulture;
		var monthDay = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal);
		return $"{day.ToString("ddd", culture)} {day.ToString(monthDay, culture)}";
	}
}

/// <summary>How a <see cref="ReasonCount"/> is said.</summary>
public enum ReasonCountForm
{
	/// <summary>"12 films", "12 films, 3 unwatched".</summary>
	Titles,

	/// <summary>"5 more films, none watched" (an anchored person's other titles).</summary>
	More,

	/// <summary>"2 of 7 films watched" (a franchise the user has started).</summary>
	Watched,
}

/// <summary>
/// Nixie 1.8 (P1-4): the count every reason line ends its facts with. Make one with <see cref="Titles"/>, <see cref="More"/>
/// or <see cref="Watched"/>, or <see cref="Of(IReadOnlyCollection{IndexEntry})"/> from the titles themselves.
/// </summary>
public sealed record ReasonCount
{
	public ReasonCountForm Form { get; init; }

	/// <summary>Titles (and <see cref="ReasonCountForm.More"/>'s others); <see cref="ReasonCountForm.Watched"/>: those watched.</summary>
	public int Count { get; init; }

	/// <summary><see cref="ReasonCountForm.Watched"/>: the whole ("2 of 7").</summary>
	public int Whole { get; init; }

	/// <summary>Of <see cref="Count"/>, those not watched; null: not said.</summary>
	public int? Unwatched { get; init; }

	/// <summary>Films only, shows only, or null: titles.</summary>
	public IndexKind? Only { get; init; }

	/// <summary>"12 films, 3 unwatched", "5 more films, none watched", "2 of 7 films watched".</summary>
	public string Text => Form == ReasonCountForm.Watched ? $"{Main} {WatchedPart}" : WatchedPart.Length > 0 ? $"{Main}, {WatchedPart}" : Main;

	/// <summary>1.8: the count alone: "12 films", "5 more films", "2 of 7 films" (a narrow tile's first count line).</summary>
	public string Main => Form switch
	{
		ReasonCountForm.More => $"{N(Count)} more {Noun(Count)}",
		ReasonCountForm.Watched => $"{N(Count)} of {N(Whole)} {Noun(Whole)}",
		_ => $"{N(Count)} {Noun(Count)}",
	};

	/// <summary>
	/// 1.8: how many are watched, "" when not said: "3 unwatched", "none watched", "all watched"; "watched" of
	/// <see cref="ReasonCountForm.Watched"/> (a narrow tile's second count line: "1,204 titles, 1,150 unwatched" is wider
	/// than a 168 px tile).
	/// </summary>
	public string WatchedPart => Form == ReasonCountForm.Watched ? "watched" : Unwatched switch
	{
		null => "",
		var u when u >= Count => Count == 1 ? "not watched" : "none watched",
		<= 0 => Count == 1 ? "watched" : "all watched",
		var u => $"{N(u.Value)} unwatched",
	};

	private string Noun(int count) => Only switch
	{
		IndexKind.Movie => count == 1 ? "film" : "films",
		IndexKind.Series => count == 1 ? "show" : "shows",
		_ => count == 1 ? "title" : "titles",
	};

	private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

	/// <summary>"12 films" (<paramref name="unwatched"/> given: "12 films, 3 unwatched").</summary>
	public static ReasonCount Titles(int count, IndexKind? only, int? unwatched = null) => new() { Form = ReasonCountForm.Titles, Count = count, Only = only, Unwatched = unwatched };

	/// <summary>"5 more films" (<paramref name="unwatched"/> given: "5 more films, none watched").</summary>
	public static ReasonCount More(int count, IndexKind? only, int? unwatched = null) => new() { Form = ReasonCountForm.More, Count = count, Only = only, Unwatched = unwatched };

	/// <summary>"2 of 7 films watched".</summary>
	public static ReasonCount Watched(int watched, int of, IndexKind? only) => new() { Form = ReasonCountForm.Watched, Count = watched, Whole = of, Only = only };

	/// <summary>The titles' count and how many are not watched: "12 films, 3 unwatched" (a show: not every episode played).</summary>
	public static ReasonCount Of(IReadOnlyCollection<IndexEntry> titles) => Titles(titles.Count, OnlyOf(titles), titles.Count(x => !x.Played));

	/// <summary>The titles' count alone ("20 films"): a collection of unwatched titles only, whose title says so.</summary>
	public static ReasonCount OfAll(IReadOnlyCollection<IndexEntry> titles) => Titles(titles.Count, OnlyOf(titles));

	/// <summary>Films only, shows only, or null (both, or none).</summary>
	public static IndexKind? OnlyOf(IEnumerable<IndexEntry> titles)
	{
		IndexKind? only = null;
		foreach (var title in titles)
		{
			if (only is null)
			{
				only = title.Kind;
			}
			else if (only != title.Kind)
			{
				return null;
			}
		}

		return only;
	}
}

/// <summary>
/// Nixie 1.8 (P1-4): a badge on a Nixie tile, an award fact of its titles: "3 Best Picture winners", "Palme d'Or winner"
/// (<see cref="AwardFacts.BadgesFor"/>). At most <see cref="AwardFacts.MostBadges"/> a tile.
/// </summary>
/// <param name="Text">What the badge says, and Narrator after the reason line.</param>
/// <param name="Award">The award, short ("Best Picture"): a collection of that award shows no badge of it.</param>
/// <param name="Won">Winners; false: nominees.</param>
/// <param name="Titles">How many of the collection's titles it counts.</param>
public sealed record CollectionBadge(string Text, string Award, bool Won, int Titles);

/// <summary>
/// Nixie 1.8 (P1-4): the titles of every Made by Nixie collection, in one place, so the lineup, the anchors and Spotlight say
/// them the same way. A title front-loads its hook (the name comes first, or after two short words at most), never starts
/// with "Your" and says nothing about the viewer (<see cref="Check"/>).
/// </summary>
public static class NixieTitles
{
	/// <summary>"Next in Mission: Impossible": a franchise the user has started (stream a).</summary>
	public static string NextIn(string franchise) => $"Next in {franchise.Trim()}";

	/// <summary>
	/// 1.8.1 (BH-180-q3): "Next after The Frontier and the Mirror": a franchise the user has started whose name isn't known
	/// (no TMDb name, and its films' names share no words), named by its first film.
	/// </summary>
	public static string NextAfter(string firstFilm) => $"Next after {firstFilm.Trim()}";

	/// <summary>"More like Heat": titles like one the user finished (stream a; the report's "Because you watched", said of the film).</summary>
	public static string MoreLike(string title) => $"More like {title.Trim()}";

	/// <summary>"More from Denis Villeneuve": a person of a title the user finished (stream a).</summary>
	public static string MoreFrom(string person) => $"More from {person.Trim()}";

	/// <summary>"Spotlight: Jean-Pierre Melville": the week's person or studio (stream s).</summary>
	public static string Spotlight(string name) => $"Spotlight: {name.Trim()}";

	// the words a title may not start with (the report: "never start with 'Your'"), and those that speak of the viewer
	private static readonly string[] s_notFirst = ["your", "you", "my", "for you"];
	private static readonly string[] s_viewer = ["you", "your", "you're", "you've", "yours"];

	/// <summary>
	/// Why <paramref name="title"/> breaks the title rules, or null when it keeps them: not empty, never "Your…", nothing about
	/// the viewer ("you", "your"). Tests run it over every builder's titles.
	/// </summary>
	public static string? Check(string? title)
	{
		if (string.IsNullOrWhiteSpace(title))
		{
			return "empty";
		}

		var words = title.Split([' ', ',', ':', '·', '“', '”', '"'], StringSplitOptions.RemoveEmptyEntries);
		if (s_notFirst.Any(x => title.TrimStart().StartsWith(x + " ", StringComparison.OrdinalIgnoreCase)))
		{
			return $"starts with “{words[0]}”";
		}

		return words.FirstOrDefault(w => s_viewer.Contains(w, StringComparer.OrdinalIgnoreCase)) is { } viewer ? $"speaks of the viewer (“{viewer}”)" : null;
	}
}

/// <summary>
/// Nixie 1.8 (P1-4): facts a reason line says of an anchor, in one form for every anchored slot (stream a): "You finished
/// Arrival", "You watched Arrival twice", "A favourite: Arrival". A reason line may say what the user did with a title (a
/// fact); a title never does (<see cref="NixieTitles.Check"/>).
/// </summary>
public static class ReasonFacts
{
	/// <summary>"Arrival (2016)" (no year: "Arrival").</summary>
	public static string Named(string name, int? year) => year is { } y ? $"{name.Trim()} ({y.ToString(CultureInfo.InvariantCulture)})" : name.Trim();

	/// <summary>"You finished Arrival".</summary>
	public static string Finished(string name) => $"You finished {name.Trim()}";

	/// <summary>
	/// 1.8.1 (the owner's library, fix 3): "You watched 36 of 39 episodes of What We Do in the Shadows": a show the anchors count
	/// finished (90 % of its episodes) with episodes left, which "You finished" misstated.
	/// </summary>
	public static string WatchedEpisodes(string name, int played, int of) =>
		$"You watched {played.ToString("N0", CultureInfo.CurrentCulture)} of {of.ToString("N0", CultureInfo.CurrentCulture)} episodes of {name.Trim()}";

	/// <summary>"You watched Arrival twice".</summary>
	public static string Rewatched(string name) => $"You watched {name.Trim()} twice";

	/// <summary>"A favourite: Arrival".</summary>
	public static string Favourite(string name) => $"A favourite: {name.Trim()}";

	/// <summary>"Next: Mission: Impossible III (2006)".</summary>
	public static string Next(string name, int? year) => $"Next: {Named(name, year)}";
}

/// <summary>
/// Nixie 1.8 (P1-4): what a Made by Nixie tile and a Home row show, for the templates' x:Bind (pure: tested in Core). The
/// Collections page's tile (any theme): the title, then the reason's facts (two lines at most), then its count line (never
/// cut off), and up to two award badges over the art; a server's box set keeps its own line ("6 films · 3 left"). Home's
/// rows: the reason line and the badges beside the "Made by Nixie" kicker.
/// </summary>
public static class NixieTileWords
{
	/// <summary>
	/// The tile's Narrator name: a Nixie collection's <see cref="SmartCollection.TileAutomationName"/>; a saved filter's name, its
	/// library and what it keeps ("Mine, saved filter, Movies, Comedy, Unwatched": it is the user's, and its titles are counted
	/// only when it opens); else the tile's own.
	/// </summary>
	public static string TileName(string automationName, SmartCollection? nixie) => nixie switch
	{
		null => automationName,
		{ Filter: { } filter } => $"{nixie.Title}, saved filter, {FilterFacts(filter).Replace(ReasonLine.Separator, ", ", StringComparison.Ordinal)}",
		_ => nixie.TileAutomationName,
	};

	/// <summary>
	/// The tile's second line: a Nixie collection's facts ("" before 1.8's reason lines); a saved filter's library and what it
	/// keeps ("Movies · Comedy · Unwatched"); else the server's detail.
	/// </summary>
	public static string TileFacts(string detail, SmartCollection? nixie) => nixie switch
	{
		null => detail,
		{ Filter: { } filter } => FilterFacts(filter),
		_ => nixie.Reason?.FactsText ?? "",
	};

	// a saved filter's library, then what it keeps
	private static string FilterFacts(SavedFilter filter) =>
		string.IsNullOrWhiteSpace(filter.LibraryName) ? filter.Describe() : $"{filter.LibraryName.Trim()}{ReasonLine.Separator}{filter.Describe()}";

	/// <summary>
	/// The tile's count lines, each part on a line of its own: the count, how many are watched, the end date ("1,204
	/// titles", "1,150 unwatched", "until Sun 29 Nov": a 168 px tile fits about 26 characters a line at 100 % text, 17 at
	/// 150 %, and no part may be the one cut off); the count alone before 1.8; "" on a server's. A saved filter's: "" (its
	/// titles are counted when it opens; 1.7 said "0 titles" for every one).
	/// </summary>
	public static string TileCount(SmartCollection? nixie) => nixie is null || nixie.Filter is not null ? "" : nixie.Reason is { } reason
		? string.Join("\n", new[] { reason.Count.Main, reason.Count.WatchedPart, reason.UntilText }.Where(x => x.Length > 0))
		: nixie.CountText;

	/// <summary>The count lines a tile keeps room for (<see cref="TileCount"/>: the count, the watched part, the end date).</summary>
	public const int TileCountLines = 3;

	/// <summary>
	/// The height of a tile's words at Windows' text size <paramref name="textScale"/> (1 = 100 %): a title of two lines
	/// (20 px each), facts of two and <see cref="TileCountLines"/> count lines (16 px each) grow with the text; the 4 px
	/// between them don't. Every tile of a row and of See all's grid is this tall, so none is clipped at 125 % or 150 % and a
	/// reason that comes later moves nothing (rule 7). 124 at 100 %.
	/// </summary>
	public static double WordsHeight(double textScale) => Math.Ceiling((2 * 20 + (2 + TileCountLines) * 16) * Math.Max(1, textScale)) + 4;

	/// <summary>
	/// 1.8.1 (BH-180-q2): the height of a server's box set's or playlist's words (<paramref name="nixie"/> false): a title of
	/// two lines and its own line ("6 films · 3 left", two lines at most), no count lines (that line is collapsed): 74 at
	/// 100 %. They had a Nixie tile's 124, about 85 px empty under their two lines, and their rows were taller. A row holds
	/// one kind of tile (the server's sets, its playlists, or Nixie's), as See all's grid does, so each is still one height.
	/// </summary>
	public static double WordsHeight(double textScale, bool nixie) =>
		nixie ? WordsHeight(textScale) : Math.Ceiling((2 * 20 + 2 * 16) * Math.Max(1, textScale)) + 2;

	/// <summary>The tile's badge <paramref name="index"/> ("" when it has none there).</summary>
	public static string Badge(SmartCollection? nixie, int index) => nixie is not null && index >= 0 && index < nixie.Badges.Count ? nixie.Badges[index].Text : "";

	/// <summary>Whether the tile has a badge <paramref name="index"/>.</summary>
	public static bool HasBadge(SmartCollection? nixie, int index) => Badge(nixie, index).Length > 0;

	/// <summary>Home's row: the reason line, then the badges: "TMDb keyword “heist” · 5 films, none watched · Best Picture winner".</summary>
	public static string RowLine(SmartCollection? collection) =>
		collection is null ? "" : string.Join(ReasonLine.Separator, new[] { collection.ReasonText }.Concat(collection.Badges.Select(x => x.Text)).Where(x => x.Length > 0));

	/// <summary>Drive-in's and Editorial's one line beside the row's title: "Made by Nixie · " and <see cref="RowLine"/>.</summary>
	public static string RowSubtitle(string kicker, SmartCollection? collection) =>
		RowLine(collection) is { Length: > 0 } line ? $"{kicker}{ReasonLine.Separator}{line}" : kicker;
}
