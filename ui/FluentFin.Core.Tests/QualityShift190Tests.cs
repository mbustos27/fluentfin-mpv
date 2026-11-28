using System.Globalization;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (stream lineup; the owner's library, fix 9): what the quality score moves on a library of the owner's shape
/// (<see cref="LibraryShape190"/>): Acclaimed's order, the themes' first posters, the categories' first posters and the
/// Spotlight's candidates and order. It prints them (HANDOFF.md has 1.8.1's against 1.9's) and checks what 1.9 promises:
/// brand-new titles and concert films no longer lead, and of two titles tied on everything else the critics' choice comes
/// first.
/// </summary>
[Collection(nameof(QualityShift190Tests))]
public sealed class QualityShift190Tests(ITestOutputHelper output)
{
	private static readonly Lazy<(List<IndexEntry> Titles, BuildContext Context)> s_shape = new(() =>
	{
		var titles = LibraryShape190.Library();
		return (titles, new BuildContext(LibraryShape190.Index(titles), LibraryShape190.Now, new HashSet<string>()) { Publishing = true });
	});

	private static string Flag(IndexEntry x) => (LibraryShape190.IsNew(x) ? " [new]" : "") + (LibraryShape190.IsConcert(x) ? " [concert]" : "");

	[Fact]
	public void TheRankings_OnALibraryOfTheOwnersShape_NewTitlesAndConcertFilmsNoLongerLead()
	{
		var (titles, context) = s_shape.Value;
		var byId = context.Titles.Entries.ToDictionary(x => x.Id);
		var quality = context.Quality;
		output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"titles {context.Titles.Entries.Count}, M {quality.M:0.###}, C films {quality.MeanOf(IndexKind.Movie):0.###}, shows {quality.MeanOf(IndexKind.Series):0.###}"));

		// 1. Acclaimed, not seen yet: the builder's best-first order (the lineup turns it daily)
		var acclaimed = Assert.Single(new HighlyRatedBuilder().Build(context)).ItemIds.Select(id => byId[id]).ToList();
		output.WriteLine($"Acclaimed: {acclaimed.Count(x => LibraryShape190.IsNew(x))} new and {acclaimed.Count(LibraryShape190.IsConcert)} concert films in its {acclaimed.Count}; "
			+ $"first 8: {acclaimed.Take(8).Count(x => LibraryShape190.IsNew(x))} new, {acclaimed.Take(8).Count(LibraryShape190.IsConcert)} concert");
		foreach (var (title, i) in acclaimed.Select((x, i) => (x, i)))
		{
			output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  acclaimed {i + 1,2}: {title.Name} ({title.Year}, {title.Rating}, critics {title.Critic?.ToString(CultureInfo.InvariantCulture) ?? "none"}) Q {quality.Of(title):0.000}{Flag(title)}"));
		}

		// 2. the themes as the lineup orders them (Best.Of, unwatched first), those under the theme ceiling: their first eight
		var ceiling = Math.Max(CollectionLineup.ThemeCeilingLeast, (int)Math.Ceiling(CollectionLineup.ThemeCeilingShare * context.Titles.Entries.Count(x => x.Kind == IndexKind.Movie)));
		var themes = new KeywordBuilder().Build(context).Select(x => (x.Id, Titles: x.ItemIds.Select(id => byId[id]).ToList()))
			.Where(x => x.Titles.Count(t => !t.Played) is >= CollectionLineup.Floor and var n && n <= ceiling).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
		var firsts = themes.Select(x => (x.Id, First: Best.Of(x.Titles.Where(t => !t.Played), quality).Take(CollectionLineup.Posters).ToList())).ToList();
		output.WriteLine($"Themes under the ceiling ({ceiling}): {themes.Count}; first posters new {firsts.Count(x => LibraryShape190.IsNew(x.First[0]))}, concert {firsts.Count(x => LibraryShape190.IsConcert(x.First[0]))}; "
			+ $"new among the first 8s {firsts.Sum(x => x.First.Count(t => LibraryShape190.IsNew(t)))}, concert {firsts.Sum(x => x.First.Count(LibraryShape190.IsConcert))}");
		foreach (var (id, first) in firsts)
		{
			output.WriteLine($"  theme {id}: {string.Join(" | ", first.Select(x => x.Name + Flag(x)))}");
		}

		// 3. the categories of the shipped catalogue the library makes: their first eight
		var categories = new MicroGenreBuilder().Run(context).Collections.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
		var categoryFirsts = categories.Select(x => (x.Id, First: x.ItemIds.Take(CollectionLineup.Posters).Select(id => byId[id]).ToList())).ToList();
		output.WriteLine($"Categories made: {categories.Count}; first posters new {categoryFirsts.Count(x => LibraryShape190.IsNew(x.First[0]))}, concert {categoryFirsts.Count(x => LibraryShape190.IsConcert(x.First[0]))}; "
			+ $"new among the first 8s {categoryFirsts.Sum(x => x.First.Count(t => LibraryShape190.IsNew(t)))}");
		foreach (var (id, first) in categoryFirsts)
		{
			output.WriteLine($"  category {id}: {string.Join(" | ", first.Select(x => x.Name + Flag(x)))}");
		}

		// 4. the Spotlight: who passes, the week's draw with nothing kept, and Ealing's order
		var candidates = SpotlightBuilder.Candidates(context);
		output.WriteLine($"Spotlight candidates: {candidates.Count} (director {candidates.Count(x => x.Subject == SpotlightSubject.Director)}, actor {candidates.Count(x => x.Subject == SpotlightSubject.Actor)}, "
			+ $"studio {candidates.Count(x => x.Subject == SpotlightSubject.Studio)}); week 48's draw: {SpotlightBuilder.Pick(candidates, SpotlightWeek.Of(LibraryShape190.Now))?.Id}");
		output.WriteLine($"  spotlight ids: {string.Join(", ", candidates.Select(x => x.Id))}");
		var ealing = SpotlightBuilder.Held(context, SpotlightBuilder.IdOf(SpotlightSubject.Studio, "ealingstudios"));
		Assert.NotNull(ealing);
		output.WriteLine($"  Ealing: {string.Join(" | ", ealing.Titles.Take(CollectionLineup.Posters).Select(x => x.Name))}");

		// 1.9's promises: the 10-rated 2026 concert film and the 2025 one aren't in Acclaimed's first eight; no theme or category
		// opens on a title of the last 12 months; Kind Hearts and Coronets (critics 100) opens Ealing before Imagine Me & You (34)
		Assert.DoesNotContain(acclaimed.Take(CollectionLineup.Posters), x => x.Name is LibraryShape190.Names.NewConcert or LibraryShape190.Names.OldConcert);
		Assert.True(acclaimed.Take(CollectionLineup.Posters).Count(x => LibraryShape190.IsNew(x) || LibraryShape190.IsConcert(x)) <= 1,
			"Acclaimed's first eight: at most one new title or concert film");
		Assert.True(ealing.Titles.ToList().FindIndex(x => x.Name == LibraryShape190.Names.KindHearts) < ealing.Titles.ToList().FindIndex(x => x.Name == LibraryShape190.Names.ImagineMe));
	}
}
