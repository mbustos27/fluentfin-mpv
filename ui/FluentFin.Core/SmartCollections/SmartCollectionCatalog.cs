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
	];

	/// <summary>
	/// Every collection, kind by kind in <see cref="Builders"/>' order, but those of the kinds turned off in Settings and the
	/// franchises an admin's box set already is (<paramref name="boxedFranchises"/>: their TMDb ids).
	/// </summary>
	public static IReadOnlyList<SmartCollection> BuildAll(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises = null, IReadOnlySet<SmartCollectionKind>? kindsOff = null)
	{
		var context = new BuildContext(index, now, boxedFranchises ?? new HashSet<string>());
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
	public static Task<IReadOnlyList<SmartCollection>> BuildAllAsync(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises = null, IReadOnlySet<SmartCollectionKind>? kindsOff = null) =>
		Task.Run(() => BuildAll(index, now, boxedFranchises, kindsOff));

	/// <summary>The collection of a kind a day for Home's rotating row: the same all day, the next one tomorrow; none when there are none.</summary>
	public static SmartCollection? OfTheDay(IReadOnlyList<SmartCollection> all, DateTimeOffset now)
	{
		var rotating = all.Where(x => SmartCollectionKinds.PageOrder.Contains(x.Kind)).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
		return rotating.Count == 0 ? null : rotating[(int)(now.Date.Ticks / TimeSpan.TicksPerDay % rotating.Count)];
	}
}
