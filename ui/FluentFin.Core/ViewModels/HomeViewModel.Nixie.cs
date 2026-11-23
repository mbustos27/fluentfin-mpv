using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>Nixie 1.6: one Made by Nixie row on Home: the collection, its first titles, and its "See all".</summary>
public sealed partial class NixieHomeRowViewModel(SmartCollection collection) : ObservableObject
{
	public SmartCollection Collection { get; } = collection;

	/// <summary>The row's title: the collection's, with what it is when the name alone wouldn't say ("With Clara Quill").</summary>
	/// 1.7.3 (P0-4): the two picks renamed say their new names from Home's saved page too, which keeps 1.7.2's titles
	/// ("Highly rated, not watched") until the index's rows replace it.
	public string Title => Collection.Kind switch
	{
		SmartCollectionKind.Actor => $"With {Collection.Title}",
		SmartCollectionKind.Director => $"Directed by {Collection.Title}",
		SmartCollectionKind.Studio or SmartCollectionKind.Network => $"From {Collection.Title}",
		SmartCollectionKind.HighlyRated or SmartCollectionKind.ShortFilms => SmartCollectionKinds.Plural(Collection.Kind), // 1.7.3
		_ => Collection.Title,
	};

	/// <summary>The small kicker over the title (Classic, Cinematic), Drive-in's sign's subtitle, Editorial's caption.</summary>
	public string Kicker => "Made by Nixie";

	/// <summary>Drive-in's bulb sign: the title in capitals ("HIGHLY RATED, NOT WATCHED").</summary>
	public string SignTitle => Title.ToUpperInvariant();

	[ObservableProperty]
	public partial ObservableCollection<BaseItemViewModel> Items { get; set; } = [];
}

/// <summary>
/// Nixie 1.6 (the owner's decision, plan 4.2): Home's Made by Nixie rows, after "New in …": "Acclaimed, not seen yet" (1.7.3;
/// was "Highly rated, not watched") and "Unfinished series" on by default, a seasonal row in season, and a franchise, keyword
/// or award collection a day (1.7.3); each can be turned off in Settings → Collections. They are in Home's snapshot
/// (<see cref="HomeCache"/>), so Home paints them at once; the library index, read in the background on a first visit, fills
/// them in when it is in.
/// </summary>
public partial class HomeViewModel
{
	/// <summary>How many titles a Made by Nixie row shows.</summary>
	public const int NixieRowLength = 20;

	public ObservableCollection<NixieHomeRowViewModel> NixieRows { get; } = [];

	/// <summary>
	/// Nixie 1.6.1: whether a saved row of Nixie's is shown under the Settings switches now: its kind on, and its Home row on
	/// (Acclaimed, Unfinished series and the seasonal row are rows of their own; any other kind is the collection a day).
	/// </summary>
	/// 1.7.3 (P0-3): and its kind still published (or a trending list's): a page saved by 1.7.2 with a director as the
	/// collection of the day painted it until the index's rows came.
	internal static bool NixieRowShown(SmartCollection collection, IReadOnlySet<NixieHomeRow> rowsOff, IReadOnlySet<SmartCollectionKind> kindsOff) =>
		(SmartCollectionCatalog.Published.Contains(collection.Kind) || Outside.TrendingBuilder.PageOrder.Contains(collection.Kind))
		&& !kindsOff.Contains(collection.Kind) && !rowsOff.Contains(collection.Kind switch
		{
			SmartCollectionKind.HighlyRated => NixieHomeRow.HighlyRated,
			SmartCollectionKind.UnfinishedSeries => NixieHomeRow.UnfinishedSeries,
			SmartCollectionKind.Seasonal => NixieHomeRow.Seasonal,
			_ => NixieHomeRow.Rotating,
		});

	/// <summary>The rows Home shows of these collections, in order, under the Settings switches.</summary>
	public static IReadOnlyList<SmartCollection> PickNixieRows(IReadOnlyList<SmartCollection> all, IReadOnlySet<NixieHomeRow> off, DateTimeOffset now)
	{
		var rows = new List<SmartCollection>();
		void Add(NixieHomeRow row, SmartCollection? collection)
		{
			if (!off.Contains(row) && collection is not null)
			{
				rows.Add(collection);
			}
		}

		Add(NixieHomeRow.HighlyRated, all.FirstOrDefault(x => x.Kind == SmartCollectionKind.HighlyRated));
		Add(NixieHomeRow.UnfinishedSeries, all.FirstOrDefault(x => x.Kind == SmartCollectionKind.UnfinishedSeries));
		Add(NixieHomeRow.Seasonal, all.FirstOrDefault(x => x.Kind == SmartCollectionKind.Seasonal));
		Add(NixieHomeRow.Rotating, SmartCollectionCatalog.OfTheDay(all, now));
		return rows;
	}

	private void ShowNixieRows(IReadOnlyList<HomeSnapshotNixieRow> rows)
	{
		NixieRows.Clear();
		// 1.6.1 (the 1.6 review): the saved page painted the rows Settings had turned off since it was saved, until the index's
		// rows replaced them; it shows today's switches at once
		var rowsOff = CollectionSettings.HomeRowsOff(localSettings);
		var kindsOff = CollectionSettings.KindsOff(localSettings);
		foreach (var row in rows.Where(r => NixieRowShown(r.Collection, rowsOff, kindsOff)))
		{
			NixieRows.Add(new NixieHomeRowViewModel(row.Collection) { Items = [.. row.Items.Select(BaseItemViewModel.FromDto)] });
		}
	}

	private IReadOnlyList<HomeSnapshotNixieRow> NixieSnapshot() =>
		[.. NixieRows.Where(x => x.Items.Count > 0).Select(x => new HomeSnapshotNixieRow(x.Collection, [.. x.Items.Select(i => i.Dto)]))];

	/// <summary>The rows from the library index: at once when it is in memory; else when its first read is in (then Home is saved again).</summary>
	/// <remarks>Never throws: Nixie's rows are extra; whatever goes wrong with them leaves the rest of Home as it loaded.</remarks>
	private async Task UpdateNixieRows()
	{
		try
		{
			// 1.7.3 (the owner's answer 2): at start (Home is the first page), the pins and saved filters of the kinds that went are
			// removed, in the background; the Collections page waits for it before it lists them
			_ = RetiredCollections.ForgetAsync(SmartCollectionServices.Store, SmartCollectionCatalog.Log);
			if (SmartCollectionServices.Index is not { } index)
			{
				return;
			}

			if (index.Current is { } snapshot)
			{
				await FillNixieRows(snapshot);
				_ = index.GetAsync(); // stale (a day old, a library change, R): read again in the background, for the next visit
				return;
			}

			_ = FillNixieRowsLater(index);
		}
		catch (Exception)
		{
			// the rows Home showed stay (the saved page's, or none)
		}
	}

	private async Task FillNixieRowsLater(LibraryIndex index)
	{
		try
		{
			if (await index.GetAsync() is { } snapshot && _shown)
			{
				await FillNixieRows(snapshot);
				await SaveShown();
			}
		}
		catch (Exception)
		{
			// as UpdateNixieRows: Home stays as it is
		}
	}

	private async Task FillNixieRows(IndexSnapshot snapshot)
	{
		var boxed = (await JellyfinClient.GetBoxSets())?.Items?.Select(x => IndexEntry.ProviderId(x, "Tmdb")).OfType<string>().ToHashSet() ?? [];
		var now = SmartCollectionCatalog.Now(_time); // 1.6.1 (BH-160-m1): the local date
		var all = await SmartCollectionCatalog.BuildAllAsync(snapshot, now, boxed, CollectionSettings.KindsOff(localSettings));
		var picked = PickNixieRows(all, CollectionSettings.HomeRowsOff(localSettings), now);
		// 1.7 trending: "Trending in your library" (Trakt and TMDb merged), only while a source is on and it has titles;
		// the lists come from the cache at once, and the ones that aren't fresh are asked again in the background
		if (Outside.TrendingServices.Collections is { } trending)
		{
			WatchTrending(trending);
			// matched off the UI thread (a big index); 1.7.3: in a catch of its own, as each kind's builder is (a throw there left
			// Home without any Made by Nixie row)
			if (await TrendingMatch.Safely(() => trending.HomeRowFor(snapshot), null, SmartCollectionCatalog.Log) is { } trendingRow)
			{
				picked = [.. picked, trendingRow];
			}

			_ = trending.RefreshAsync();
		}
		// end 1.7 trending
		var rows = new List<NixieHomeRowViewModel>();
		foreach (var collection in picked)
		{
			var first = collection.ItemIds.Take(NixieRowLength).ToList();
			// the server's records (fresh watch state); the index's own when it doesn't answer
			var items = await JellyfinClient.GetItemsByIds(first)
				?? [.. first.Select(id => snapshot.Entries.FirstOrDefault(x => x.Id == id)).OfType<IndexEntry>().Select(x => x.ToDto())];
			if (items.Count > 0)
			{
				rows.Add(new NixieHomeRowViewModel(collection) { Items = [.. items.Select(BaseItemViewModel.FromDto)] });
			}
		}

		// the same rows with the same titles keep their cards (a refresh that changes nothing redraws nothing)
		// 1.7 trending: the same collection too (its why line, its whole list): a trending row whose first titles stayed while
		// its sources changed is made again, so See all and its why line don't name a source that is off (the review, #6)
		if (rows.Select(NixieRowKey).SequenceEqual(NixieRows.Select(NixieRowKey)))
		{
			for (var i = 0; i < rows.Count; i++)
			{
				KeepCards(NixieRows[i].Items, [.. rows[i].Items.Select(x => x.Dto)]);
			}

			return;
		}

		NixieRows.Clear();
		foreach (var row in rows)
		{
			NixieRows.Add(row);
		}
	}

	// 1.7 trending: new lists (or a source turned on or off) while Home shows: its Nixie rows again, on Home's own thread. The
	// app's trending collections live as long as the app and Home's view model doesn't: they hold it only weakly.
	private bool _watchingTrending;

	private void WatchTrending(Outside.TrendingCollections trending)
	{
		if (_watchingTrending)
		{
			return;
		}

		_watchingTrending = true;
		var ui = SynchronizationContext.Current;
		var home = new WeakReference<HomeViewModel>(this);
		void OnChanged()
		{
			if (!home.TryGetTarget(out var target))
			{
				trending.Changed -= OnChanged;
				return;
			}

			if (ui is null)
			{
				_ = target.TrendingChanged();
			}
			else
			{
				ui.Post(_ => _ = target.TrendingChanged(), null);
			}
		}

		trending.Changed += OnChanged;
	}

	private async Task TrendingChanged()
	{
		try
		{
			if (_shown && SmartCollectionServices.Index?.Current is { } snapshot)
			{
				await FillNixieRows(snapshot);
				await SaveShown();
			}
		}
		catch (Exception)
		{
			// as UpdateNixieRows: Home stays as it is
		}
	}
	// end 1.7 trending

	// 1.7 trending: what makes two Home rows the same: the collection (its id, title, why line and every title on it) and the cards
	private static string NixieRowKey(NixieHomeRowViewModel r) =>
		string.Join("|", r.Collection.Id, r.Collection.Title, r.Collection.Why, string.Join(",", r.Collection.ItemIds), string.Join(",", r.Items.Select(i => i.Dto.Id)));

	/// <summary>A row's "See all": its collection's page.</summary>
	public void OpenNixieRow(NixieHomeRowViewModel row) => commands.OpenSmartCollection(row.Collection);
}
