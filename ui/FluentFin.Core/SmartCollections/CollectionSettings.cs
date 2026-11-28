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

	/// <summary>1.9 (wire's review, Minor 9): the categories' one-time step ran (<see cref="CollectionSettings.Migrate"/>).</summary>
	public static Key<bool> CategoriesSettled { get; } = new("CollectionsCategoriesSettled", false);
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

	// 1.9 (wire's review, Minor 9)
	/// <summary>1.9: the kinds 1.8.1 had a switch for (every one off was Made by Nixie off).</summary>
	public static IReadOnlyList<SmartCollectionKind> Kinds181 { get; } =
	[
		SmartCollectionKind.Franchise, SmartCollectionKind.Keyword, SmartCollectionKind.HighlyRated, SmartCollectionKind.UnfinishedSeries,
		SmartCollectionKind.Seasonal, SmartCollectionKind.ShortFilms, SmartCollectionKind.NextIn, SmartCollectionKind.MoreLike,
		SmartCollectionKind.MoreFrom, SmartCollectionKind.Spotlight,
	];

	/// <summary>
	/// 1.9, at start, once: a user who turned every Made by Nixie kind off in 1.8.1 keeps Made by Nixie off. The categories are a
	/// new kind, on by default (the off-list names the kinds turned off), so they came on after the update and the Collections
	/// page stopped saying every kind is off. With every 1.8.1 kind off, Categories goes off too. It runs once (a note on this
	/// PC): a user who turns Categories on afterwards, everything else off, keeps it on.
	/// <para>
	/// The review of 1.9's ui (Minor 1): it runs unguarded at start, so a setting of the wrong kind ("CollectionsKindsOff": 5,
	/// "CollectionsCategoriesSettled": "yes") or a settings file that can't be written stopped Nixie from starting, every time.
	/// Such a failure is said (its type only) and the step is left for the next start, not marked done.
	/// </para>
	/// </summary>
	public static void Migrate(ILocalSettingsService settings, ILogger? log = null)
	{
		try
		{
			if (settings.ReadSetting(CollectionSettingKeys.CategoriesSettled))
			{
				return;
			}

			if (Kinds181.All(KindsOff(settings).Contains))
			{
				Set(settings, CollectionSettingKeys.KindsOff, SmartCollectionKind.MicroGenre, on: false);
			}

			settings.SaveSetting(CollectionSettingKeys.CategoriesSettled, true);
		}
		catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
		{
			log?.LogWarning("Collections: the categories' one-time step couldn't read or write the settings ({Error}); it runs again at the next start", ex.GetType().Name);
		}
	}
	// end 1.9

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
			Row(NixieHomeRow.UnfinishedSeries, "Unfinished series", "Shows you watched three or more episodes of, then none for a month."), // 1.8.1 (fix 5)
			Row(NixieHomeRow.Seasonal, "In season", Builders.SeasonalBuilder.SeasonsText), // 1.8.1 (fix 4); 1.9: and Thanksgiving
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
				// 1.9 (the pre-release nit): not for the categories, which Home never shows (HomeViewModel.HomeBuildOff): a stale Home
				// asks the server for its page again for nothing; the Collections page sees the switch itself (KindsOffNow)
				if (kind != SmartCollectionKind.MicroGenre)
				{
					_homeCache?.MarkStale();
				}

				if (CollectionSettings.PeopleKinds.Contains(kind))
				{
					ViewModels.SmartCollectionServices.Index?.PeopleSwitched();
				}

				// 1.9 (the text stream's review, Major 1; rule 9): the categories' switch turns the shows' summaries pass off and on
				// at once: off cancels it and deletes the matches (in memory and LibraryIndex\Summaries), on reads them now
				if (kind == TextPass.Kind)
				{
					TextPass.App?.Switched();
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
		// 1.9 (Collections 2): what the categories are, and that they are matched on this PC (nothing is sent anywhere)
		SmartCollectionKind.MicroGenre => CategoriesText,
		_ => "",
	};

	/// <summary>1.9 (Collections 2): the Categories switch's words.</summary>
	public const string CategoriesText = "Films or shows that share a genre and a decade, a country or a subject, such as crime dramas from the 1970s. "
		+ "Matched on this PC from the server's own data. Off, the Collections page and This week show none.";

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
