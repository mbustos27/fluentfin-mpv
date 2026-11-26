using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.ViewModels;

/// <summary>Nixie 1.6: one Made by Nixie row on Home: the collection, its first titles, and its "See all".</summary>
public sealed partial class NixieHomeRowViewModel : ObservableObject
{
	public NixieHomeRowViewModel(SmartCollection collection) => Collection = collection;

	/// <summary>1.8 (the re-review): the row's collection; a new lineup changes it in place (<see cref="Take"/>).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title))]
	[NotifyPropertyChangedFor(nameof(SignTitle))]
	public partial SmartCollection Collection { get; set; }

	/// <summary>
	/// 1.8 (the re-review): another collection in this row's place, the row kept (its row on the page stays, and so does the
	/// keyboard): each card of the same title stays, another is replaced where it is (the row moves the keyboard from a
	/// replaced card to the one in its place, ShelfRow), and the cards past the new ones go from the end.
	/// </summary>
	internal void Take(SmartCollection collection, IReadOnlyList<BaseItemDto> items)
	{
		Collection = collection;
		for (var i = 0; i < items.Count; i++)
		{
			if (i < Items.Count && Items[i].Dto.Id == items[i].Id)
			{
				Items[i].Dto = items[i];
			}
			else if (i < Items.Count)
			{
				Items[i] = BaseItemViewModel.FromDto(items[i]);
			}
			else
			{
				Items.Add(BaseItemViewModel.FromDto(items[i]));
			}
		}

		while (Items.Count > items.Count)
		{
			Items.RemoveAt(Items.Count - 1);
		}
	}

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
/// Nixie 1.6 (the owner's decision, plan 4.2): Home's Made by Nixie rows, after "New in …". 1.8 (P1-1): they come from the
/// lineup (<see cref="CollectionLineup"/>): at most three, two by default (the best personal slot, then the seasonal row in
/// its window, otherwise Trending, otherwise Spotlight, then the lineup's next ones), and "Unfinished series", Home's own
/// row. Settings → Collections' switches still govern them. They are in Home's snapshot (<see cref="HomeCache"/>), so Home
/// paints them at once; the library index, read in the background on a first visit, fills them in when it is in. 1.8: a
/// first read's first pages fill them meanwhile, and a new lineup swaps only the rows that changed, in place.
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
	/// 1.8 (P1-1): the trending row follows its sources' switches only (Settings → Look up lists online), as it did when Home
	/// added it apart; "This week's picks" governs the lineup's other picks. Review (Minor 4): under its own stored name (Picks),
	/// not 1.7's "A collection a day" (Rotating): turned off in 1.7, that hid one row a day, and now hid every personal row.
	internal static bool NixieRowShown(SmartCollection collection, IReadOnlySet<NixieHomeRow> rowsOff, IReadOnlySet<SmartCollectionKind> kindsOff) =>
		(SmartCollectionCatalog.Published.Contains(collection.Kind) || Outside.TrendingBuilder.PageOrder.Contains(collection.Kind))
		&& !kindsOff.Contains(collection.Kind) && (collection.Kind switch
		{
			SmartCollectionKind.HighlyRated => NixieHomeRow.HighlyRated,
			SmartCollectionKind.UnfinishedSeries => NixieHomeRow.UnfinishedSeries,
			SmartCollectionKind.Seasonal => NixieHomeRow.Seasonal,
			_ when Outside.TrendingBuilder.PageOrder.Contains(collection.Kind) => (NixieHomeRow?)null, // 1.8
			_ => NixieHomeRow.Picks,
		} is not { } row || !rowsOff.Contains(row));

	/// <summary>
	/// 1.8 (P1-1): the rows Home shows, in order, under the Settings switches: at most <paramref name="rows"/> of the lineup
	/// (<see cref="Lineup.ForHome"/>), then "Unfinished series" (Home's own row, not one of them).
	/// </summary>
	public static IReadOnlyList<SmartCollection> PickNixieRows(Lineup lineup, IReadOnlyList<SmartCollection> all, IReadOnlySet<NixieHomeRow> rowsOff,
		IReadOnlySet<SmartCollectionKind> kindsOff, int rows)
	{
		var picked = lineup.ForHome(rows, x => NixieRowShown(x, rowsOff, kindsOff)).ToList();
		if (all.FirstOrDefault(x => x.Kind == SmartCollectionKind.UnfinishedSeries) is { } unfinished && NixieRowShown(unfinished, rowsOff, kindsOff))
		{
			picked.Add(unfinished);
		}

		return picked;
	}

	private void ShowNixieRows(IReadOnlyList<HomeSnapshotNixieRow> rows)
	{
		NixieRows.Clear();
		_nixieProvisional = false;
		// 1.6.1 (the 1.6 review): the saved page painted the rows Settings had turned off since it was saved, until the index's
		// rows replaced them; it shows today's switches at once
		var rowsOff = CollectionSettings.HomeRowsOff(localSettings);
		var kindsOff = CollectionSettings.KindsOff(localSettings);
		// 1.8 (P1-1, rule 7): and no more of the lineup's rows than Home shows now (a page saved by 1.7 had up to five: they
		// went at once when the lineup's came in, and every row under them moved up)
		var lineupRows = CollectionLineup.HomeRows(localSettings);
		foreach (var row in rows.Where(r => NixieRowShown(r.Collection, rowsOff, kindsOff)))
		{
			if (row.Collection.Kind != SmartCollectionKind.UnfinishedSeries && lineupRows-- <= 0)
			{
				continue;
			}

			NixieRows.Add(new NixieHomeRowViewModel(row.Collection) { Items = [.. row.Items.Select(BaseItemViewModel.FromDto)] });
		}

		SayFirstNixieRow("Home's saved page"); // 1.8 (rule 10)
	}

	// 1.8 (P1-1, rule 10): Home's first Made by Nixie row, timed from Home's page made (a view model a visit), said once a visit
	private readonly System.Diagnostics.Stopwatch _opened = System.Diagnostics.Stopwatch.StartNew();
	private bool _firstNixieRowSaid;

	/// <summary>Tests: what Home's first Made by Nixie row of this visit came from (as its log line says); null before it.</summary>
	internal string? FirstNixieRowFrom { get; private set; }

	private void SayFirstNixieRow(string from)
	{
		if (_firstNixieRowSaid || NixieRows.Count == 0)
		{
			return;
		}

		_firstNixieRowSaid = true;
		FirstNixieRowFrom = from;
		SmartCollectionCatalog.Log?.LogInformation("Home: its first Made by Nixie row in {Ms} ms since Home opened, from {From}", _opened.ElapsedMilliseconds, from);
	}

	// the re-review: a first read's pages' rows are never saved (a play or a removal saves Home meanwhile): none until the whole
	// index's are in
	private IReadOnlyList<HomeSnapshotNixieRow> NixieSnapshot() => _nixieProvisional ? [] :
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

			var asked = System.Diagnostics.Stopwatch.StartNew(); // 1.8 (rule 10): how long Home waits for its Nixie rows
			if (index.Current is { } snapshot)
			{
				await FillNixieRows(snapshot, (asked, "the index in memory"));
				_ = index.GetAsync(); // stale (a day old, a library change, R): read again in the background, for the next visit
				return;
			}

			NixieFill = FillNixieRowsLater(index, asked);
		}
		catch (Exception)
		{
			// the rows Home showed stay (the saved page's, or none)
		}
	}

	/// <summary>Tests: the fill of the rows behind Home when the index wasn't in memory (done when there is none).</summary>
	internal Task NixieFill { get; private set; } = Task.CompletedTask;

	private async Task FillNixieRowsLater(LibraryIndex index, System.Diagnostics.Stopwatch asked)
	{
		// the re-review (Major): the server and user this fill is for, from its start: one that ends after a sign-out and another
		// sign-in shows and saves nothing (it saved the first server's Home as the other's)
		var session = Session;
		try
		{
			// 1.8 (P1-1): the index saved on this PC, when there is one (no server read waited for). The re-review: a kept Home
			// is filled while hidden too (Back shows it as it is)
			if (await index.SavedAsync() is { } saved)
			{
				if (await FillNixieRows(saved, (asked, "the saved index"), session))
				{
					await SaveShownFor(session);
				}

				return;
			}

			// a first read (nothing saved): 1.8 (P1-1, the coordinator's Major: Home must not get slower) the rows from its first
			// pages while it goes on, unless Home's saved page shows rows already; then the whole index's, in their place
			var whole = index.GetAsync();
			if (NixieRows.Count == 0)
			{
				await FillFromFirstPages(index, whole, asked, session);
			}

			if (await whole is { } snapshot && await FillNixieRows(snapshot, (asked, snapshot.Reading is null ? "the index read" : "the index's first pages"), session))
			{
				await SaveShownFor(session);
			}
		}
		catch (Exception)
		{
			// as UpdateNixieRows: Home stays as it is
		}
	}

	// the re-review (Major): the server and user signed in now
	private (string? Url, Guid User) Session => (JellyfinClient.BaseUrl, JellyfinClient.UserId);

	// Home saved, if it is still that session's (SaveShown reads the session at once)
	private async Task SaveShownFor((string? Url, Guid User) session)
	{
		if (Session == session)
		{
			await SaveShown();
		}
	}

	/// <summary>Tests: Home saved now (as a play or a removal saves it).</summary>
	internal Task SaveNow() => SaveShown();

	/// <summary>The most first-pages fills a first read makes (one at each of its pages' rounds, every 4 s, until Home has its rows).</summary>
	internal const int FirstPagesFills = 3;

	// 1.8 (P1-1): a first read's titles so far (LibraryIndex.Changed, IndexSnapshot.Reading), made into Home's rows as they come,
	// until Home shows as many of the lineup's rows as it wants (or FirstPagesFills, or the whole read is in). Not saved: Home's
	// saved page is the whole index's
	private async Task FillFromFirstPages(LibraryIndex index, Task<IndexSnapshot?> whole, System.Diagnostics.Stopwatch asked, (string? Url, Guid User) session)
	{
		IndexSnapshot? last = null;
		for (var fill = 0; fill < FirstPagesFills && _shown && !whole.IsCompleted; fill++)
		{
			if (NixieRows.Count(x => x.Collection.Kind != SmartCollectionKind.UnfinishedSeries) >= CollectionLineup.HomeRows(localSettings))
			{
				return;
			}

			if (await NextPages(index, last, whole) is not { } pages || whole.IsCompleted || !_shown)
			{
				return;
			}

			last = pages;
			await FillNixieRows(pages, (asked, "the index's first pages"), session, provisional: true);
		}
	}

	/// <summary>
	/// The re-review: a first read's pages are its titles first by name (a digit, "A"), so of the lineup only Acclaimed and
	/// Under 90 minutes show from them, and only full (their <see cref="SmartCollections.Builders.HighlyRatedBuilder.Most"/> titles: the count
	/// the whole index's says too); never saved (<see cref="NixieSnapshot"/>).
	/// </summary>
	internal static IReadOnlyList<SmartCollection> Provisional(IReadOnlyList<SmartCollection> all) =>
		[.. all.Where(x => (x.Kind == SmartCollectionKind.HighlyRated && x.ItemIds.Count >= SmartCollections.Builders.HighlyRatedBuilder.Most)
			|| (x.Kind == SmartCollectionKind.ShortFilms && x.ItemIds.Count >= SmartCollections.Builders.ShortFilmsBuilder.Most))];

	// the re-review: the rows shown are a first read's pages' (not saved)
	private bool _nixieProvisional;

	// the read's titles so far, newer than `last`; null when the whole read is in first. Its wait returns to the caller's
	// thread (Home's): Changed is raised off it
	private static async Task<IndexSnapshot?> NextPages(LibraryIndex index, IndexSnapshot? last, Task whole)
	{
		var next = new TaskCompletionSource<IndexSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnChanged(object? sender, IndexSnapshot snapshot)
		{
			if (snapshot.Reading is not null && !ReferenceEquals(snapshot, last))
			{
				next.TrySetResult(snapshot);
			}
		}

		index.Changed += OnChanged;
		try
		{
			if (index.ReadingSoFar is { } soFar && !ReferenceEquals(soFar, last))
			{
				return soFar;
			}

			await Task.WhenAny(next.Task, whole);
			return next.Task.IsCompletedSuccessfully ? next.Task.Result : null;
		}
		finally
		{
			index.Changed -= OnChanged;
		}
	}

	/// <param name="timed">1.8 (rule 10): Home's own fill (not a trending change): the time since Home asked, said once filled, and from what.</param>
	/// <param name="session">The re-review (Major): the server and user the fill is for (the one signed in now when none is given).</param>
	/// <param name="provisional">The re-review: a first read's pages (<see cref="Provisional"/>).</param>
	/// <returns>Whether the rows were shown (false: the fill's session isn't the one signed in now, nothing shown).</returns>
	private async Task<bool> FillNixieRows(IndexSnapshot snapshot, (System.Diagnostics.Stopwatch Asked, string From)? timed = null, (string? Url, Guid User)? session = null,
		bool provisional = false)
	{
		var mine = session ?? Session;
		if (Session != mine || (snapshot.Key is { } key && key != HomeCache.FileKey(mine.Url ?? "", mine.User)))
		{
			return false; // another server's or user's (the index is its own: BH-171-m6)
		}

		var build = System.Diagnostics.Stopwatch.StartNew();
		var boxed = (await JellyfinClient.GetBoxSets())?.Items?.Select(x => IndexEntry.ProviderId(x, "Tmdb")).OfType<string>().ToHashSet() ?? [];
		var now = SmartCollectionCatalog.Now(_time); // 1.6.1 (BH-160-m1): the local date
		_nixieDate = now.Date; // 1.8: the lineup's day these rows are of
		s_nixieDay = now.Date;
		var kindsOff = CollectionSettings.KindsOff(localSettings);
		// 1.7 trending: "Trending in your library" (Trakt and TMDb merged), only while a source is on and it has titles;
		// the lists come from the cache at once, and the ones that aren't fresh are asked again in the background
		SmartCollection? trendingRow = null;
		if (Outside.TrendingServices.Collections is { } trending)
		{
			WatchTrending(trending);
			// matched off the UI thread (a big index); 1.7.3: in a catch of its own, as each kind's builder is (a throw there left
			// Home without any Made by Nixie row)
			trendingRow = await TrendingMatch.Safely(() => trending.HomeRowFor(snapshot), null, SmartCollectionCatalog.Log);
			_ = trending.RefreshAsync();
		}
		// end 1.7 trending
		// 1.8 (P1-1): the collections and their lineup, off Home's thread; the trending row is one of the lineup's candidates
		// review (Minor 4): Home's own lineup, of what its switches let on Home (made.Home), so none it hides takes a slot
		var rowsOff = CollectionSettings.HomeRowsOff(localSettings);
		bool Shown(SmartCollection x) => NixieRowShown(x, rowsOff, kindsOff);
		IReadOnlyList<SmartCollection> extra = trendingRow is null ? [] : [trendingRow];
		// 1.8 (stream s's Major 2): the week's Spotlight is made apart (SmartCollectionCatalog.BuildLaterAsync), beside the build,
		// never in Home's first rows' way. When Home shows rows already (its saved page, the first pages), the swap waits for it,
		// so a row isn't swapped twice; otherwise the rows show first and it comes in after them
		var fill = ++_nixieFills;
		LineupBuild made;
		var spotlightIn = true;
		Task<IReadOnlyList<SmartCollection>> later = Task.FromResult<IReadOnlyList<SmartCollection>>([]);
		if (provisional)
		{
			// the re-review: of a first read's pages, only the full picks (Provisional); no trending row, no Spotlight
			var all = await CollectionLineup.BuildAsync(snapshot, now, boxed, kindsOff, [], homeShown: Shown);
			made = await CollectionLineup.AssembleAsync(snapshot, Provisional(all.All), [], now, Shown, fallback: new LineupBuild([], Lineup.Empty));
		}
		else
		{
			later = LaterBuild(snapshot, now, kindsOff);
			made = await CollectionLineup.BuildAsync(snapshot, now, boxed, kindsOff, extra, homeShown: Shown);
			spotlightIn = NixieRows.Count > 0 || later.IsCompleted;
			if (spotlightIn)
			{
				made = await WithSpotlight(snapshot, made, extra, later, now, Shown);
			}
		}

		var built = build.ElapsedMilliseconds;
		if (await ShowNixieLineup(snapshot, made, rowsOff, kindsOff, mine) is not (>= 0 and var rows))
		{
			return false;
		}

		_nixieProvisional = provisional;
		// 1.8 (rule 10): durations and counts only, never a title
		if (timed is { } t)
		{
			SmartCollectionCatalog.Log?.LogInformation("Home: {Rows} Made by Nixie rows in {Ms} ms from {From} (made in {BuildMs} ms, their cards in {CardsMs} ms)",
				rows, t.Asked.ElapsedMilliseconds, t.From, built, build.ElapsedMilliseconds - built);
			SayFirstNixieRow(t.From);
		}

		if (!spotlightIn)
		{
			SpotlightLater = AddSpotlight(snapshot, made, extra, later, now, Shown, rowsOff, kindsOff, fill, build, mine);
		}

		return true;
	}

	/// <summary>Tests: the week's Spotlight coming in after Home's first rows (done when there is none).</summary>
	internal Task SpotlightLater { get; private set; } = Task.CompletedTask;

	/// <summary>The collections made after Home's first rows (stream s's <see cref="SmartCollectionCatalog.BuildLaterAsync"/>); tests give their own.</summary>
	internal Func<IndexSnapshot, DateTimeOffset, IReadOnlySet<SmartCollectionKind>, Task<IReadOnlyList<SmartCollection>>> LaterBuild { get; set; } =
		(index, now, kindsOff) => SmartCollectionCatalog.BuildLaterAsync(index, now, kindsOff);

	// fills of Home's Nixie rows started this visit: a Spotlight that comes after a newer fill began is that fill's to show
	private int _nixieFills;

	// 1.8 (stream s's Major 2): the week's Spotlight after Home's first rows: the lineup with it, the rows that change swapped in
	// place (rule 7), Home saved again. Never throws
	private async Task AddSpotlight(IndexSnapshot snapshot, LineupBuild made, IReadOnlyList<SmartCollection> extra, Task<IReadOnlyList<SmartCollection>> later, DateTimeOffset now,
		Func<SmartCollection, bool> shown, IReadOnlySet<NixieHomeRow> rowsOff, IReadOnlySet<SmartCollectionKind> kindsOff, int fill, System.Diagnostics.Stopwatch build,
		(string? Url, Guid User) session)
	{
		try
		{
			// the re-review: a kept Home too (hidden, Back shows it); never another session's
			var with = await WithSpotlight(snapshot, made, extra, later, now, shown);
			if (ReferenceEquals(with, made) || fill != _nixieFills || Session != session)
			{
				return;
			}

			var was = NixieRows.Select(NixieRowKey).ToList();
			if (await ShowNixieLineup(snapshot, with, rowsOff, kindsOff, session) < 0)
			{
				return;
			}

			var changed = NixieRows.Select(NixieRowKey).Where((key, i) => i >= was.Count || was[i] != key).Count();
			SmartCollectionCatalog.Log?.LogInformation("Home: the week's Spotlight in {Ms} ms since Home's rows were asked ({Changed} rows changed)", build.ElapsedMilliseconds, changed);
			if (changed > 0)
			{
				await SaveShownFor(session);
			}
		}
		catch (Exception)
		{
			// as UpdateNixieRows: Home stays as it is
		}
	}

	// 1.8 (stream s's Major 2): the lineup with the week's Spotlight once BuildLaterAsync has it (no builder runs again); the
	// build as it was when there is none (switched off, a first read's pages, nothing to pick)
	private static async Task<LineupBuild> WithSpotlight(IndexSnapshot snapshot, LineupBuild made, IReadOnlyList<SmartCollection> extra, Task<IReadOnlyList<SmartCollection>> later,
		DateTimeOffset now, Func<SmartCollection, bool> shown)
	{
		var spotlight = await later;
		// the re-review: a lineup that couldn't be made with it leaves the one made without it (it cleared Home's rows)
		return spotlight.Count == 0 ? made : await CollectionLineup.AssembleAsync(snapshot, made.All, [.. extra, .. spotlight], now, shown, fallback: made);
	}

	// Home's rows of a lineup: picked under the switches, their cards asked, swapped in where they changed; how many rows (-1:
	// the session isn't the fill's now, nothing swapped)
	private async Task<int> ShowNixieLineup(IndexSnapshot snapshot, LineupBuild made, IReadOnlySet<NixieHomeRow> rowsOff, IReadOnlySet<SmartCollectionKind> kindsOff,
		(string? Url, Guid User) session)
	{
		var picked = PickNixieRows(made.Home, made.All, rowsOff, kindsOff, CollectionLineup.HomeRows(localSettings));
		// 1.8: every row's cards asked at once (they were asked one row after another)
		var answers = await Task.WhenAll(picked.Select(async collection =>
		{
			var first = collection.ItemIds.Take(NixieRowLength).ToList();
			// the server's records (fresh watch state); the index's own when it doesn't answer
			var items = await JellyfinClient.GetItemsByIds(first) ?? Saved(snapshot, first);
			return items.Count > 0 ? new NixieHomeRowViewModel(collection) { Items = [.. items.Select(BaseItemViewModel.FromDto)] } : null;
		}));
		var rows = answers.OfType<NixieHomeRowViewModel>().ToList();
		if (Session != session)
		{
			return -1; // the re-review (Major)
		}

		// the same rows with the same titles keep their cards (a refresh that changes nothing redraws nothing)
		// 1.7 trending: the same collection too (its why line, its whole list): a trending row whose first titles stayed while
		// its sources changed is made again, so See all and its why line don't name a source that is off (the review, #6)
		// 1.8 (P1-1, rule 7): row by row, in place: a new lineup behind the rows shown (the saved page's, the first pages') swaps
		// only the rows that changed, each where it is; none goes and comes back, so nothing under it moves meanwhile
		for (var i = 0; i < rows.Count; i++)
		{
			if (i < NixieRows.Count && NixieRowKey(NixieRows[i]) == NixieRowKey(rows[i]))
			{
				KeepCards(NixieRows[i].Items, [.. rows[i].Items.Select(x => x.Dto)]);
			}
			else if (i < NixieRows.Count)
			{
				// the re-review: the row stays on the page, the keyboard with it (its cards change where they are)
				NixieRows[i].Take(rows[i].Collection, [.. rows[i].Items.Select(x => x.Dto)]);
			}
			else
			{
				NixieRows.Add(rows[i]);
			}
		}

		while (NixieRows.Count > rows.Count)
		{
			NixieRows.RemoveAt(NixieRows.Count - 1);
		}

		return rows.Count;
	}

	// 1.8 (P1-1): the local date the Nixie rows shown were made for (null: none made yet this visit)
	private DateTime? _nixieDate;

	// 1.8 (the review's midnight case): the local date Home's rows were last made for in this app, whichever visit made them. A
	// visit within five minutes of Home's last save shows that page as it is (HomeCache's IsFresh): across midnight, yesterday's
	private static DateTime? s_nixieDay;

	/// <summary>
	/// 1.8 (P1-1): Home kept whole on Back (1.7.5) and shown again on another day: its Made by Nixie rows are made again behind,
	/// from the index in memory (the lineup turns its daily picks each day and its themes each ISO week, so yesterday's rows are
	/// not today's); the same day, nothing. For <see cref="Returned"/>, and (the review's midnight case) a visit that showed a
	/// page saved under five minutes ago: its rows are of the day this app last made them. Never throws.
	/// </summary>
	internal async Task RefreshNixieIfNewDay()
	{
		try
		{
			if ((_nixieDate ?? s_nixieDay) is { } shown && SmartCollectionCatalog.Now(_time).Date != shown && SmartCollectionServices.Index?.Current is { } snapshot)
			{
				var session = Session;
				if (await FillNixieRows(snapshot, (System.Diagnostics.Stopwatch.StartNew(), "the index in memory, a new day"), session))
				{
					await SaveShownFor(session);
				}
			}
			else if (NixieRowsOfNoKnownDay(_nixieDate ?? s_nixieDay, NixieRows.Count, NixieFill.IsCompleted))
			{
				// 1.8.1 (BH-180-q4): rows of a day this app doesn't know: a saved page's, shown again under five minutes after it was
				// saved, before any fill of this app made rows (the start's fill still on its way: a first read). Across midnight they
				// were yesterday's until the next visit; made now as on a load, from the index in memory or behind it
				SmartCollectionCatalog.Log?.LogInformation("Home: its Made by Nixie rows are a saved page's, of no day this app made rows on: made again");
				await UpdateNixieRows();
			}
		}
		catch (Exception)
		{
			// as UpdateNixieRows: Home stays as it is
		}
	}

	/// <summary>
	/// 1.8.1 (BH-180-q4): Home shows Made by Nixie rows whose day nothing in this app knows: no fill of this view model made them
	/// (<paramref name="shownDay"/> null: neither this page's nor any this app made) and none of its own is on its way.
	/// </summary>
	internal static bool NixieRowsOfNoKnownDay(DateTime? shownDay, int rows, bool fillDone) => shownDay is null && rows > 0 && fillDone;

	/// <summary>Tests: a new process (no Home of this app has made its rows yet).</summary>
	internal static void ForgetNixieDayForTests() => s_nixieDay = null;

	// the index's own records of these titles, in their order (one pass over the index, not one a title)
	private static List<BaseItemDto> Saved(IndexSnapshot snapshot, List<Guid> ids)
	{
		var wanted = ids.ToHashSet();
		var found = new Dictionary<Guid, IndexEntry>();
		foreach (var entry in snapshot.Entries)
		{
			if (wanted.Contains(entry.Id))
			{
				found.TryAdd(entry.Id, entry);
			}
		}

		return [.. ids.Select(id => found.GetValueOrDefault(id)).OfType<IndexEntry>().Select(x => x.ToDto())];
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
				var session = Session;
				if (await FillNixieRows(snapshot, session: session))
				{
					await SaveShownFor(session);
				}
			}
		}
		catch (Exception)
		{
			// as UpdateNixieRows: Home stays as it is
		}
	}
	// end 1.7 trending

	// 1.7 trending: what makes two Home rows the same: the collection (its id, title, why line and every title on it) and the cards
	// 1.8 (stream t's model): and what the row says under its title (its reason line, its badges): a row of a 1.7 saved page,
	// which has none, gives way to the same collection with its reason; an unwatched count that changed is said anew
	private static string NixieRowKey(NixieHomeRowViewModel r) =>
		string.Join("|", r.Collection.Id, r.Collection.Title, r.Collection.Why, r.Collection.ReasonText, r.Collection.BadgesText, string.Join(",", r.Collection.ItemIds),
			string.Join(",", r.Items.Select(i => i.Dto.Id)));

	/// <summary>A row's "See all": its collection's page.</summary>
	public void OpenNixieRow(NixieHomeRowViewModel row) => commands.OpenSmartCollection(row.Collection);
}
