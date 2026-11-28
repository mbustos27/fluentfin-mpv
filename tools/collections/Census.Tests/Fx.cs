using FluentFin.Core.SmartCollections;
using Nixie.Collections;

namespace Census.Tests;

/// <summary>Fixtures: index entries and categories made in a line.</summary>
internal static class Fx
{
	private static int s_tmdb = 1000;

	public static IndexEntry Film(string name = "A film", int? year = 2000, string[]? genres = null, string[]? tags = null, int? tagCount = null,
		string[]? countries = null, string[]? studios = null, double? minutes = 100, double? rating = 6.5, float? critic = null, bool played = false,
		string? tmdb = null) =>
		Entry(IndexKind.Movie, name, year, genres, tags, tagCount, countries, studios, minutes, rating, critic, played, tmdb);

	public static IndexEntry Show(string name = "A show", int? year = 2010, string[]? genres = null, string[]? studios = null, double? minutes = 22,
		double? rating = 7.5, bool played = false, string? tmdb = null) =>
		Entry(IndexKind.Series, name, year, genres, null, 0, null, studios, minutes, rating, null, played, tmdb);

	private static IndexEntry Entry(IndexKind kind, string name, int? year, string[]? genres, string[]? tags, int? tagCount, string[]? countries,
		string[]? studios, double? minutes, double? rating, float? critic, bool played, string? tmdb) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Kind = kind,
		Year = year,
		Genres = genres ?? [],
		Tags = tags ?? [],
		TagCount = tagCount ?? tags?.Length ?? 0,
		Countries = countries ?? [],
		Studios = studios ?? [],
		RunTimeTicks = minutes is { } m ? (long)(m * TimeSpan.TicksPerMinute) : null,
		Rating = rating,
		Critic = critic,
		Played = played,
		Tmdb = tmdb ?? Interlocked.Increment(ref s_tmdb).ToString(System.Globalization.CultureInfo.InvariantCulture),
	};

	public static Category Cat(Rule rule, string id = "a-category", string kind = "films", string family = "a-family", string? name = null,
		string? parent = null, string reason = "Facts of the titles") => new()
	{
		Id = id,
		Name = name ?? "Crime Dramas from the 1970s",
		Kind = kind,
		Family = family,
		Parent = parent,
		Rule = rule,
		Reason = reason,
	};

	public static Truth Match(Rule rule, IndexEntry title, string kind = "films") => Rules.Match(Cat(rule, kind: kind), title);

	public static Nixie.Collections.Census CensusOf(IReadOnlyList<IndexEntry> titles, IEnumerable<Category> categories, IReadOnlyDictionary<Guid, SeriesPlays>? plays = null) =>
		Nixie.Collections.Census.Of(CanonicalTitles.Of(titles), plays, new Catalogue { Version = 1, Categories = [.. categories] });

	/// <summary>The repository's SmartCollections\Data folder and tools\collections folder.</summary>
	public static string Data => Options.DataFolder() ?? throw new InvalidOperationException("no Data folder above the tests");

	public static string Tools => Path.GetFullPath(Path.Combine(Data, "..", "..", "..", "..", "tools", "collections"));
}
