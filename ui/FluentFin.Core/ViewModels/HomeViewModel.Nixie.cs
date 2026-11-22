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
	public string Title => Collection.Kind switch
	{
		SmartCollectionKind.Actor => $"With {Collection.Title}",
		SmartCollectionKind.Director => $"Directed by {Collection.Title}",
		SmartCollectionKind.Studio or SmartCollectionKind.Network => $"From {Collection.Title}",
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
/// Nixie 1.6 (the owner's decision, plan 4.2): Home's Made by Nixie rows, after "New in …": "Highly rated, not watched" and
/// "Unfinished series" on by default, a seasonal row in season, and one of the other kinds a day; each can be turned off in
/// Settings → Collections. They are in Home's snapshot (<see cref="HomeCache"/>), so Home paints them at once; the library
/// index, read in the background on a first visit, fills them in when it is in.
/// </summary>
public partial class HomeViewModel
{
	/// <summary>How many titles a Made by Nixie row shows.</summary>
	public const int NixieRowLength = 20;

	public ObservableCollection<NixieHomeRowViewModel> NixieRows { get; } = [];

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
		foreach (var row in rows)
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
		var now = _time.GetUtcNow();
		var all = await SmartCollectionCatalog.BuildAllAsync(snapshot, now, boxed, CollectionSettings.KindsOff(localSettings));
		var picked = PickNixieRows(all, CollectionSettings.HomeRowsOff(localSettings), now);
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
		if (rows.Select(r => (r.Collection.Id, Ids: string.Join(",", r.Items.Select(i => i.Dto.Id)))).SequenceEqual(NixieRows.Select(r => (r.Collection.Id, Ids: string.Join(",", r.Items.Select(i => i.Dto.Id))))))
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

	/// <summary>A row's "See all": its collection's page.</summary>
	public void OpenNixieRow(NixieHomeRowViewModel row) => commands.OpenSmartCollection(row.Collection);
}
