using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6: every Made by Nixie collection of an index, one builder per kind (the plan's 1.3). Pure and quick (the
/// 1,334-film budget test): the pages call it off the UI thread with the index they have.
/// </summary>
public static class SmartCollectionCatalog
{
	public static IReadOnlyList<ISmartCollectionBuilder> Builders { get; } =
	[
		new FranchiseBuilder(), new DecadeBuilder(), new DirectorBuilder(), new ActorBuilder(), new StudioBuilder(IndexKind.Movie),
		new StudioBuilder(IndexKind.Series), new GenreMixBuilder(), new KeywordBuilder(), new CountryBuilder(), new HighlyRatedBuilder(),
		new UnfinishedSeriesBuilder(), new SeasonalBuilder(), new ShortFilmsBuilder(),
		new AwardBuilder(), // 1.7 keyless: Wikidata's awards (nothing without its lists)
	];

	/// <summary>
	/// Every collection, kind by kind in <see cref="Builders"/>' order, but those of the kinds turned off in Settings and the
	/// franchises an admin's box set already is (<paramref name="boxedFranchises"/>: their TMDb ids).
	/// </summary>
	/// 1.7 keyless: with the outside lists of <paramref name="outside"/>, else the app's (<see cref="Outside.OutsideListsSnapshot.Shared"/>).
	public static IReadOnlyList<SmartCollection> BuildAll(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises = null, IReadOnlySet<SmartCollectionKind>? kindsOff = null,
		Outside.OutsideListsSnapshot? outside = null)
	{
		var context = new BuildContext(index, now, boxedFranchises ?? new HashSet<string>()) { Outside = outside ?? Outside.OutsideListsSnapshot.Shared };
		var all = new List<SmartCollection>();
		foreach (var builder in Builders)
		{
			if (kindsOff?.Contains(builder.Kind) == true)
			{
				continue;
			}

			all.AddRange(builder.Build(context).Where(x => x.ItemIds.Count > 0));
		}

		return all;
	}

	/// <summary>
	/// <see cref="BuildAll"/> off the calling thread: a 25,000-film index takes a moment to group, and a page must not wait
	/// for it on the UI thread.
	/// </summary>
	public static Task<IReadOnlyList<SmartCollection>> BuildAllAsync(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises = null, IReadOnlySet<SmartCollectionKind>? kindsOff = null,
		Outside.OutsideListsSnapshot? outside = null)
	{
		var lists = outside ?? Outside.OutsideListsSnapshot.Shared; // 1.7 keyless: the lists as they are now, not when the task runs
		return Task.Run(() => BuildAll(index, now, boxedFranchises, kindsOff, lists));
	}

	/// <summary>
	/// Nixie 1.6.1 (bug hunt BH-160-m1): the clock every page builds Nixie's collections by, the PC's local time: the seasonal
	/// row's month and the day's row (<see cref="OfTheDay"/>) follow the date on the user's clock. Home and the Collections
	/// page went by UTC, so in California the Halloween row left them at 5 PM on October 31, while the Libraries page (local)
	/// still counted it, and the day's row changed in the afternoon.
	/// </summary>
	public static DateTimeOffset Now(TimeProvider? time = null) => (time ?? TimeProvider.System).GetLocalNow();

	/// <summary>The collection of a kind a day for Home's rotating row: the same all day, the next one tomorrow; none when there are none.</summary>
	public static SmartCollection? OfTheDay(IReadOnlyList<SmartCollection> all, DateTimeOffset now)
	{
		var rotating = all.Where(x => SmartCollectionKinds.PageOrder.Contains(x.Kind)).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
		return rotating.Count == 0 ? null : rotating[(int)(now.Date.Ticks / TimeSpan.TicksPerDay % rotating.Count)];
	}
}
