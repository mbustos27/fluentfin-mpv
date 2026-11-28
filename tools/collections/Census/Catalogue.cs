using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nixie.Collections;

/// <summary>
/// The category catalogue (ui\FluentFin.Core\SmartCollections\Data\micro-genres.json), as the census reads it. The shape is the
/// 1.9 brief's: every rule field is optional, fields AND together, and each list means what its name says.
/// </summary>
public sealed class Catalogue
{
	public int Version { get; init; }

	public List<Category> Categories { get; init => field = value ?? []; } = [];

	internal static readonly JsonSerializerOptions Json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a misspelt field ("genreAll") is an error, not a rule that matches everything
	};

	public static Catalogue Read(string path) => Parse(File.ReadAllText(path));

	public static Catalogue Parse(string json) => JsonSerializer.Deserialize<Catalogue>(json, Json) ?? throw new JsonException("empty catalogue");

	/// <summary>
	/// An authoring aid: this catalogue with some categories' rules or reason lines replaced (<paramref name="patch"/>), so a batch
	/// of rule changes is one reviewable file. Names are never patched (the owner's review renames). Throws on an unknown id.
	/// </summary>
	public Catalogue With(CataloguePatch patch)
	{
		var byId = patch.Patches.ToDictionary(x => x.Id, StringComparer.Ordinal);
		var unknown = byId.Keys.Where(id => !Categories.Exists(c => c.Id == id)).ToList();
		if (unknown.Count > 0)
		{
			throw new InvalidOperationException("the patch names categories the catalogue hasn't: " + string.Join(", ", unknown));
		}

		return new Catalogue
		{
			Version = Version,
			Categories =
			[
				.. Categories.Select(c => byId.TryGetValue(c.Id, out var p)
					? new Category { Id = c.Id, Name = c.Name, Kind = c.Kind, Family = c.Family, Parent = c.Parent, Rule = p.Rule ?? c.Rule, Reason = p.Reason ?? c.Reason }
					: c),
			],
		};
	}

	// names and reasons keep their typography (“ ” –) rather than \u escapes
	private static readonly JsonSerializerOptions Text = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	/// <summary>
	/// The catalogue in the shipped file's layout: two lines a category (its words, then its rule and reason), rule fields in the
	/// brief's order and only those that narrow. <paramref name="only"/>: keep just these ids (the cut), in the file's order.
	/// </summary>
	public string Format(IReadOnlySet<string>? only = null)
	{
		var kept = Categories.Where(c => only is null || only.Contains(c.Id)).ToList();
		var text = new System.Text.StringBuilder();
		text.Append("{\n  \"version\": ").Append(Version.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(",\n  \"categories\": [\n");
		for (var i = 0; i < kept.Count; i++)
		{
			var c = kept[i];
			text.Append("    { \"id\": ").Append(Q(c.Id)).Append(", \"name\": ").Append(Q(c.Name)).Append(", \"kind\": ").Append(Q(c.Kind))
				.Append(", \"family\": ").Append(Q(c.Family));
			if (c.Parent is not null)
			{
				text.Append(", \"parent\": ").Append(Q(c.Parent));
			}

			text.Append(",\n      \"rule\": ").Append(RuleText(c.Rule)).Append(", \"reason\": ").Append(Q(c.Reason)).Append(" }")
				.Append(i < kept.Count - 1 ? ",\n" : "\n");
		}

		return text.Append("  ]\n}\n").ToString();
	}

	private static string Q(string text) => JsonSerializer.Serialize(text, Text);

	private static string RuleText(Rule r)
	{
		var fields = new List<string>();
		void List(string name, string[] values)
		{
			if (values.Length > 0)
			{
				fields.Add($"\"{name}\": [{string.Join(", ", values.Select(Q))}]");
			}
		}

		void Range(string name, int?[]? range)
		{
			if (range is { Length: 2 } && (range[0] is not null || range[1] is not null))
			{
				fields.Add($"\"{name}\": [{N(range[0])}, {N(range[1])}]");
			}
		}

		void Number(string name, double? value)
		{
			if (value is { } v)
			{
				fields.Add($"\"{name}\": {v.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
			}
		}

		List("genresAll", r.GenresAll);
		List("genresAny", r.GenresAny);
		List("genresNone", r.GenresNone);
		List("keywordsAny", r.KeywordsAny);
		List("keywordsAll", r.KeywordsAll);
		List("keywordsNone", r.KeywordsNone);
		Range("years", r.Years);
		List("countriesAny", r.CountriesAny);
		List("studiosAny", r.StudiosAny);
		Range("lengthMinutes", r.LengthMinutes);
		Number("communityMin", r.CommunityMin);
		Number("criticMin", r.CriticMin);
		List("textAny", r.TextAny);
		Number("textMin", r.TextMin);
		return fields.Count == 0 ? "{}" : "{ " + string.Join(", ", fields) + " }";
	}

	private static string N(int? n) => n is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
}

/// <summary>One category: a name in Netflix's order, a rule over what the index keeps, and the reason line's facts.</summary>
public sealed class Category
{
	public string Id { get; init; } = "";

	public string Name { get; init; } = "";

	/// <summary>"films" or "shows".</summary>
	public string Kind { get; init; } = "";

	public string Family { get; init; } = "";

	/// <summary>A keyword collection's id ("keyword:neonoir"), or null.</summary>
	public string? Parent { get; init; }

	public Rule Rule { get; init => field = value ?? new(); } = new();

	public string Reason { get; init; } = "";

	[JsonIgnore]
	public bool Films => Kind == "films";
}

/// <summary>A category's rule. Lists that are empty (or missing) don't narrow; a range's null end is open.</summary>
public sealed class Rule
{
	public string[] GenresAll { get; init => field = value ?? []; } = [];
	public string[] GenresAny { get; init => field = value ?? []; } = [];
	public string[] GenresNone { get; init => field = value ?? []; } = [];
	public string[] KeywordsAny { get; init => field = value ?? []; } = [];
	public string[] KeywordsAll { get; init => field = value ?? []; } = [];
	public string[] KeywordsNone { get; init => field = value ?? []; } = [];

	/// <summary>[from, to], both years included; either may be null.</summary>
	public int?[]? Years { get; init; }

	public string[] CountriesAny { get; init => field = value ?? []; } = [];
	public string[] StudiosAny { get; init => field = value ?? []; } = [];

	/// <summary>A film's runtime, or a show's episode length: [least, most] minutes, both included; either may be null.</summary>
	public int?[]? LengthMinutes { get; init; }

	public double? CommunityMin { get; init; }
	public double? CriticMin { get; init; }

	/// <summary>The shows' summary words (step 1b); the index keeps no summary, so a rule with them is "unknown" here.</summary>
	public string[] TextAny { get; init => field = value ?? []; } = [];

	/// <summary>
	/// 1.9 (the text review): how many different entries of <see cref="TextAny"/> a summary must have (null: one). For lists whose
	/// words are common alone ("family", "boss"), where two of them together say the theme.
	/// </summary>
	public int? TextMin { get; init; }

	[JsonIgnore]
	public IEnumerable<string> AllKeywords => KeywordsAny.Concat(KeywordsAll).Concat(KeywordsNone);

	/// <summary>Whether anything narrows the rule beyond its kind.</summary>
	[JsonIgnore]
	public bool Narrows =>
		GenresAll.Length + GenresAny.Length + GenresNone.Length + KeywordsAny.Length + KeywordsAll.Length + KeywordsNone.Length
		+ CountriesAny.Length + StudiosAny.Length + TextAny.Length > 0
		|| Years is [not null, _] or [_, not null] || LengthMinutes is [not null, _] or [_, not null]
		|| CommunityMin is not null || CriticMin is not null;
}

/// <summary>
/// The vetted keywords a rule may use beyond the index's allowlist (Data\keywords-matchable.json): TMDb's public keyword names,
/// each in a group, and each group with a short reason. Matchable, never named: no collection or reason line is ever made of
/// one on its own. The app's loader (MicroGenres.LoadMatchable) reads only <c>keywords</c> and each one's <c>keyword</c>;
/// <c>groups</c> and <c>group</c> are the authors' notes.
/// </summary>
public sealed class Matchable
{
	public int Version { get; init; }

	/// <summary>The file's note for its readers.</summary>
	public string? About { get; init; }

	/// <summary>Group name → why its keywords may be matched.</summary>
	public Dictionary<string, string> Groups { get; init => field = value ?? []; } = [];

	[JsonPropertyName("keywords")]
	public List<MatchableKeyword> Entries { get; init => field = value ?? []; } = [];

	[JsonIgnore]
	public IEnumerable<string> Keywords => Entries.Select(x => x.Keyword);

	public static Matchable Read(string path) => Parse(File.ReadAllText(path));

	public static Matchable Parse(string json) => JsonSerializer.Deserialize<Matchable>(json, Catalogue.Json) ?? throw new JsonException("empty list");
}

public sealed class MatchableKeyword
{
	public string Keyword { get; init; } = "";

	public string Group { get; init; } = "";
}

/// <summary>A batch of rule or reason-line changes (<see cref="Catalogue.With"/>), each with why.</summary>
public sealed class CataloguePatch
{
	public string? About { get; init; }

	public List<CategoryPatch> Patches { get; init => field = value ?? []; } = [];

	public static CataloguePatch Read(string path) =>
		JsonSerializer.Deserialize<CataloguePatch>(File.ReadAllText(path), Catalogue.Json) ?? throw new JsonException("empty patch");
}

/// <summary>One category's change: its new rule, its new reason line, or both; and why.</summary>
public sealed class CategoryPatch
{
	public string Id { get; init; } = "";

	/// <summary>Why the change: the false match it stops, or the server it lets work.</summary>
	public string Why { get; init; } = "";

	public Rule? Rule { get; init; }

	public string? Reason { get; init; }
}
