using System.Globalization;
using System.Text;
using FluentFin.Core.SmartCollections;

namespace Nixie.Collections;

/// <summary>
/// The index's vocabulary per kind, for writing rules: genres, countries, studios or networks, official ratings, kept
/// keywords, decades and lengths, each with its count. Public category names and counts only: never a title, an id or a person.
/// </summary>
public static class Vocab
{
	public static string Of(CanonicalTitles titles, int top)
	{
		var text = new StringBuilder();
		foreach (var kind in new[] { IndexKind.Movie, IndexKind.Series })
		{
			var of = titles.Entries.Where(x => x.Kind == kind).ToList();
			text.AppendLine(CultureInfo.InvariantCulture, $"== {(kind == IndexKind.Movie ? "films" : "shows")}: {of.Count}");
			Counts(text, "genres", of.SelectMany(x => x.Genres), int.MaxValue);
			Counts(text, "genre pairs", of.SelectMany(x => Pairs(x.Genres)), top);
			Counts(text, "countries (any)", of.SelectMany(x => x.Countries), top);
			Counts(text, "studios/networks", of.SelectMany(x => x.Studios), top);
			Counts(text, "official ratings", of.Select(x => x.OfficialRating ?? "(none)"), top);
			Counts(text, "kept keywords", of.SelectMany(x => x.Tags), int.MaxValue);
			Counts(text, "decades", of.Select(x => x.Year is { } y ? $"{y / 10 * 10}s" : "(none)"), int.MaxValue);
			Counts(text, "length", of.Select(x => Rules.Minutes(x) switch
			{
				null => "(none)",
				<= 15 => "≤15",
				<= 25 => "16-25",
				<= 35 => "26-35",
				<= 50 => "36-50",
				<= 65 => "51-65",
				<= 89 => "66-89",
				<= 120 => "90-120",
				<= 150 => "121-150",
				_ => ">150",
			}), int.MaxValue);
			Counts(text, "community rating", of.Select(x => x.Rating is > 0 and var r ? $"{Math.Floor(r)}" : "(none)"), int.MaxValue);
			Counts(text, "critics' rating", of.Select(x => x.Critic is > 0 and var c ? $"{Math.Floor(c / 10) * 10}" : "(none)"), int.MaxValue);
		}

		return text.ToString();
	}

	private static IEnumerable<string> Pairs(string[] genres)
	{
		var g = genres.Order(StringComparer.OrdinalIgnoreCase).ToArray();
		for (var i = 0; i < g.Length; i++)
		{
			for (var j = i + 1; j < g.Length; j++)
			{
				yield return $"{g[i]} + {g[j]}";
			}
		}
	}

	private static void Counts(StringBuilder text, string what, IEnumerable<string> values, int top)
	{
		var counts = values.GroupBy(x => x.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => (Name: g.Key, Count: g.Count()))
			.OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
		text.AppendLine(CultureInfo.InvariantCulture, $"-- {what} ({counts.Count} distinct)");
		text.AppendLine(string.Join("; ", counts.Take(top).Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Name} {x.Count}"))));
	}
}
