using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6 (plan 4.4): one Nixie preset of a library's presets strip: a collection Nixie made of that library's titles
/// ("Heists", "1980s films", "Comedy · Horror"). The colpage stream owns the strip and its look (its <c>LibraryPreset</c>,
/// <c>ILibraryPresetSource</c>); this is what the strip's Nixie entries are made from (<see cref="NixieLibraryPresets"/>).
/// </summary>
/// <param name="Id">Stable: "nixie:" and the collection's id ("nixie:franchise:2344").</param>
/// <param name="Label">The chip's text: the collection's name.</param>
/// <param name="AutomationName">Narrator: "Heists, made by Nixie, keyword, 12 films".</param>
/// <param name="Items">Its titles in this library.</param>
public sealed record NixiePreset(string Id, string Label, string AutomationName, SmartCollectionKind Kind, SmartCollection Collection, IReadOnlySet<Guid> Items)
{
	public const string IdPrefix = "nixie:";

	/// <summary>Opens the collection on the Collection page.</summary>
	public void Open(GlobalCommands commands) => commands.OpenSmartCollection(Collection);
}

/// <summary>
/// Nixie 1.6: a library's Nixie presets, made of its own titles only, kind by kind in <see cref="Order"/>: its franchises,
/// decades, genre mixes, keywords, countries and awards, then its picks; at most <see cref="Most"/>, the kinds turned off in
/// Settings left out.
/// </summary>
/// 1.7.3 (the owner's answer 3): no directors, actors, studios or networks; decades, genre mixes and countries stay here as
/// filters, though Nixie no longer publishes them as collections (<see cref="StripOnlyKinds"/>).
public static class NixiePresets
{
	public const int Most = 12;

	/// <summary>
	/// 1.7.3: the strip's kinds, in its order (1.7.2's, less the people, studios and networks). A kind not listed goes last
	/// (it went first: <c>IndexOf</c>'s -1).
	/// </summary>
	public static IReadOnlyList<SmartCollectionKind> Order { get; } =
	[
		SmartCollectionKind.Franchise, SmartCollectionKind.Decade, SmartCollectionKind.GenreMix, SmartCollectionKind.Keyword,
		SmartCollectionKind.Country, SmartCollectionKind.Award,
		SmartCollectionKind.HighlyRated, SmartCollectionKind.UnfinishedSeries, SmartCollectionKind.Seasonal, SmartCollectionKind.ShortFilms,
	];

	/// <summary>
	/// 1.7.3: the kinds only the strip makes (they are not published: the catalog's <c>BuildAll</c> leaves them out), in the
	/// strip's order (their Settings switches). Fix round (BH-173-m2): each has its Settings switch again
	/// (<see cref="CollectionsSettingsViewModel.LibraryFilters"/>), and one turned off is left out (1.7.2's switches governed the
	/// strip too; the first 1.7.3 build brought them all back). 1.7.5: made by the catalog's builders (<see cref="For"/>).
	/// </summary>
	public static IReadOnlyList<SmartCollectionKind> StripOnlyKinds { get; } = [SmartCollectionKind.Decade, SmartCollectionKind.GenreMix, SmartCollectionKind.Country];

	// their ids' first part, as their builders make them ("decade:movie:1980", "genres:movie:comedy+horror", "country:france")
	private static readonly string[] StripOnlyPrefixes = ["decade:", "genres:", "country:"];

	/// <summary>
	/// 1.7.3: whether a collection's id (or a preset's, "nixie:decade:…") is of a kind only the strip makes: a pin of one is
	/// made by its id (<see cref="SmartCollectionCatalog"/>'s <c>Find</c>), since the catalog no longer publishes it.
	/// </summary>
	public static bool IsStripOnly(string? id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return false;
		}

		var own = id.StartsWith(NixiePreset.IdPrefix, StringComparison.Ordinal) ? id[NixiePreset.IdPrefix.Length..] : id;
		return StripOnlyPrefixes.Any(p => own.StartsWith(p, StringComparison.Ordinal));
	}

	/// <summary>The presets from an index (pure: tests, and <see cref="NixieLibraryPresets"/> once the index is in).</summary>
	public static IReadOnlyList<NixiePreset> For(IndexSnapshot index, Guid libraryId, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises = null,
		IReadOnlySet<SmartCollectionKind>? kindsOff = null, int most = Most)
	{
		var own = index with { Entries = [.. index.Entries.Where(x => x.InLibrary(libraryId))] }; // 1.7.3 (P0-2a): a title in two libraries is in both
		if (own.Entries.Count == 0)
		{
			return [];
		}

		// 1.7.3: the published kinds the strip shows, then the strip's own (decades, genre mixes, countries). Fix round's
		// re-review (BH-173-M2's other half): every title of each rule, never a collection's display cap, as a saved filter of
		// the chip keeps (Under 90 minutes + Comedy showed 0 titles in the library, and 80 once saved).
		// 1.7.5 (fx1; M2's review of 1.7.3): both in one build of the catalog's (BuildKinds), less the kinds turned off: its gate
		// counter and its catch, and the library's titles collapsed once (the strip's own kinds had a context, a loop and a catch
		// of their own, and collapsed the titles again)
		var kinds = SmartCollectionCatalog.Published.Concat(StripOnlyKinds).Where(x => kindsOff?.Contains(x) != true).ToHashSet();
		var all = SmartCollectionCatalog.BuildKinds(own, now, kinds, boxedFranchises, Outside.OutsideListsSnapshot.Shared, whole: true).Where(x => kinds.Contains(x.Kind));
		// one of each kind first (the strip shows a mix, not twelve franchises), then the rest in the same order
		var byKind = all.GroupBy(x => x.Kind).OrderBy(g => Place(g.Key)).Select(g => g.ToList()).ToList();
		var picked = new List<SmartCollection>();
		for (var round = 0; picked.Count < most && byKind.Any(g => g.Count > round); round++)
		{
			picked.AddRange(byKind.Where(g => g.Count > round).Select(g => g[round]).Take(most - picked.Count));
		}

		return [.. picked.Select(x => new NixiePreset(NixiePreset.IdPrefix + x.Id, ShortLabel(x), x.AutomationName, x.Kind, x, x.ItemIds.ToHashSet()))];
	}

	// 1.7.3: a kind's place in the strip; one not in Order after all of them
	private static int Place(SmartCollectionKind kind)
	{
		for (var i = 0; i < Order.Count; i++)
		{
			if (Order[i] == kind)
			{
				return i;
			}
		}

		return int.MaxValue;
	}

	/// <summary>The longest label a strip chip takes (the strip scrolls with up to twelve of them).</summary>
	public const int LabelMost = 24;

	/// <summary>
	/// A preset's chip text, short (the library is said by the page around it): "1980s" not "1980s films", "France" not "Films
	/// from France", "Acclaimed"; a name longer than <see cref="LabelMost"/> ends in "…". Narrator hears the whole name.
	/// </summary>
	public static string ShortLabel(SmartCollection collection)
	{
		var label = collection.Kind switch
		{
			SmartCollectionKind.Decade => collection.Title.Split(' ')[0],
			SmartCollectionKind.Country when collection.Title.StartsWith("Films from ", StringComparison.Ordinal) => collection.Title["Films from ".Length..] switch
			{
				"the United States" => "USA",
				"the United Kingdom" => "UK",
				var country when country.StartsWith("the ", StringComparison.Ordinal) => country[4..],
				var country => country,
			},
			SmartCollectionKind.HighlyRated => "Acclaimed", // 1.7.3 (P0-4): "Acclaimed, not seen yet" (was "Highly rated")
			SmartCollectionKind.ShortFilms => "Under 90 minutes", // 1.7.3 (P0-4): was the title, "Short films"
			SmartCollectionKind.UnfinishedSeries => "Unfinished",
			_ => collection.Title,
		};
		return label.Length <= LabelMost ? label : label[..(LabelMost - 1)].TrimEnd(' ', ':', ',', '·', '-', '–') + "…";
	}

	/// <summary>
	/// The presets of a library for its strip: from the library index (none while it is first read), less the franchises an
	/// admin's box set already is and the kinds turned off. Off the UI thread; never throws.
	/// </summary>
	public static async Task<IReadOnlyList<NixiePreset>> ForLibraryAsync(IJellyfinClient client, Guid libraryId, CancellationToken cancellationToken = default)
	{
		try
		{
			if (SmartCollectionServices.Index is not { } index || await index.GetAsync(cancellationToken) is not { } snapshot)
			{
				return [];
			}

			var boxed = (await client.GetBoxSets(cancellationToken))?.Items?.Select(x => IndexEntry.ProviderId(x, "Tmdb")).OfType<string>().ToHashSet() ?? [];
			var off = CollectionSettings.KindsOff(SmartCollectionServices.Settings);
			return await Task.Run(() => For(snapshot, libraryId, SmartCollectionCatalog.Now(), boxed, off), cancellationToken);
		}
		catch (OperationCanceledException)
		{
			return [];
		}
	}
}

/// <summary>
/// Nixie 1.6, for the colpage stream's seams (its <c>ILibraryPresetSource</c>, called once a library's titles are in, and its
/// <c>ISavedFilters</c>, Save as collection): every library's Nixie presets, made off the UI thread whenever the library index
/// comes in (or is first asked for), so the strip's synchronous call is a lookup; and the saved filter, kept in
/// <see cref="SmartCollectionStore"/>. The adapter to colpage's interfaces is <c>NixieLibraryPresetSource</c> (ColpageSeams.cs).
/// </summary>
public sealed class NixieLibraryPresets
{
	private readonly LibraryIndex _index;
	private readonly IJellyfinClient _client;
	private readonly SmartCollectionStore _store;
	private readonly ILocalSettingsService? _settings;
	private readonly ILogger? _logger;
	private readonly Lock _lock = new();
	private IndexSnapshot? _madeFor;
	// review Major 2: whose presets these are (the server and user, HomeCache.FileKey): another user's are never served, and a
	// sign-out or a removed login clears them (LibraryIndex.Forgotten)
	private string? _madeForKey;
	private Dictionary<Guid, IReadOnlyList<NixiePreset>> _presets = [];
	private Task _making = Task.CompletedTask;

	/// <summary>
	/// New presets were made (the index came in or changed): a strip that asked before they were ready (none) can ask again.
	/// Raised off the UI thread. For colpage's source that delivers later (its next shape).
	/// </summary>
	public event EventHandler? Changed;

	public NixieLibraryPresets(LibraryIndex index, IJellyfinClient client, SmartCollectionStore store, ILocalSettingsService? settings = null, ILogger<NixieLibraryPresets>? logger = null)
	{
		_index = index;
		_client = client;
		_store = store;
		_settings = settings;
		_logger = logger;
		index.Changed += (_, snapshot) =>
		{
			if (snapshot.Reading is null) // 1.7.1: a first read's pages so far make no presets (a library's strip waits for the whole)
			{
				Make(snapshot);
			}
		};
		index.Forgotten += (_, _) => Clear();
	}

	private string CurrentKey => HomeCache.FileKey(_client.BaseUrl, _client.UserId);

	/// <summary>A sign-out or a removed login: nothing kept for anyone.</summary>
	private void Clear()
	{
		lock (_lock)
		{
			_madeFor = null;
			_madeForKey = null;
			_presets = [];
		}
	}

	/// <summary>The presets being made now (tests wait for it).</summary>
	internal Task Making
	{
		get
		{
			lock (_lock)
			{
				return _making;
			}
		}
	}

	/// <summary>
	/// A library's presets with their titles among <paramref name="titles"/> (the ones the page has), none of them empty. A
	/// lookup: none while the index is first read (they are made then, for the next visit).
	/// </summary>
	public IReadOnlyList<NixiePreset> PresetsFor(BaseItemDto library, IReadOnlyList<BaseItemDto> titles)
	{
		if (library.Id is not { } id)
		{
			return [];
		}

		IReadOnlyList<NixiePreset>? made = null;
		var off = CollectionSettings.KindsOff(_settings);
		var stale = false;
		lock (_lock)
		{
			// only the signed-in user's own: another user's (made before a sign-out) are never theirs
			if (_madeForKey == CurrentKey)
			{
				_presets.TryGetValue(id, out made);
				stale = _madeOff != OffKey(off); // fix round (BH-173-m2): a switch changed since they were made
			}
		}

		if (made is not null && stale && _index.Current is { } changed)
		{
			Make(changed); // made again with the switches as they are now; Changed says when (a kind turned on comes then)
		}

		if (made is null)
		{
			if (_index.Current is { } current)
			{
				Make(current);
			}
			else
			{
				_ = _index.GetAsync(); // its Changed makes them
			}

			return [];
		}

		var shown = titles.Select(x => x.Id).OfType<Guid>().ToHashSet();
		return [.. made.Where(p => !off.Contains(p.Kind)).Select(p => p with { Items = p.Items.Where(shown.Contains).ToHashSet() }).Where(p => p.Items.Count > 0)];
	}

	// fix round (BH-173-m2): the switches the presets were made with, as one string
	private string _madeOff = "";

	private static string OffKey(IReadOnlySet<SmartCollectionKind> off) => string.Join(",", off.Order());

	/// <summary>
	/// colpage's asking shape (its ILibraryPresetSource.PresetsForAsync, e6d55cd): the presets once they are made, waiting
	/// for the index's first read (minutes on a big library; the strip shows the built-in four meanwhile, and
	/// <see cref="Changed"/> says when there are more); none when the index can't be read or <paramref name="cancellationToken"/> ends.
	/// </summary>
	public async Task<IReadOnlyList<NixiePreset>> PresetsForAsync(BaseItemDto library, IReadOnlyList<BaseItemDto> titles, CancellationToken cancellationToken)
	{
		var now = PresetsFor(library, titles);
		if (now.Count > 0 || library.Id is not { } id)
		{
			return now;
		}

		try
		{
			if (await _index.GetAsync(cancellationToken) is not { } snapshot)
			{
				return [];
			}

			Make(_index.Current ?? snapshot); // 1.7.1: the newest (a first read's people may be in by now: never made again from the older)
			await Making.WaitAsync(cancellationToken);
			return PresetsFor(library, titles);
		}
		catch (OperationCanceledException)
		{
			return [];
		}
	}

	// every library's presets for this index, off the UI thread (one at a time; a newer index's wins)
	private void Make(IndexSnapshot snapshot)
	{
		var off = CollectionSettings.KindsOff(_settings);
		var offKey = OffKey(off);
		lock (_lock)
		{
			var key = snapshot.Key ?? CurrentKey; // 1.7.1 (BH-171-m6): the index's own user, not whoever is signed in by now
			if (ReferenceEquals(_madeFor, snapshot) && _madeForKey == key && _madeOff == offKey)
			{
				return;
			}

			_madeFor = snapshot;
			_madeForKey = key;
			_madeOff = offKey; // fix round (BH-173-m2)
			_making = Task.Run(async () =>
			{
				try
				{
					var boxed = (await _client.GetBoxSets())?.Items?.Select(x => IndexEntry.ProviderId(x, "Tmdb")).OfType<string>().ToHashSet() ?? [];
					var now = SmartCollectionCatalog.Now();
					var presets = snapshot.Libraries.DistinctBy(l => l.Id).ToDictionary(l => l.Id, l => NixiePresets.For(snapshot, l.Id, now, boxed, off)); // 1.7.2: a library listed twice is one
					bool current;
					lock (_lock)
					{
						current = ReferenceEquals(_madeFor, snapshot) && _madeForKey == key && key == CurrentKey && _madeOff == offKey;
						if (current)
						{
							_presets = presets;
						}
					}

					if (current)
					{
						Changed?.Invoke(this, EventArgs.Empty);
					}
				}
				catch (Exception ex)
				{
					_logger?.LogWarning(ex, "The libraries' Nixie presets could not be made");
				}
			});
		}
	}

	/// <summary>A preset's label ("Heists") by its id, for a saved filter's words; null when it isn't one of the presets made.</summary>
	public string? LabelOf(string presetId)
	{
		lock (_lock)
		{
			return _madeForKey == CurrentKey ? _presets.Values.SelectMany(x => x).FirstOrDefault(p => p.Id == presetId)?.Label : null;
		}
	}

	/// <summary>
	/// Save as collection: the library's filters kept under a name (a Nixie preset on too: <paramref name="presetId"/>, its
	/// titles only). False when it could not be kept.
	/// </summary>
	public async Task<bool> SaveFilter(string name, SavedFilter filter)
	{
		try
		{
			await _store.SaveFilter(name, filter);
			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Save as collection failed");
			return false;
		}
	}
}
