using System.Globalization;
using System.Text;
using System.Text.Json;
using Nixie.Collections;

// Nixie 1.9 (Collections 2, stream cat): the census of the category catalogue on a saved library index. Counts only on the
// console: never a title, an id or a person. Titles go only to the files --examples and --names write (the owner's own review).
var options = Options.Parse(args);
if (options is null)
{
	Console.Error.WriteLine("""
		usage: Census [--index <saved index .json>] [--catalogue <micro-genres.json>] [--matchable <keywords-matchable.json>]
		              [--check] [--vocab [top]] [--json <out.json>] [--examples <out.md>] [--format <out.json> [--keep <ids.txt>]] [--names <out.md>] [--patch <patch.json>]
		  --check      lint the catalogue and the matchable list (no index needed); exit 1 on a problem
		  --vocab      the index's genres, countries, studios, ratings and lengths per kind, with counts
		  --json       each category's pool, written to a file
		  --examples   up to 5 titles of each known pool, written to a local file for the owner's review (never printed)
		  --format     write the catalogue in the shipped layout; --keep cuts it to the ids a file lists
		  --names      the names list for the owner's review, with example titles, written to a local file (never printed)
		  --patch      apply a batch of rule or reason-line changes to the catalogue first (with --format, to write them)
		The catalogue and the list default to the repository's ui\FluentFin.Core\SmartCollections\Data.
		""");
	return 2;
}

if (options is { Vocab: { } top, Index: { } vocabIndex })
{
	Console.Write(Vocab.Of(IndexFile.Read(vocabIndex).Titles, top));
	return 0;
}

var data = Options.DataFolder();
var catalogue = Catalogue.Read(options.Catalogue ?? Path.Combine(data ?? ".", "micro-genres.json"));
if (options.Patch is { } patchFile)
{
	catalogue = catalogue.With(CataloguePatch.Read(patchFile));
}
var matchablePath = options.Matchable ?? Path.Combine(data ?? ".", "keywords-matchable.json");
var matchable = File.Exists(matchablePath) ? Matchable.Read(matchablePath) : new Matchable(); // none yet: every extra keyword is a lint problem

if (options.Format is { } formatTo)
{
	// the shipped layout, optionally cut to the ids a file lists (one a line; # starts a comment)
	var keep = options.Keep is { } keepFile
		? File.ReadAllLines(keepFile).Select(x => x.Split('#')[0].Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal)
		: null;
	File.WriteAllText(formatTo, catalogue.Format(keep), new UTF8Encoding(false));
	Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {(keep is null ? catalogue.Categories.Count : catalogue.Categories.Count(c => keep.Contains(c.Id)))} categories"));
	return 0;
}

var problems = Lint.Check(catalogue, matchable);
if (options.Check)
{
	foreach (var problem in problems)
	{
		Console.WriteLine(problem);
	}

	var unused = Lint.Unused(catalogue, matchable).ToList();
	if (unused.Count > 0)
	{
		Console.WriteLine("matchable keywords no category uses: " + string.Join("; ", unused));
	}

	Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
		$"{catalogue.Categories.Count} categories ({catalogue.Categories.Count(x => x.Films)} films, {catalogue.Categories.Count(x => !x.Films)} shows), {matchable.Keywords.Count()} matchable keywords ({unused.Count} unused): {problems.Count} problems"));
	if (options.Index is null)
	{
		return problems.Count == 0 ? 0 : 1;
	}
}
else if (problems.Count > 0)
{
	Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"note: the catalogue has {problems.Count} lint problems (--check lists them)"));
}

if (options.Index is null)
{
	Console.Error.WriteLine("--index is needed for a census or --vocab");
	return 2;
}

var index = IndexFile.Read(options.Index);
Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"index revision {index.Revision}: {(index.Revision >= 4 ? "every matchable keyword kept" : "the allowlist and the six seasonal keywords kept")}"));
var census = Census.Of(index.Titles, index.Plays, catalogue, index.Kept);
Console.Write(Report.Table(census));
if (options.Json is { } json)
{
	File.WriteAllText(json, Report.Json(census));
}

if (options.Examples is { } examples)
{
	File.WriteAllText(examples, Report.Examples(census, 5), new UTF8Encoding(false));
	Console.Error.WriteLine("examples written to the file named (local only: never commit it)");
}

if (options.Names is { } names)
{
	File.WriteAllText(names, Report.Names(census, 5), new UTF8Encoding(false));
	Console.Error.WriteLine("names list written to the file named (local only: it holds titles; never commit it)");
}

return options.Check && problems.Count > 0 ? 1 : 0;

internal sealed record Options(string? Index, string? Catalogue, string? Matchable, bool Check, int? Vocab, string? Json, string? Examples)
{
	public string? Format { get; init; }

	public string? Keep { get; init; }

	public string? Names { get; init; }

	public string? Patch { get; init; }

	public static Options? Parse(string[] args)
	{
		string? index = null, catalogue = null, matchable = null, json = null, examples = null, format = null, keep = null, names = null, patch = null;
		int? vocab = null;
		var check = false;
		for (var i = 0; i < args.Length; i++)
		{
			string? Next() => i + 1 < args.Length ? args[++i] : null;
			switch (args[i])
			{
				case "--index": index = Next(); break;
				case "--catalogue": catalogue = Next(); break;
				case "--matchable": matchable = Next(); break;
				case "--json": json = Next(); break;
				case "--examples": examples = Next(); break;
				case "--format": format = Next(); break;
				case "--keep": keep = Next(); break;
				case "--names": names = Next(); break;
				case "--patch": patch = Next(); break;
				case "--check": check = true; break;
				case "--vocab" when i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var n):
					vocab = n;
					i++;
					break;
				case "--vocab": vocab = 60; break;
				default: return null;
			}
		}

		return new Options(index, catalogue, matchable, check, vocab, json, examples) { Format = format, Keep = keep, Names = names, Patch = patch };
	}

	/// <summary>The repository's Data folder, found from the working folder or the tool's own folder upwards; null when neither is in it.</summary>
	public static string? DataFolder()
	{
		foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
		{
			for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
			{
				var data = Path.Combine(dir.FullName, "ui", "FluentFin.Core", "SmartCollections", "Data");
				if (Directory.Exists(data))
				{
					return data;
				}
			}
		}

		return null;
	}
}

/// <summary>The census's words: a table and a JSON file of counts, and (only when asked, to a file) example titles.</summary>
internal static class Report
{
	public static string Table(Census census)
	{
		var text = new StringBuilder();
		var films = Census.CeilingFor(census.Films);
		var shows = Census.CeilingFor(census.Shows);
		text.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"census: {census.Films:N0} films, {census.Shows:N0} shows (copies are one); floor {Census.Floor} unwatched; ceilings: films {films}, shows {shows}"));
		text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{"id",-44} {"kind",-5} {"all",11} {"unwatched",11} {"verdict",-11} {"closest in its family",-52} {"parent",6} {"watched",7}"));
		foreach (var family in census.Pools.GroupBy(x => x.Category.Family))
		{
			text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"[{family.Key}]"));
			foreach (var pool in family)
			{
				var closest = census.Closest(pool) is { } c ? string.Create(CultureInfo.InvariantCulture, $"{c.Id} {c.Jaccard:0.00}") : "";
				var parent = census.ParentOverlap(pool) is { } p ? p.ToString("0.00", CultureInfo.InvariantCulture) : "";
				var watched = pool.Category.Films ? "" : pool.WatchedShows.ToString(CultureInfo.InvariantCulture);
				text.AppendLine(string.Create(CultureInfo.InvariantCulture,
					$"{pool.Category.Id,-44} {pool.Category.Kind,-5} {Range(pool.Least, pool.Most),11} {Range(pool.LeastUnwatched, pool.MostUnwatched),11} {Word(census.VerdictOf(pool)),-11} {closest,-52} {parent,6} {watched,7}"));
			}
		}

		var verdicts = census.Pools.GroupBy(x => (x.Category.Kind, Verdict: census.VerdictOf(x))).OrderBy(x => x.Key.Kind).ThenBy(x => x.Key.Verdict)
			.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Key.Kind} {Word(x.Key.Verdict)} {x.Count()}"));
		text.AppendLine("verdicts: " + string.Join("; ", verdicts));
		var fitting = census.Pools.Where(x => !x.Category.Films && census.VerdictOf(x) == Verdict.Fits).SelectMany(x => x.Sure).Select(x => x.Id).ToHashSet();
		text.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"watched shows in a fitting show category: {census.WatchedShows.Count(fitting.Contains)} of {census.WatchedShows.Count}"));
		return text.ToString();
	}

	public static string Json(Census census) => JsonSerializer.Serialize(census.Pools.Select(pool => new
	{
		id = pool.Category.Id,
		kind = pool.Category.Kind,
		family = pool.Category.Family,
		least = pool.Least,
		most = pool.Most,
		leastUnwatched = pool.LeastUnwatched,
		mostUnwatched = pool.MostUnwatched,
		verdict = Word(census.VerdictOf(pool)),
		closest = census.Closest(pool) is { } c ? new { id = c.Id, jaccard = Math.Round(c.Jaccard, 3) } : null,
		parent = census.ParentOverlap(pool) is { } p ? Math.Round(p, 3) : (double?)null,
		watchedShows = pool.WatchedShows,
	}), new JsonSerializerOptions { WriteIndented = true });

	public static string Examples(Census census, int count)
	{
		var text = new StringBuilder("# Example titles per category (local only: the owner's own library, never commit)\n\n");
		foreach (var pool in census.Pools.Where(x => x.Known && x.Least > 0))
		{
			var titles = census.Examples(pool, count).Select(Titled);
			text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {pool.Category.Id}: {string.Join("; ", titles)}"));
		}

		return text.ToString();
	}

	/// <summary>
	/// The names list for the owner's review (local only: it holds titles of their library): by kind, shows first, then by family;
	/// each category's name, reason line and pool, and up to <paramref name="count"/> example titles where the pool is known.
	/// </summary>
	public static string Names(Census census, int count)
	{
		var text = new StringBuilder();
		foreach (var kind in new[] { "shows", "films" })
		{
			var pools = census.Pools.Where(x => x.Category.Kind == kind).ToList();
			text.AppendLine(CultureInfo.InvariantCulture, $"## {(kind == "shows" ? "Shows" : "Films")} ({pools.Count})").AppendLine();
			foreach (var family in pools.GroupBy(x => x.Category.Family))
			{
				text.AppendLine(CultureInfo.InvariantCulture, $"### {Heading(family.Key)}").AppendLine();
				foreach (var pool in family)
				{
					text.AppendLine(CultureInfo.InvariantCulture, $"- **{pool.Category.Name}**. {pool.Category.Reason}. {PoolWords(census, pool)}");
					if (pool.Known && pool.Least > 0)
					{
						var titles = census.Examples(pool, count).Select(Titled);
						text.AppendLine(CultureInfo.InvariantCulture, $"  - {string.Join("; ", titles)}");
					}
				}

				text.AppendLine();
			}
		}

		return text.ToString();
	}

	private static string PoolWords(Census census, Pool pool)
	{
		var noun = pool.Category.Films ? "films" : "shows";
		if (pool.Known)
		{
			var watched = pool.Least - pool.LeastUnwatched;
			var shows = pool.Category.Films || pool.WatchedShows == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"; episodes played of {pool.WatchedShows}");
			return string.Create(CultureInfo.InvariantCulture, $"Pool: {pool.Least:N0} {noun}, {(watched == 0 ? "none watched" : $"{watched:N0} watched")}{shows}.");
		}

		var least = pool.Least > 0 ? string.Create(CultureInfo.InvariantCulture, $" (at least {pool.Least:N0} {noun} already)") : "";
		return pool.Category.Rule.TextAny.Length > 0
			? $"Pool unknown until the summary pass (step 1b){least}."
			: $"Pool unknown until the app's census{least}.";
	}

	// "Louie (2010)", once: a name that already ends with its year ("Louie (2010)" on the server) doesn't get it twice
	internal static string Titled(FluentFin.Core.SmartCollections.IndexEntry title)
	{
		if (title.Year is not { } year)
		{
			return title.Name;
		}

		var suffix = string.Create(CultureInfo.InvariantCulture, $"({year})");
		return title.Name.TrimEnd().EndsWith(suffix, StringComparison.Ordinal) ? title.Name.TrimEnd() : $"{title.Name} {suffix}";
	}

	private static string Heading(string family) => family.Length == 0 ? family : char.ToUpperInvariant(family[0]) + family[1..].Replace('-', ' ');

	public static string Word(Verdict verdict) => verdict switch
	{
		Verdict.Fits => "fits",
		Verdict.UnderFloor => "under-floor",
		Verdict.OverCeiling => "over-ceil",
		_ => "unknown",
	};

	private static string Range(int least, int most) => least == most ? least.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{least}-{most}");
}
