using System.Globalization;
using System.Text.RegularExpressions;
using FluentFin.Core.SmartCollections;

namespace Nixie.Collections;

/// <summary>
/// The catalogue's and the matchable list's lint: what the 1.9 brief asks of a name, a rule and a reason line, and of a keyword
/// a rule may use. Each problem is one line, "id: what". The tests run it over the shipped files.
/// </summary>
public static partial class Lint
{
	/// <summary>Netflix's cap (the research): a name of at most 50 characters.</summary>
	public const int NameMost = 50;

	/// <summary>A reason line's facts, before the builder adds the count.</summary>
	public const int ReasonMost = 70;

	/// <summary>TMDb's film genres, as Jellyfin keeps them.</summary>
	public static readonly IReadOnlySet<string> FilmGenres = new HashSet<string>(StringComparer.Ordinal)
	{
		"Action", "Adventure", "Animation", "Comedy", "Crime", "Documentary", "Drama", "Family", "Fantasy", "History", "Horror",
		"Music", "Mystery", "Romance", "Science Fiction", "TV Movie", "Thriller", "War", "Western",
	};

	/// <summary>
	/// The show genres a rule may name: TheTVDB's (the owner's server's shows: "Action" 1,241, "Science Fiction" 724, "Anime"
	/// 1,281, "Mini-Series" 323) and TMDb's TV genres, which combine some ("Action &amp; Adventure", "Sci-Fi &amp; Fantasy").
	/// </summary>
	public static readonly IReadOnlySet<string> ShowGenres = new HashSet<string>(StringComparer.Ordinal)
	{
		"Action", "Adventure", "Animation", "Anime", "Awards Show", "Children", "Comedy", "Crime", "Documentary", "Drama", "Family",
		"Fantasy", "Food", "Game Show", "History", "Home and Garden", "Horror", "Indie", "Martial Arts", "Mini-Series", "Musical",
		"Mystery", "News", "Reality", "Romance", "Science Fiction", "Soap", "Sport", "Suspense", "Talk Show", "Thriller", "Travel",
		"War", "Western",
		"Action & Adventure", "Sci-Fi & Fantasy", "War & Politics", "Kids", "Talk",
	};

	/// <summary>
	/// TheTVDB's show genres with a TMDb counterpart: a shows rule that needs one accepts both (a server fed by TMDb has only the
	/// combined name). TheTVDB's others (Anime, Mini-Series, Horror, Romance, Sport…) have none on TMDb.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> TmdbShowGenre = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		["Action"] = "Action & Adventure",
		["Adventure"] = "Action & Adventure",
		["Science Fiction"] = "Sci-Fi & Fantasy",
		["Fantasy"] = "Sci-Fi & Fantasy",
		["War"] = "War & Politics",
		["Children"] = "Kids",
		["Talk Show"] = "Talk",
	};

	// filler and praise (the research's pitfall 3): words that promise nothing a rule checks
	private static readonly string[] s_filler =
	[
		"gem", "gems", "must-watch", "must-see", "favorite", "favorites", "favourite", "favourites", "ultimate", "epic", "epics", "best",
		"essential", "essentials", "greatest", "amazing", "awesome", "top", "unmissable", "binge-worthy", "bingeable", "classic",
		"classics", "iconic", "legendary", "perfect", "masterpieces", "brilliant",
	];

	// a mood word in a name needs a rule field that says so (the brief): a tone keyword, or a genre where the word is the genre's
	private static readonly (string Word, string[] Keywords, string[] Genres)[] s_moods =
	[
		("dark", ["dark comedy", "neo-noir", "film noir", "dark fantasy"], []),
		("gritty", ["gritty", "neo-noir"], []),
		("feel-good", ["heartwarming", "comforting", "cheerful", "joyful", "hopeful"], []),
		("heartwarming", ["heartwarming"], []),
		("witty", ["witty", "wry"], []),
		("wry", ["wry"], []),
		("lighthearted", ["lighthearted"], []),
		("whimsical", ["whimsical"], []),
		("suspenseful", ["suspenseful"], []),
		("tense", ["tense", "suspenseful"], []),
		("intense", ["intense"], []),
		("inspiring", ["inspirational"], []),
		("inspirational", ["inspirational"], []),
		("hopeful", ["hopeful"], []),
		("melancholy", ["melancholy"], []),
		("absurd", ["absurd", "absurdism", "surreal"], []),
		("surreal", ["surreal", "surrealism"], []),
		("playful", ["playful"], []),
		("thought-provoking", ["thoughtful", "provocative", "philosophical"], []),
		("cerebral", ["thoughtful", "philosophical", "mind-bending"], []),
		("mind-bending", ["mind-bending", "nonlinear timeline"], []),
		("romantic", ["romcom", "love", "romance"], ["Romance"]),
		("violent", ["violence", "brutality", "gore"], []),
		("gory", ["gore", "splatter"], []),
		("quirky", ["quirky"], []),
		("offbeat", ["quirky", "absurd"], []),
		("goofy", ["slapstick comedy", "absurd"], []),
		("scary", [], ["Horror"]),
		("cozy", ["cozy"], []),
	];

	// a region word needs a country (films) or a network (shows)
	private static readonly string[] s_regions =
	[
		"british", "english", "scottish", "irish", "french", "german", "italian", "spanish", "korean", "japanese",
		"chinese", "hong kong", "taiwanese", "indian", "bollywood", "thai", "indonesian", "filipino", "australian", "canadian",
		"mexican", "brazilian", "argentine", "argentinian", "danish", "swedish", "norwegian", "finnish", "icelandic", "nordic",
		"scandinavian", "dutch", "belgian", "polish", "czech", "russian", "soviet", "turkish", "iranian", "israeli", "greek",
		"european", "latin american", "new zealand", "kiwi", "nigerian", "egyptian", "south african", "swiss", "austrian",
	];

	/// <summary>Parts of a keyword a matchable list may never hold: TMDb's junk ("duringcreditsstinger") and words for harm.</summary>
	public static readonly string[] DeniedParts = ["stinger", "pedophil", "homophob", "transphob", "nudity", "porn", "fetish"];

	/// <summary>
	/// Whole words a matchable keyword may never hold: production facts, spoilers, and harm or sex that would read badly in any
	/// rule ("woman director", "twist ending", "death of …", "suicide").
	/// </summary>
	public static readonly string[] DeniedWords =
	[
		"director", "suicide", "rape", "sexual", "incest", "nude", "sex", "erotic", "twist", "surprise", "death of", "dies",
		"killed", "dying", "spoiler", "ending", "self-harm", "abuse", "torture", "slur", "racist", "cannibal", "cannibalism",
		"molestation", "lynching", "genocide",
	];

	public static List<string> Check(Catalogue catalogue, Matchable matchable)
	{
		var problems = new List<string>();
		problems.AddRange(CheckMatchable(matchable));
		var extra = matchable.Keywords.Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (catalogue.Version != 1)
		{
			problems.Add($"catalogue: version {catalogue.Version}, not 1");
		}

		var ids = new HashSet<string>(StringComparer.Ordinal);
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var c in catalogue.Categories)
		{
			var at = string.IsNullOrEmpty(c.Id) ? "(no id)" : c.Id;
			void Say(string what) => problems.Add($"{at}: {what}");
			if (!KebabRegex().IsMatch(c.Id))
			{
				Say("the id isn't lower-case words joined by hyphens");
			}

			if (!ids.Add(c.Id))
			{
				Say("the id is used twice");
			}

			foreach (var problem in NameProblems(c))
			{
				Say(problem);
			}

			if (!names.Add(c.Name.Trim()))
			{
				Say("the name is used twice");
			}

			if (c.Kind is not ("films" or "shows"))
			{
				Say($"the kind is \"{c.Kind}\", not films or shows");
			}

			if (!KebabRegex().IsMatch(c.Family))
			{
				Say("the family isn't lower-case words joined by hyphens");
			}

			if (c.Parent is not null && Census.ParentKeyword(c.Parent) is null)
			{
				Say($"the parent \"{c.Parent}\" is no keyword collection (keyword: and an allowlisted keyword's letters and digits)");
			}

			if (string.IsNullOrWhiteSpace(c.Reason) || c.Reason.Length > ReasonMost || char.IsLower(c.Reason[0]))
			{
				Say($"the reason line is empty, over {ReasonMost} characters, or starts in lower case");
			}
			else if (NixieTitles.Check(c.Reason) is { } why)
			{
				Say($"the reason line {why}");
			}

			foreach (var problem in RuleProblems(c, extra))
			{
				Say(problem);
			}
		}

		return problems;
	}

	public static IEnumerable<string> NameProblems(Category c)
	{
		var name = c.Name;
		if (string.IsNullOrWhiteSpace(name))
		{
			yield return "no name";
			yield break;
		}

		if (name.Length > NameMost)
		{
			yield return $"the name has {name.Length} characters, over {NameMost}";
		}

		if (name != name.Trim() || name.Contains("  ", StringComparison.Ordinal))
		{
			yield return "the name has stray spaces";
		}

		if (NixieTitles.Check(name) is { } why)
		{
			yield return $"the name {why}";
		}

		var words = Words(name);
		if (words.FirstOrDefault(w => s_filler.Contains(w)) is { } filler)
		{
			yield return $"the name says \"{filler}\", a word no rule checks";
		}

		var r = c.Rule;
		foreach (var (word, backing, genres) in s_moods)
		{
			// backed: a backing keyword every title must have, or a keyword list of backing words only, or the genre itself
			var backed = r.KeywordsAll.Any(k => backing.Contains(k))
				|| (r.KeywordsAny.Length > 0 && r.KeywordsAny.All(k => backing.Contains(k)))
				|| genres.Any(g => r.GenresAll.Contains(g) || (r.GenresAny is [var only] && only == g));
			if (Says(name, word) && !backed)
			{
				yield return $"the name says \"{word}\" with no keyword or genre that backs it";
			}
		}

		if (s_regions.FirstOrDefault(x => Says(name, x)) is { } region && r.CountriesAny.Length == 0 && r.StudiosAny.Length == 0)
		{
			yield return $"the name says \"{region}\" with no country or network in its rule";
		}

		if ((Says(name, "acclaimed") || Says(name, "critically")) && r.CriticMin is null)
		{
			yield return "the name says \"acclaimed\" with no critics' rating in its rule";
		}

		if ((Says(name, "highly rated") || Says(name, "top-rated") || Says(name, "well-rated")) && r.CommunityMin is null)
		{
			yield return "the name says it is rated with no rating in its rule";
		}

		// a decade in the name is the rule's decade ("1970s": 1970–1979), and "from the 1970s" isn't wider or narrower; "set in
		// the 1980s" is a setting (a keyword), not when it was made
		foreach (Match m in DecadeRegex().Matches(name))
		{
			if (name[..m.Index].EndsWith("set in the ", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var from = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
			if (r.Years is not [{ } a, { } b] || a < from || b > from + 9)
			{
				yield return $"the name says the {m.Value} and its years aren't within {from}–{from + 9}";
			}
		}

		if ((Says(name, "half-hour") || Says(name, "short")) && r.LengthMinutes is not [_, { }])
		{
			yield return "the name says short or half-hour with no longest length in its rule";
		}

		if (Says(name, "hour-long") && r.LengthMinutes is not [{ }, _])
		{
			yield return "the name says hour-long with no shortest length in its rule";
		}
	}

	public static IEnumerable<string> RuleProblems(Category c, IReadOnlySet<string> extra)
	{
		var r = c.Rule;
		if (!r.Narrows)
		{
			yield return "the rule doesn't narrow anything";
		}

		var genres = c.Films ? FilmGenres : ShowGenres;
		foreach (var g in r.GenresAll.Concat(r.GenresAny).Concat(r.GenresNone).Where(g => !genres.Contains(g)))
		{
			yield return $"\"{g}\" is no {(c.Films ? "film" : "show")} genre";
		}

		if (r.GenresAll.Intersect(r.GenresNone).Any() || r.GenresAny.Intersect(r.GenresNone).Any())
		{
			yield return "a genre is both wanted and not";
		}

		foreach (var k in r.AllKeywords)
		{
			if (k != k.Trim().ToLowerInvariant())
			{
				yield return $"the keyword \"{k}\" isn't trimmed lower case";
			}
			else if (!Rules.Listed(k) && !extra.Contains(k))
			{
				yield return $"the keyword \"{k}\" is neither allowlisted nor on the matchable list";
			}
		}

		if (r.KeywordsAny.Concat(r.KeywordsAll).Intersect(r.KeywordsNone).Any())
		{
			yield return "a keyword is both wanted and not";
		}

		if (!c.Films && (r.AllKeywords.Any() || r.CountriesAny.Length > 0 || r.CriticMin is not null))
		{
			yield return "a shows rule uses keywords, countries or a critics' rating (shows have none on the servers surveyed)";
		}

		if (c.Films && r.TextAny.Length > 0)
		{
			yield return "a films rule uses summary words (step 1b is for shows)";
		}

		if (r.TextAny.Any(w => string.IsNullOrWhiteSpace(w) || w != w.Trim().ToLowerInvariant()))
		{
			yield return "a summary word isn't trimmed lower case";
		}

		// the summaries pass compares words: two entries of the same words ("co-workers", "co workers") are one, and an entry
		// that ends a sentence inside ("dr. who") can never match as one phrase
		var keys = r.TextAny.Select(Text.Key).ToList();
		if (keys.GroupBy(x => x, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
		{
			yield return $"the summary entry \"{twice.Key}\" is listed twice (as the summaries pass reads words)";
		}

		if (r.TextAny.FirstOrDefault(w => Text.Tokens(w).Skip(1).Any(t => t.AfterBreak)) is { } broken)
		{
			yield return $"the summary entry \"{broken}\" has a sentence break inside: it can never match";
		}

		if (r.TextMin is { } least && (r.TextAny.Length == 0 || least < 2 || least > keys.Distinct(StringComparer.Ordinal).Count()))
		{
			yield return "textMin is not between 2 and the number of different summary entries (or has no summary entries)";
		}

		if (r.Years is { } years && (years.Length != 2 || years.Any(y => y is < 1880 or > 2040) || (years is [{ } a, { } b] && a > b)))
		{
			yield return "the years aren't [from, to] between 1880 and 2040";
		}

		if (r.LengthMinutes is { } length && (length.Length != 2 || length.Any(m => m is < 1 or > 600) || (length is [{ } a2, { } b2] && a2 > b2)))
		{
			yield return "the length isn't [least, most] minutes between 1 and 600";
		}

		if (r.CommunityMin is < 0 or > 10 || r.CriticMin is < 0 or > 100)
		{
			yield return "a rating is out of its scale (community 0–10, critics 0–100)";
		}

		if (!c.Films)
		{
			foreach (var problem in TmdbGenreProblems(r))
			{
				yield return problem;
			}
		}
	}

	// a shows rule that needs a TheTVDB genre with a TMDb counterpart accepts both: genresAny lists the pair, genresNone refuses
	// "Kids" with "Children", and genresAll may hold one only when genresAny's one OR group is taken by another pair
	private static IEnumerable<string> TmdbGenreProblems(Rule r)
	{
		foreach (var g in r.GenresAny.Where(g => TmdbShowGenre.TryGetValue(g, out var alias) && !r.GenresAny.Contains(alias)))
		{
			yield return $"genresAny has \"{g}\" without TMDb's \"{TmdbShowGenre[g]}\"";
		}

		foreach (var g in r.GenresNone.Where(g => g is "Children" or "Talk Show" && !r.GenresNone.Contains(TmdbShowGenre[g])))
		{
			yield return $"genresNone has \"{g}\" without TMDb's \"{TmdbShowGenre[g]}\"";
		}

		var anyTaken = r.GenresAny.Any(g => TmdbShowGenre.ContainsKey(g));
		foreach (var g in r.GenresAll.Where(TmdbShowGenre.ContainsKey))
		{
			if (!anyTaken)
			{
				yield return $"genresAll needs \"{g}\": put it in genresAny with TMDb's \"{TmdbShowGenre[g]}\"";
			}
		}
	}

	public static IEnumerable<string> CheckMatchable(Matchable matchable)
	{
		if (matchable.Version != 1)
		{
			yield return $"matchable: version {matchable.Version}, not 1";
		}

		foreach (var (name, reason) in matchable.Groups)
		{
			if (string.IsNullOrWhiteSpace(reason))
			{
				yield return $"matchable group \"{name}\": no reason";
			}

			if (!matchable.Entries.Any(x => x.Group == name))
			{
				yield return $"matchable group \"{name}\": no keywords";
			}
		}

		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var entry in matchable.Entries)
		{
			var k = entry.Keyword;
			if (!matchable.Groups.ContainsKey(entry.Group))
			{
				yield return $"matchable \"{k}\": its group \"{entry.Group}\" has no reason";
			}

			if (string.IsNullOrWhiteSpace(k) || k != k.Trim().ToLowerInvariant())
			{
				yield return $"matchable \"{k}\": not trimmed lower case";
			}
			else if (!seen.Add(k))
			{
				yield return $"matchable \"{k}\": listed twice";
			}
			else if (Rules.Listed(k))
			{
				yield return $"matchable \"{k}\": already kept (allowlisted or seasonal)";
			}
			else if ((DeniedParts.FirstOrDefault(d => k.Contains(d, StringComparison.Ordinal)) ?? DeniedWords.FirstOrDefault(d => Says(k, d))) is { } denied)
			{
				yield return $"matchable \"{k}\": denied (\"{denied}\")";
			}
		}
	}

	/// <summary>The matchable keywords no category uses: weight the index would keep for nothing (revision 4 keeps them all).</summary>
	public static IEnumerable<string> Unused(Catalogue catalogue, Matchable matchable)
	{
		var used = catalogue.Categories.SelectMany(c => c.Rule.AllKeywords).ToHashSet(StringComparer.Ordinal);
		return matchable.Keywords.Where(k => !used.Contains(k));
	}

	private static string[] Words(string text) =>
		[.. text.ToLowerInvariant().Split([' ', ',', ':', '·', '(', ')', '&', '/'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('\'', '"', '.'))];

	// a word or phrase as whole words ("dark" in "Dark Comedies", not in "Darkwing")
	private static bool Says(string name, string phrase) =>
		Regex.IsMatch(name, $@"(?<![\p{{L}}\p{{N}}-]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	private static partial Regex KebabRegex();

	[GeneratedRegex(@"\b(1[89]\d0|20\d0)s\b")]
	private static partial Regex DecadeRegex();
}
