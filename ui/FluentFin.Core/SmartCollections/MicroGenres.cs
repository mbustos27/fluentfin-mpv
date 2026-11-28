using System.Text.Json;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections;

// Nixie 1.9 (Collections 2, stream match): the micro-genre catalogue ("Gritty 1970s Crime Dramas"), shipped as data in
// SmartCollections\Data\micro-genres.json (embedded, as spotlight-studios.json), read and checked here. The rules are matched
// on this PC (MicroGenreMatcher) and made into collections by MicroGenreBuilder; nothing about the library leaves the PC.

/// <summary>
/// Nixie 1.9: an inclusive range of whole numbers, either end open (null): a rule's years ("1970 to 1979"), or its length in
/// minutes (a film's runtime, a show's episode length).
/// </summary>
public readonly record struct MicroRange(int? From, int? To)
{
	/// <summary>Neither end set: the range keeps everything, a title with no value too.</summary>
	public bool IsOpen => From is null && To is null;

	/// <summary>Whether <paramref name="value"/> is in the range; a title with no value is in an open range only (missing data is no match).</summary>
	public bool Contains(int? value) =>
		IsOpen || (value is { } v && (From is not { } from || v >= from) && (To is not { } to || v <= to));

	/// <summary>
	/// 1.9 (the pre-release fix B): as <see cref="Contains(int?)"/>, and a title with no value is in when
	/// <paramref name="unknownKept"/> (a rule's length where most titles of its kind have none: <see cref="MicroGenreMatcher.LengthUnknownKept"/>).
	/// </summary>
	public bool Contains(int? value, bool unknownKept) => value is null ? IsOpen || unknownKept : Contains(value);
}

/// <summary>
/// Nixie 1.9: what a micro-genre's titles have. Every field is optional; the fields AND together, and each list means what
/// its name says: <see cref="GenresAll"/> every one, <see cref="GenresAny"/> at least one, <see cref="GenresNone"/> none of
/// them. Genres, countries and keywords compare without case; studios by their letters and digits (<see cref="Make.Key"/>),
/// as the Spotlight's studios do.
/// </summary>
public sealed record MicroGenreRule
{
	public IReadOnlyList<string> GenresAll { get; init; } = [];
	public IReadOnlyList<string> GenresAny { get; init; } = [];
	public IReadOnlyList<string> GenresNone { get; init; } = [];

	/// <summary>TMDb keywords (the index's tags), lower case; only those on <see cref="MicroGenres.Matchable"/>.</summary>
	public IReadOnlyList<string> KeywordsAny { get; init; } = [];
	public IReadOnlyList<string> KeywordsAll { get; init; } = [];
	public IReadOnlyList<string> KeywordsNone { get; init; } = [];

	/// <summary>The production year, both ends included.</summary>
	public MicroRange Years { get; init; }

	/// <summary>Any of the title's production countries (Jellyfin fills them for films only).</summary>
	public IReadOnlyList<string> CountriesAny { get; init; } = [];

	/// <summary>Any of the title's studios (a film's companies, a show's networks).</summary>
	public IReadOnlyList<string> StudiosAny { get; init; } = [];

	/// <summary>
	/// A film's runtime, or a show's episode length, in whole minutes, both ends included. 1.9 (the pre-release fix B): a title
	/// with no length is no match where most titles of its kind have one, and is judged on the rule's other fields where most
	/// have none (<see cref="MicroGenreMatcher.LengthUnknownKept"/>).
	/// </summary>
	public MicroRange LengthMinutes { get; init; }

	/// <summary>The least community rating (0–10); a title with none is no match.</summary>
	public double? CommunityMin { get; init; }

	/// <summary>The least critics' rating (0–100); a title with none is no match.</summary>
	public double? CriticMin { get; init; }

	/// <summary>
	/// The words a title's summary must have one of (step 1b), as whole words, case-folded (<see cref="TextWords"/>). The index
	/// keeps no summary (<see cref="IndexEntry"/>): the summaries' pass (<see cref="TextPass"/>) reads them and keeps which
	/// categories' words each has (<see cref="TextMatches"/>). Missing data is not a decision (rule 4): a title not read, or with
	/// no summary, is in no such category, and none is made until the pass has read its kind.
	/// </summary>
	public IReadOnlyList<string> TextAny { get; init; } = [];

	/// <summary>
	/// 1.9 (the review of wip/190-text, item 2): how many different words of <see cref="TextAny"/> a summary must have (1, the
	/// default: any one). 2 keeps "office … boss", not "office" alone: a list's words that also mean something else ("bar",
	/// "town") then need a second word of the list beside them. Words that are the same words whatever joins them
	/// ("co-workers", "co workers") count once.
	/// </summary>
	public int TextMin { get; init; } = 1;

	/// <summary>
	/// The rule has a word list (<see cref="TextAny"/>): it keeps only titles the summaries' pass matched, and nothing until the
	/// pass has read its kind's titles (1.9, stream text; the name from before the pass existed).
	/// </summary>
	public bool WaitsForText => TextAny.Count > 0;

	/// <summary>No field set: a rule that would keep every title of its kind (the validator refuses it).</summary>
	public bool IsEmpty =>
		GenresAll.Count == 0 && GenresAny.Count == 0 && GenresNone.Count == 0 && KeywordsAny.Count == 0 && KeywordsAll.Count == 0 && KeywordsNone.Count == 0
		&& Years.IsOpen && CountriesAny.Count == 0 && StudiosAny.Count == 0 && LengthMinutes.IsOpen && CommunityMin is null && CriticMin is null && TextAny.Count == 0;
}

/// <summary>
/// Nixie 1.9: one category of the catalogue: its id (lower case words and hyphens), its name as the tile says it (at most
/// <see cref="MicroGenres.NameMost"/> characters, <see cref="NixieTitles.Check"/>), films or shows, its family (at most one a
/// week of a family, in the lineup), its parent keyword collection's id when it has one, its rule, and the reason line's fact
/// ("Crime and drama, 1970–79": the titles', never the viewer's).
/// </summary>
/// <param name="Parent">The parent keyword collection's id as the keyword builder makes it ("keyword:neonoir"); null for none.</param>
public sealed record MicroGenre(string Id, string Name, IndexKind Kind, string Family, string? Parent, MicroGenreRule Rule, string Reason)
{
	/// <summary>What every micro-genre collection's id starts with.</summary>
	public const string IdPrefix = "micro:";

	/// <summary>Its collection's id: "micro:crime-drama-1970s".</summary>
	public string CollectionId => IdPrefix + Id;
}

/// <summary>Nixie 1.9: a catalogue read: the categories that passed the checks, in the file's order, and what was wrong with the rest.</summary>
public sealed class MicroGenreCatalogue
{
	private readonly Dictionary<string, MicroGenre> _byId;

	public MicroGenreCatalogue(IReadOnlyList<MicroGenre> categories, IReadOnlyList<string> problems)
	{
		Categories = categories;
		Problems = problems;
		_byId = new Dictionary<string, MicroGenre>(StringComparer.Ordinal);
		foreach (var category in categories)
		{
			_byId.TryAdd(category.Id, category);
		}
	}

	/// <summary>No categories (no file, or a damaged one).</summary>
	public static MicroGenreCatalogue Empty { get; } = new([], []);

	/// <summary>The categories that passed every check, in the file's order.</summary>
	public IReadOnlyList<MicroGenre> Categories { get; }

	/// <summary>
	/// What was wrong, one line a problem ("#3 “crime-drama-1970s”: the years run backwards (1979 to 1970)"): the catalogue's
	/// own words and ids only, never the library's. A category with a problem is left out; the tests want none.
	/// </summary>
	public IReadOnlyList<string> Problems { get; }

	/// <summary>A category by its id ("crime-drama-1970s") or its collection's id ("micro:crime-drama-1970s"); null for neither.</summary>
	public MicroGenre? Of(string? id) =>
		id is null ? null : _byId.TryGetValue(id.StartsWith(MicroGenre.IdPrefix, StringComparison.Ordinal) ? id[MicroGenre.IdPrefix.Length..] : id, out var found) ? found : null;
}

/// <summary>
/// Nixie 1.9 (Collections 2): the micro-genre catalogue, from the data file <c>SmartCollections\Data\micro-genres.json</c>
/// (embedded), and the keywords a rule may use, from <c>SmartCollections\Data\keywords-matchable.json</c> beside the
/// allowlist. As <see cref="SpotlightStudios"/>: a file that can't be read is an empty catalogue, and nothing throws. Each
/// category is checked, and one that fails a check is left out (<see cref="MicroGenreCatalogue.Problems"/> says why):
/// <list type="bullet">
/// <item>an id used before, or not lower case words and hyphens;</item>
/// <item>a name over <see cref="NameMost"/> characters, or one <see cref="NixieTitles.Check"/> refuses (a name used before too);</item>
/// <item>a field the format doesn't have, at any level;</item>
/// <item>an empty rule;</item>
/// <item>years or a length that run backwards;</item>
/// <item>a keyword that isn't matchable (<see cref="Matchable"/>);</item>
/// <item>a kind other than films or shows, a missing family or reason, a parent that is no keyword collection, a rating out of
/// its scale, and a genre or keyword both wanted and refused.</item>
/// </list>
/// </summary>
public static class MicroGenres
{
	/// <summary>The catalogue's name in the assembly.</summary>
	public const string Resource = "FluentFin.Core.SmartCollections.Data.micro-genres.json";

	/// <summary>The matchable keywords' file's name in the assembly.</summary>
	public const string MatchableResource = "FluentFin.Core.SmartCollections.Data.keywords-matchable.json";

	/// <summary>The catalogue format this code reads.</summary>
	public const int Version = 1;

	/// <summary>The longest name a tile may have (Netflix's names stay under about 50 characters).</summary>
	public const int NameMost = 50;

	private static readonly Lazy<IReadOnlySet<string>> s_matchable = new(() => LoadMatchable(typeof(MicroGenres).Assembly.GetManifestResourceStream(MatchableResource)));
	private static readonly Lazy<MicroGenreCatalogue> s_shipped = new(() => Load(typeof(MicroGenres).Assembly.GetManifestResourceStream(Resource), Matchable));

	/// <summary>
	/// The keywords a rule may use, lower case: the allowlist's (<see cref="Keywords.Allowed"/>), the seasonal ones, and the
	/// vetted list in <c>keywords-matchable.json</c>. Only the allowlist's ever name anything; the rest are matched, never said.
	/// </summary>
	public static IReadOnlySet<string> Matchable => s_matchable.Value;

	/// <summary>The shipped catalogue; empty while the data file is missing or damaged.</summary>
	public static MicroGenreCatalogue Shipped => s_shipped.Value;

	private static readonly JsonDocumentOptions s_options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

	/// <summary>
	/// The matchable keywords: the allowlist's and the seasonal ones, and those of <paramref name="json"/> (null or damaged:
	/// none more). The file is a list of keywords, or an object whose <c>keywords</c> list holds keywords or objects with a
	/// <c>keyword</c> (other fields are the authors' notes, not read).
	/// </summary>
	internal static IReadOnlySet<string> LoadMatchable(Stream? json)
	{
		var keywords = new HashSet<string>(StringComparer.Ordinal);
		keywords.UnionWith(Keywords.Allowed.Keys.Select(Norm));
		keywords.UnionWith(Keywords.Seasonal.Select(Norm));
		if (json is null)
		{
			return keywords;
		}

		var more = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			using (json)
			{
				using var doc = JsonDocument.Parse(json, s_options);
				var list = doc.RootElement.ValueKind switch
				{
					JsonValueKind.Array => doc.RootElement,
					JsonValueKind.Object when doc.RootElement.TryGetProperty("keywords", out var k) && k.ValueKind == JsonValueKind.Array => k,
					_ => default,
				};
				if (list.ValueKind != JsonValueKind.Array)
				{
					return keywords;
				}

				foreach (var item in list.EnumerateArray())
				{
					var word = item.ValueKind switch
					{
						JsonValueKind.String => item.GetString(),
						JsonValueKind.Object when item.TryGetProperty("keyword", out var k) && k.ValueKind == JsonValueKind.String => k.GetString(),
						_ => null,
					};
					if (!string.IsNullOrWhiteSpace(word))
					{
						more.Add(Norm(word));
					}
				}
			}
		}
		catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or InvalidOperationException or ArgumentException)
		{
			return keywords; // a damaged file adds none, and none of it half
		}

		keywords.UnionWith(more);
		return keywords;
	}

	/// <summary>
	/// The catalogue of <paramref name="json"/> (null or damaged: empty), its keywords checked against
	/// <paramref name="matchable"/> (null: <see cref="Matchable"/>). Never throws.
	/// </summary>
	internal static MicroGenreCatalogue Load(Stream? json, IReadOnlySet<string>? matchable = null)
	{
		if (json is null)
		{
			return MicroGenreCatalogue.Empty;
		}

		matchable ??= Matchable;
		try
		{
			using (json)
			{
				using var doc = JsonDocument.Parse(json, s_options);
				return Read(doc.RootElement, matchable);
			}
		}
		catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
		{
			return new MicroGenreCatalogue([], [$"the file can't be read ({ex.GetType().Name})"]);
		}
	}

	private static readonly string[] s_fileFields = ["version", "categories"];
	private static readonly string[] s_categoryFields = ["id", "name", "kind", "family", "parent", "rule", "reason"];
	private static readonly string[] s_ruleFields =
	[
		"genresAll", "genresAny", "genresNone", "keywordsAny", "keywordsAll", "keywordsNone", "years", "countriesAny", "studiosAny", "lengthMinutes",
		"communityMin", "criticMin", "textAny", "textMin",
	];

	private static MicroGenreCatalogue Read(JsonElement root, IReadOnlySet<string> matchable)
	{
		var problems = new List<string>();
		if (root.ValueKind != JsonValueKind.Object)
		{
			return new MicroGenreCatalogue([], ["the file is not an object"]);
		}

		var file = new Said("the file", problems);
		Fields(root, s_fileFields, file);
		if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != Version)
		{
			return new MicroGenreCatalogue([], [.. problems, $"the file's version is not {Version}"]);
		}

		if (!root.TryGetProperty("categories", out var list) || list.ValueKind != JsonValueKind.Array)
		{
			return new MicroGenreCatalogue([], [.. problems, "the file has no categories list"]);
		}

		var categories = new List<MicroGenre>();
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var n = 0;
		foreach (var item in list.EnumerateArray())
		{
			n++;
			var rawId = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
			var said = new Said(rawId is null ? $"#{n}" : $"#{n} “{rawId}”", problems);
			var category = Category(item, matchable, said);
			// an id or a name used before is refused whether or not the first passed its checks: which one was meant is the authors' call
			if (rawId is not null && !ids.Add(rawId))
			{
				said.Problem("the id is used before");
			}

			if (category is not null && !names.Add(category.Name))
			{
				said.Problem("the name is used before");
			}

			if (category is not null && !said.Bad)
			{
				categories.Add(category);
			}
		}

		return new MicroGenreCatalogue(categories, problems);
	}

	// one category, or null when it can't be one (its problems said)
	private static MicroGenre? Category(JsonElement item, IReadOnlySet<string> matchable, Said said)
	{
		if (item.ValueKind != JsonValueKind.Object)
		{
			said.Problem("not an object");
			return null;
		}

		Fields(item, s_categoryFields, said);

		var id = Text(item, "id", said, required: true);
		if (id is not null && !IsId(id))
		{
			said.Problem("the id is not lower case words and hyphens");
		}

		var name = Text(item, "name", said, required: true);
		if (name is not null)
		{
			if (name.Length > NameMost)
			{
				said.Problem($"the name is over {NameMost} characters ({name.Length})");
			}

			if (NixieTitles.Check(name) is { } why)
			{
				said.Problem($"the name {why}");
			}

			if (Filler(name) is { } filler)
			{
				said.Problem($"the name has the filler word “{filler}”");
			}
		}

		IndexKind? kind = Text(item, "kind", said, required: true) switch
		{
			null => null,
			"films" => IndexKind.Movie,
			"shows" => IndexKind.Series,
			var other => Refused(said, $"the kind “{other}” is not films or shows"),
		};

		var family = Text(item, "family", said, required: true);

		string? parent = null;
		if (Text(item, "parent", said, required: false) is { } parentText)
		{
			parent = ParentId(parentText);
			if (parent is null)
			{
				said.Problem($"the parent “{parentText}” is not a keyword collection");
			}
		}

		var reason = Text(item, "reason", said, required: true);
		if (reason is not null && NixieTitles.Check(reason) is { } reasonWhy)
		{
			said.Problem($"the reason {reasonWhy}");
		}

		MicroGenreRule? rule = null;
		if (!item.TryGetProperty("rule", out var ruleElement) || ruleElement.ValueKind == JsonValueKind.Null)
		{
			said.Problem("the rule is empty");
		}
		else if (ruleElement.ValueKind != JsonValueKind.Object)
		{
			said.Problem("the rule is not an object");
		}
		else
		{
			rule = Rule(ruleElement, matchable, said);
		}

		return id is null || name is null || kind is null || family is null || reason is null || rule is null
			? null
			: new MicroGenre(id, name, kind.Value, family, parent, rule, reason);
	}

	private static MicroGenreRule? Rule(JsonElement element, IReadOnlySet<string> matchable, Said said)
	{
		Fields(element, s_ruleFields, said, "the rule's ");
		var rule = new MicroGenreRule
		{
			GenresAll = Words(element, "genresAll", said, lower: false),
			GenresAny = Words(element, "genresAny", said, lower: false),
			GenresNone = Words(element, "genresNone", said, lower: false),
			KeywordsAny = Words(element, "keywordsAny", said, lower: true),
			KeywordsAll = Words(element, "keywordsAll", said, lower: true),
			KeywordsNone = Words(element, "keywordsNone", said, lower: true),
			Years = Range(element, "years", said, least: null),
			CountriesAny = Words(element, "countriesAny", said, lower: false),
			StudiosAny = Words(element, "studiosAny", said, lower: false),
			LengthMinutes = Range(element, "lengthMinutes", said, least: 0),
			CommunityMin = Number(element, "communityMin", said, 0, 10),
			CriticMin = Number(element, "criticMin", said, 0, 100),
			TextAny = Words(element, "textAny", said, lower: true),
			TextMin = WholeNumber(element, "textMin", said) ?? 1, // 1.9 (review item 2)
		};

		// 1.9 (the review of wip/190-text, item 2): a textMin needs a word list with that many different words
		if (element.TryGetProperty("textMin", out var textMin) && textMin.ValueKind != JsonValueKind.Null)
		{
			var different = rule.TextAny.Select(TextWords.Words).Where(x => x.Count > 0).Select(x => string.Join(' ', x)).Distinct(StringComparer.Ordinal).Count();
			if (rule.TextMin < 1)
			{
				said.Problem($"the rule's textMin is under 1 ({rule.TextMin})");
			}
			else if (rule.TextAny.Count == 0)
			{
				said.Problem("the rule's textMin has no textAny");
			}
			else if (rule.TextMin > different)
			{
				said.Problem($"the rule's textMin ({rule.TextMin}) is more than its textAny's different words ({different})");
			}
		}

		foreach (var keyword in rule.KeywordsAny.Concat(rule.KeywordsAll).Concat(rule.KeywordsNone).Distinct(StringComparer.Ordinal))
		{
			if (!matchable.Contains(keyword))
			{
				said.Problem($"the keyword “{keyword}” is not matchable");
			}
		}

		foreach (var studio in rule.StudiosAny.Where(x => Make.Key(x).Length == 0))
		{
			said.Problem($"the studio “{studio}” has no letters or digits");
		}

		// wanted and refused: a rule that can never match
		foreach (var genre in rule.GenresAll.Intersect(rule.GenresNone, StringComparer.OrdinalIgnoreCase))
		{
			said.Problem($"the genre “{genre}” is both wanted and refused");
		}

		foreach (var keyword in rule.KeywordsAll.Intersect(rule.KeywordsNone, StringComparer.Ordinal))
		{
			said.Problem($"the keyword “{keyword}” is both wanted and refused");
		}

		if (rule.IsEmpty)
		{
			said.Problem("the rule is empty");
		}

		return rule;
	}

	// a parent as the keyword builder's id: "keyword:neo-noir" and "keyword:neonoir" are "keyword:neonoir"; null for no allowlisted keyword
	internal static string? ParentId(string parent)
	{
		const string prefix = "keyword:";
		if (!parent.StartsWith(prefix, StringComparison.Ordinal))
		{
			return null;
		}

		var key = Make.Key(parent[prefix.Length..]);
		return key.Length > 0 && Keywords.Allowed.Keys.Any(x => Make.Key(x) == key) ? prefix + key : null;
	}

	// the research's pitfall 3 (generic names: "Hidden Gems", "Must-Watch Classics"): words that praise and promise nothing a
	// rule can check; a name says facts (era, place, source, subject, a mood a rule field backs)
	private static readonly string[] s_filler = ["gem", "gems", "must-watch", "must-see", "favorite", "favorites", "favourite", "favourites", "ultimate", "essential", "unmissable"];

	private static string? Filler(string name) =>
		name.Split([' ', ',', ':', '·', '“', '”', '"', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
			.FirstOrDefault(word => s_filler.Contains(word.Trim('.', '!', '?'), StringComparer.OrdinalIgnoreCase));

	// lower case letters and digits, in words joined by single hyphens
	private static bool IsId(string id) =>
		id.Length > 0 && id[0] != '-' && id[^1] != '-' && !id.Contains("--", StringComparison.Ordinal) && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

	private static string Norm(string keyword) => keyword.Trim().ToLowerInvariant();

	private static IndexKind? Refused(Said said, string problem)
	{
		said.Problem(problem);
		return null;
	}

	// every field of the object is one the format has, and none is given twice
	private static void Fields(JsonElement element, string[] known, Said said, string whose = "")
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var property in element.EnumerateObject())
		{
			if (Array.IndexOf(known, property.Name) < 0)
			{
				said.Problem($"{whose}field “{property.Name}” is not in the format");
			}
			else if (!seen.Add(property.Name))
			{
				said.Problem($"{whose}field “{property.Name}” is given twice");
			}
		}
	}

	// a text field, trimmed; null when missing (a problem when required) or not text (a problem)
	private static string? Text(JsonElement element, string field, Said said, bool required)
	{
		if (!element.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
		{
			if (required)
			{
				said.Problem($"no {field}");
			}

			return null;
		}

		if (value.ValueKind != JsonValueKind.String)
		{
			said.Problem($"the {field} is not text");
			return null;
		}

		var text = value.GetString()!.Trim();
		if (text.Length == 0)
		{
			said.Problem($"the {field} is empty");
			return null;
		}

		return text;
	}

	// a list of words, each trimmed (keywords lower case), each once; missing or null: none
	private static IReadOnlyList<string> Words(JsonElement element, string field, Said said, bool lower)
	{
		if (!element.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
		{
			return [];
		}

		if (value.ValueKind != JsonValueKind.Array)
		{
			said.Problem($"the rule's {field} is not a list");
			return [];
		}

		var words = new List<string>();
		foreach (var item in value.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
			{
				said.Problem($"the rule's {field} has an item that is not a word");
				continue;
			}

			var word = lower ? Norm(item.GetString()!) : item.GetString()!.Trim();
			if (!words.Contains(word, lower ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
			{
				words.Add(word);
			}
		}

		return words;
	}

	// [from, to], either a whole number or null; missing or null: open
	private static MicroRange Range(JsonElement element, string field, Said said, int? least)
	{
		if (!element.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
		{
			return default;
		}

		if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
		{
			said.Problem($"the rule's {field} is not [from, to]");
			return default;
		}

		int? End(JsonElement end)
		{
			if (end.ValueKind == JsonValueKind.Null)
			{
				return null;
			}

			if (end.ValueKind == JsonValueKind.Number && end.TryGetInt32(out var number))
			{
				return number;
			}

			said.Problem($"the rule's {field} has an end that is not a whole number");
			return null;
		}

		var range = new MicroRange(End(value[0]), End(value[1]));
		if (range is { From: { } from, To: { } to } && from > to)
		{
			said.Problem($"the rule's {field} run backwards ({from} to {to})");
		}

		if (least is { } min && (range.From < min || range.To < min))
		{
			said.Problem($"the rule's {field} go below {min}");
		}

		return range;
	}

	// 1.9 (review item 2): a whole number; missing or null: none
	private static int? WholeNumber(JsonElement element, string field, Said said)
	{
		if (!element.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
		{
			return null;
		}

		if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
		{
			said.Problem($"the rule's {field} is not a whole number");
			return null;
		}

		return number;
	}

	// a number in [least, most]; missing or null: none
	private static double? Number(JsonElement element, string field, Said said, double least, double most)
	{
		if (!element.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
		{
			return null;
		}

		if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
		{
			said.Problem($"the rule's {field} is not a number");
			return null;
		}

		if (number < least || number > most)
		{
			said.Problem($"the rule's {field} is outside {least} to {most}");
		}

		return number;
	}

	// a category's problems, said under its label
	private sealed class Said(string where, List<string> problems)
	{
		public bool Bad { get; private set; }

		public void Problem(string what)
		{
			problems.Add($"{where}: {what}");
			Bad = true;
		}
	}
}
