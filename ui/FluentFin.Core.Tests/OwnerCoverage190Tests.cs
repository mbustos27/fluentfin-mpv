using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Phase 3, stream anchors): the bar on a library of the owner's shape (<see cref="OwnerShape190"/>): how many of
/// the 87 shows with an episode played land in a category the Collections page shows (the Categories row; This week's are
/// among them). 1.9 before Phase 3: 27 on the owner's own library (the report, after fix B), 31 on this fixture, where 53 are
/// in some made category. The bar: clearly more; the aim 45.
/// </summary>
[Collection(CollectionNamesSerial.Name)] // the builders read TextMatches.Shared, which is static
public sealed class OwnerCoverage190Tests(ITestOutputHelper output)
{
	/// <summary>What 1.9's row held here before Phase 3 (the measurement at 391ffc56).</summary>
	private const int Before = 31;

	// the Phase 3 review's privacy fix (item 9): the fixture stands in for the owner's played shows by shape only. Exact episode
	// counts, networks and years made real shows recognisable in a public repository: counts of ten or more are rounded to
	// fives, and the shows whose categories come from their summaries, or that no category holds, carry no network
	[Fact]
	public void TheOwnerShapedFixture_KeepsTheShapeOnly()
	{
		Assert.All(OwnerShape190.PlayedShows, x =>
		{
			Assert.True(x.Episodes < 10 || x.Episodes % 5 == 0, $"{x.Name}: {x.Episodes} episodes");
			Assert.True(x.Left < 10 || x.Left % 5 == 0, $"{x.Name}: {x.Left} left");
			Assert.Matches(@"^Played \d\d$", x.Name);
		});
		Assert.All(OwnerShape190.PlayedShows.Where(x => x.Group is OwnerShape190.Group.Shown or OwnerShape190.Group.Missed), x => Assert.Empty(x.Studios));
	}

	// the Phase 3 review's Major: every taste category stayed, so over 10 weeks of the same plays 10 to 11 of the row's 12
	// tiles were the same each week. At most CarriedMost are carried over (one played lately first); the rest rotate
	[Fact]
	public void OverTenWeeksOfTheSamePlays_AtMostSixCarryOver_TheRestOfTheRowRotates()
	{
		var library = OwnerShape190.Make();
		TextMatches.Put(library.Text);
		try
		{
			var history = new Dictionary<string, CategoryWeek>(StringComparer.Ordinal);
			var rows = new List<List<string>>();
			List<string>? before = null;
			IReadOnlyList<string> carriedBefore = [];
			var covered = new List<int>();
			for (var w = 0; w < 10; w++)
			{
				var now = OwnerShape190.Now.AddDays(7 * w);
				var made = CollectionLineup.Build(library.Index, now, null, null, [], FluentFin.Core.Outside.OutsideListsSnapshot.Empty, history: history);
				var row = made.Lineup.Categories.Select(x => x.Id).ToList();
				var carried = made.Lineup.CarriedCategories;
				covered.Add(OwnerShape190.Covered(library, made.Lineup.Categories).Count);
				var others = row.Where(x => !carried.Contains(x)).ToList();
				var changed = before is null ? others.Count : others.Count(x => !before.Contains(x));
				output.WriteLine($"week {w}: carried {carried.Count}, others {others.Count}, changed {changed}, covered {covered[^1]}; {string.Join(" | ", row)}");

				Assert.Equal(CollectionLineup.CategoryRow, row.Count);
				Assert.True(carried.Count <= CollectionLineup.CarriedMost, $"week {w}: {carried.Count} carried");
				Assert.All(carried, x => Assert.Contains(x, rows.TakeLast(CollectionLineup.CategoryWeeksApart - 1).SelectMany(r => r))); // carried: shown in the three weeks before
				if (before is not null)
				{
					Assert.True(changed * 2 >= others.Count, $"week {w}: {changed} of the {others.Count} not carried changed");
					// the carried ones stay: last week's carried still in the row, but one at most (a title's 30 days ending)
					Assert.True(carriedBefore.Count(row.Contains) >= carriedBefore.Count - 1, $"week {w}: of last week's {carriedBefore.Count} carried, {carriedBefore.Count(row.Contains)} stayed");
				}

				history[SpotlightWeek.Of(now).Seed] = made.Lineup.CategoryWeek!;
				rows.Add(row);
				before = row;
				carriedBefore = carried;
			}

			output.WriteLine($"covered: {string.Join(", ", covered)}");
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}
	}

	[Fact]
	public void TheOwnersPlayedShows_LandInTheShownCategories_MoreThanBefore()
	{
		var library = OwnerShape190.Make();
		TextMatches.Put(library.Text);
		try
		{
			var watch = System.Diagnostics.Stopwatch.StartNew();
			var made = CollectionLineup.Build(library.Index, OwnerShape190.Now, null, null, [], FluentFin.Core.Outside.OutsideListsSnapshot.Empty);
			var took = watch.ElapsedMilliseconds;
			var categories = made.All.Where(x => x.Kind == SmartCollectionKind.MicroGenre).ToList();
			var row = made.Lineup.Categories;
			var covered = OwnerShape190.Covered(library, row);
			var anyMade = OwnerShape190.Covered(library, categories);
			output.WriteLine($"built in {took} ms; {categories.Count} categories made, {row.Count} in the row");
			foreach (var category in row)
			{
				output.WriteLine($"  {category.Title} ({category.ItemIds.Count}): {category.ReasonText} [{string.Join(", ", OwnerShape190.Covered(library, [category]))}]");
			}

			foreach (var pick in made.Lineup.Picks)
			{
				output.WriteLine($"  this week: {pick.Slot} {pick.Collection.Title}");
			}

			output.WriteLine($"  the More like row: {string.Join(", ", made.Lineup.MoreLike.Select(id => made.All.First(x => x.Id == id).Title))}");
			foreach (var group in Enum.GetValues<OwnerShape190.Group>())
			{
				var names = OwnerShape190.PlayedShows.Where(x => x.Group == group).Select(x => x.Name).ToList();
				output.WriteLine($"{group}: {names.Count(covered.Contains)} of {names.Count} shown, {names.Count(anyMade.Contains)} in a made category");
			}

			var lately = OwnerShape190.PlayedShows.Where(x => x.DaysAgo < 30).Select(x => x.Name).ToList();
			output.WriteLine($"shown: {covered.Count} of {library.Played.Count} (before Phase 3: {Before}); played in the last 30 days: {lately.Count(covered.Contains)} of {lately.Count}; in a made category: {anyMade.Count}");
			foreach (var k in new[] { 12, 18, 24 })
			{
				output.WriteLine($"  the most any {k} categories here could hold (greedy): {MostHeld(library, categories, k)}");
			}

			// next week, with this week kept, a week of plays on
			var week = new Dictionary<string, CategoryWeek>(StringComparer.Ordinal) { [SpotlightWeek.Of(OwnerShape190.Now).Seed] = made.Lineup.CategoryWeek! };
			var next = CollectionLineup.Build(library.Index, OwnerShape190.Now.AddDays(7), null, null, [], FluentFin.Core.Outside.OutsideListsSnapshot.Empty, history: week);
			var nextCovered = OwnerShape190.Covered(library, next.Lineup.Categories).Count;
			output.WriteLine($"next week: {nextCovered} shown; {string.Join(", ", next.Lineup.Categories.Select(x => x.Title))}");

			Assert.Equal(87, library.Played.Count);
			Assert.True(covered.Count > Before + 2, $"{covered.Count} of the 87 played shows in a shown category (before: {Before})");
			// the week after carries over six categories of their taste (CollectionLineup.CarriedMost; the Phase 3 review's Major):
			// 30 here, where every one carried (a row frozen for months) kept 34 and none carried 29
			Assert.True(nextCovered >= Before - 2, $"next week: {nextCovered} of the 87 (this week: {covered.Count})");
			// R3: a category of filters alone in the row holds two or more played titles
			var playedIds = library.Played.Values.Select(x => x.Id).Concat(library.PlayedFilms.Select(x => x.Id)).ToHashSet();
			foreach (var category in row.Where(x => CollectionLineup.FiltersOnly(MicroGenres.Shipped.Of(x.Id))))
			{
				Assert.True(category.ItemIds.Count(playedIds.Contains) >= CollectionLineup.FilteredLeast, $"{category.Title} holds fewer than two played titles");
			}

			// R2: each row tile holding a played show names one, a played one: first, or second after "From each title's summary"
			// (the Phase 3 review: kept first, so a narrow tile still says it)
			foreach (var category in row.Where(x => OwnerShape190.Covered(library, [x]).Count > 0))
			{
				var facts = category.Reason!.Facts;
				var named = facts[0] == TextPass.ReasonFact ? facts[1] : facts[0];
				Assert.StartsWith("With “", named, StringComparison.Ordinal);
				Assert.Contains(OwnerShape190.Covered(library, [category]), x => named.Contains(x, StringComparison.Ordinal));
			}

			// R1: More like for the titles played lately, the newest first
			var likes = made.Lineup.MoreLike.Select(id => made.All.First(x => x.Id == id)).ToList();
			Assert.InRange(likes.Count, 4, 6);
			Assert.Equal(likes.OrderByDescending(x => x.AnchoredAt).Select(x => x.Id), likes.Select(x => x.Id));
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}
	}

	// the played shows the best k categories made here could hold between them (a greedy cover: what a longer row would show)
	private static int MostHeld(OwnerShape190.Library library, IReadOnlyList<SmartCollection> categories, int k)
	{
		var played = library.Played.Values.Select(x => x.Id).ToHashSet();
		var sets = categories.Select(x => x.ItemIds.Where(played.Contains).ToHashSet()).Where(x => x.Count > 0).ToList();
		var covered = new HashSet<Guid>();
		for (var i = 0; i < k && sets.Count > 0; i++)
		{
			var best = sets.MaxBy(x => x.Count(id => !covered.Contains(id)))!;
			covered.UnionWith(best);
			sets.Remove(best);
		}

		return covered.Count;
	}
}
