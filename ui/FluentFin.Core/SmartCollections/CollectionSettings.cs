using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6: Settings → Collections, kept on this PC. Every switch is on by default, so each is stored the other way round
/// ("…Off"): a settings store that says nothing (a new install, a test's) keeps them on.
/// </summary>
public static class CollectionSettingKeys
{
	/// <summary>"Tidy collection names" turned off: box set and library names show as the server has them.</summary>
	public static Key<bool> KeepServerNames { get; } = new("CollectionsKeepServerNames", false);

	/// <summary>The Made by Nixie kinds turned off, by name ("Actor,Country").</summary>
	public static Key<string> KindsOff { get; } = new("CollectionsKindsOff", "");

	/// <summary>The Home rows turned off, by name ("HighlyRated,Rotating").</summary>
	public static Key<string> HomeRowsOff { get; } = new("CollectionsHomeRowsOff", "");
}

/// <summary>Nixie 1.6: the Made by Nixie rows Home can show (the owner's decision: the first three on, a rotating one a day).</summary>
public enum NixieHomeRow
{
	HighlyRated,
	UnfinishedSeries,
	Seasonal,

	/// <summary>1.6 to 1.7: "A collection a day". 1.8: no row of Home's: turned off in 1.7, it hides nothing (review, Minor 4).</summary>
	Rotating,

	/// <summary>1.8 (P1-1): "This week's picks": the lineup's rows that have no switch of their own (personal, themes, canon, Spotlight, Under 90).</summary>
	Picks,
}

public static class CollectionSettings
{
	/// <summary>At start, and when the switch changes: the names follow "Tidy collection names".</summary>
	/// 1.7.3 fix round (BH-173-m2, q4): "CollectionsKindsOff" is left as the user set it. The first 1.7.3 build took the seven
	/// kinds it no longer publishes out of it: decades, genre mixes and countries turned off in 1.7.2 came back in every
	/// library's filter strip (which they still govern: <see cref="CollectionsSettingsViewModel.LibraryFilters"/>), and directors,
	/// actors, studios and networks turned off came back on after a return to 1.7.2. Their names change nothing in 1.7.3 (no
	/// builder of theirs is published).
	public static void Apply(ILocalSettingsService settings)
	{
		CollectionNames.Tidy = !settings.ReadSetting(CollectionSettingKeys.KeepServerNames);
	}

	public static IReadOnlySet<SmartCollectionKind> KindsOff(ILocalSettingsService? settings) =>
		Parse<SmartCollectionKind>(settings?.ReadSetting(CollectionSettingKeys.KindsOff));

	/// <summary>1.8 (BH-180-M2): the kinds made of the titles' people: the index reads its people while one of them is on.</summary>
	public static IReadOnlySet<SmartCollectionKind> PeopleKinds { get; } = new HashSet<SmartCollectionKind> { SmartCollectionKind.MoreFrom, SmartCollectionKind.Spotlight };

	/// <summary>1.8 (BH-180-M2): whether a kind made of people is on (the library index's people step runs only then).</summary>
	public static bool PeopleWanted(ILocalSettingsService? settings) => !PeopleKinds.All(KindsOff(settings).Contains);

	public static IReadOnlySet<NixieHomeRow> HomeRowsOff(ILocalSettingsService? settings) =>
		Parse<NixieHomeRow>(settings?.ReadSetting(CollectionSettingKeys.HomeRowsOff));

	private static HashSet<T> Parse<T>(string? text) where T : struct, Enum =>
		[.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Enum.TryParse<T>(x, out var v) ? (T?)v : null).OfType<T>()];

	internal static void Set<T>(ILocalSettingsService settings, Key<string> key, T value, bool on) where T : struct, Enum
	{
		var off = Parse<T>(settings.ReadSetting(key));
		if (on ? off.Remove(value) : off.Add(value))
		{
			settings.SaveSetting(key, string.Join(",", off.OrderBy(x => x)));
		}
	}
}

/// <summary>One switch of Settings → Collections ("Franchises", "Acclaimed, not seen yet").</summary>
public sealed partial class CollectionSwitch(string name, string description, Func<bool> read, Action<bool> write) : ObservableObject
{
	public string Name { get; } = name;

	public string Description { get; } = description;

	public bool IsOn
	{
		get => read();
		set
		{
			if (value != read())
			{
				write(value);
				OnPropertyChanged();
			}
		}
	}
}

/// <summary>
/// Nixie 1.6: Settings → Collections: "Tidy collection names", the Home rows, and each kind of Made by Nixie collection. A
/// change applies on the next visit of Home and the Collections page (Home's saved page is marked stale); the names change at once.
/// </summary>
public sealed class CollectionsSettingsViewModel
{
	private readonly ILocalSettingsService _settings;
	private readonly HomeCache? _homeCache;

	public CollectionsSettingsViewModel(ILocalSettingsService settings, HomeCache? homeCache = null)
	{
		_settings = settings;
		_homeCache = homeCache;
		TidyNames = new CollectionSwitch("Tidy collection names",
			"Shows “[HUB] Marvel” as “Marvel” and “!010_Bond” as “Bond”, for collections and libraries. Titles of films and shows, and playlists, stay as they are.",
			() => !settings.ReadSetting(CollectionSettingKeys.KeepServerNames),
			on =>
			{
				settings.SaveSetting(CollectionSettingKeys.KeepServerNames, !on);
				CollectionSettings.Apply(settings);
				_homeCache?.MarkStale();
			});
		HomeRows =
		[
			// 1.7.3 (P0-4, rule 11): "Acclaimed, not seen yet" (was "Highly rated, not watched": "rated 7.5 or more on your server").
			// Fix round (BH-173-q1): nothing counts raters; what backs a rating is a critics' rating, tags, copies and outside lists
			Row(NixieHomeRow.HighlyRated, SmartCollectionKinds.Plural(SmartCollectionKind.HighlyRated), HighlyRatedText),
			Row(NixieHomeRow.UnfinishedSeries, "Unfinished series", "Shows you started and haven't watched for a month."),
			Row(NixieHomeRow.Seasonal, "In season", "Halloween in October, Christmas in December."),
			// 1.8 (P1-1): Home's rows come from the week's lineup (CollectionLineup). Review (Minor 4): under a name of their own
			// ("Picks"): 1.7's "A collection a day" turned off hid a row a day, not every personal, theme and canon row
			Row(NixieHomeRow.Picks, PicksName, PicksText),
			// 1.8 (P1-1): two of the week's picks on Home by default, three with this on
			new CollectionSwitch(ThirdRowName, ThirdRowText,
				() => CollectionLineup.HomeRows(settings) > CollectionLineup.HomeDefault,
				on =>
				{
					settings.SaveSetting(CollectionLineup.HomeRowsKey, on ? CollectionLineup.HomeMost : CollectionLineup.HomeDefault);
					_homeCache?.MarkStale();
				}),
		];
		// 1.7 keyless: not the awards: they come from Wikidata, whose switch is in Look up lists online ("from the server's own data").
		// 1.7.3 (P0-3): the seven kinds Nixie no longer publishes have no switch (PageOrder: franchises and keywords here)
		// 1.8 (BH-180-M2): and 1.8's kinds (Next in, More like, More from, the Spotlight): with no switch, every switch off still
		// left Home two rows, This week and the people step. More from and the Spotlight off stop the people step
		Kinds = [.. SmartCollectionKinds.Switched.Select(kind => new CollectionSwitch(SmartCollectionKinds.Plural(kind), KindText(kind),
			() => !CollectionSettings.KindsOff(settings).Contains(kind),
			on =>
			{
				CollectionSettings.Set(settings, CollectionSettingKeys.KindsOff, kind, on);
				_homeCache?.MarkStale();
				if (CollectionSettings.PeopleKinds.Contains(kind))
				{
					ViewModels.SmartCollectionServices.Index?.PeopleSwitched();
				}
			}))];
		// fix round (BH-173-m2): the presets strip's own kinds, with the switches 1.7.2 had for them (the same setting)
		LibraryFilters = [.. NixiePresets.StripOnlyKinds.Select(kind => new CollectionSwitch(SmartCollectionKinds.Plural(kind), "",
			() => !CollectionSettings.KindsOff(settings).Contains(kind),
			on => CollectionSettings.Set(settings, CollectionSettingKeys.KindsOff, kind, on)))];
	}

	/// <summary>1.8 (BH-180-M2): what 1.8's kind switches turn off (the others' words are their names).</summary>
	public static string KindText(SmartCollectionKind kind) => kind switch
	{
		SmartCollectionKind.NextIn => "The next film of a franchise you started.",
		SmartCollectionKind.MoreLike => "Titles like one you finished.",
		SmartCollectionKind.MoreFrom => "More of a director or actor of a title you finished. Off with Spotlight, Nixie doesn't read the titles' people.",
		SmartCollectionKind.Spotlight => "A director, actor or studio each week. Off with More from, Nixie doesn't read the titles' people.",
		_ => "",
	};

	/// <summary>Acclaimed's Home row switch, its words (fix round, BH-173-q1).</summary>
	public const string HighlyRatedText = "The best-rated titles on the server that you haven't watched. Titles with more behind their rating come first.";

	// 1.8 (P1-1): the week's picks on Home, and their third row
	public const string PicksName = "This week's picks";

	public const string PicksText = "Collections Nixie picks for the week: first one tied to a title you finished, then what's in season or trending on the server.";

	public const string ThirdRowName = "A third row of picks";

	public const string ThirdRowText = "Three of this week's picks on Home instead of two.";

	/// <summary>
	/// 1.7.3 fix round (BH-173-m2): the filters Nixie adds to each library's strip (decades, genre mixes, countries), each with its
	/// switch: a library opened after a change leaves out the ones turned off.
	/// </summary>
	public IReadOnlyList<CollectionSwitch> LibraryFilters { get; }

	private CollectionSwitch Row(NixieHomeRow row, string name, string description) => new(name, description,
		() => !CollectionSettings.HomeRowsOff(_settings).Contains(row),
		on =>
		{
			CollectionSettings.Set(_settings, CollectionSettingKeys.HomeRowsOff, row, on);
			_homeCache?.MarkStale();
		});

	public CollectionSwitch TidyNames { get; }

	/// <summary>Home's Made by Nixie rows.</summary>
	public IReadOnlyList<CollectionSwitch> HomeRows { get; }

	/// <summary>Each kind of Made by Nixie collection (the Collections page, Home's rotating row, the libraries' presets).</summary>
	public IReadOnlyList<CollectionSwitch> Kinds { get; }
}

// 1.7.3 (P0-3, the owner's answer 2)
/// <summary>
/// Nixie no longer makes director, actor, studio and network collections (the owner, 2026-11-22: they go, with a changelog
/// line; "More from" brings people back on title pages later). Their pins and the saved filters made with one of their
/// presets on are removed from this PC's saved collections, before the Collections page first reads them, so no empty tile or
/// error is left behind. Idempotent: once they are gone there is nothing to do. Decades, genre mixes and countries stay: the
/// library presets strip still makes them, and their pins and saved filters keep working.
/// </summary>
public static class RetiredCollections
{
	/// <summary>The kinds whose pins and saved filters go.</summary>
	public static IReadOnlySet<SmartCollectionKind> Gone { get; } = new HashSet<SmartCollectionKind>
	{
		SmartCollectionKind.Director, SmartCollectionKind.Actor, SmartCollectionKind.Studio, SmartCollectionKind.Network,
	};

	// their ids' first part, as the builders make them ("director:clarafilm", "studio:a24"); a preset's carries "nixie:" first
	private static readonly string[] Prefixes = ["director:", "actor:", "studio:", "network:"];

	/// <summary>Whether a collection's id (or a preset's, "nixie:director:…") is one of a kind that <see cref="Gone"/>.</summary>
	public static bool IsGone(string? id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return false;
		}

		var own = id.StartsWith(NixiePreset.IdPrefix, StringComparison.Ordinal) ? id[NixiePreset.IdPrefix.Length..] : id;
		return Prefixes.Any(p => own.StartsWith(p, StringComparison.Ordinal));
	}

	/// <summary>
	/// The pins and saved filters of the kinds that <see cref="Gone"/> taken out of <paramref name="store"/> (the signed-in
	/// user's); how many went. Never throws: whatever fails leaves them for the next time (they show nowhere meanwhile).
	/// </summary>
	public static async Task<int> ForgetAsync(SmartCollectionStore? store, ILogger? log = null)
	{
		if (store is null)
		{
			return 0;
		}

		try
		{
			// 1.7.5 (fx1; M2's review of 1.7.3): one locked store call, the file written once; Home's call and the Collections
			// page's at the same time count each pin once between them, so the line is said once
			var gone = await store.RemoveWhere(IsGone).ConfigureAwait(false);
			if (gone > 0)
			{
				log?.LogInformation("Made by Nixie: {Count} pinned or saved collections of directors, actors, studios or networks were removed (Nixie no longer makes them)", gone);
			}

			return gone;
		}
		catch (Exception ex)
		{
			log?.LogWarning("Made by Nixie: the pins and saved filters of directors, actors, studios or networks couldn't be removed ({Error}); they show nowhere", ex.GetType().Name);
			return 0;
		}
	}
}
// end 1.7.3
