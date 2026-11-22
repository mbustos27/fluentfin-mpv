using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;

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
	Rotating,
}

public static class CollectionSettings
{
	/// <summary>At start, and when the switch changes: the names follow "Tidy collection names".</summary>
	public static void Apply(ILocalSettingsService settings) => CollectionNames.Tidy = !settings.ReadSetting(CollectionSettingKeys.KeepServerNames);

	public static IReadOnlySet<SmartCollectionKind> KindsOff(ILocalSettingsService? settings) =>
		Parse<SmartCollectionKind>(settings?.ReadSetting(CollectionSettingKeys.KindsOff));

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

/// <summary>One switch of Settings → Collections ("Franchises", "Highly rated, not watched").</summary>
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
			Row(NixieHomeRow.HighlyRated, "Highly rated, not watched", "Titles rated 7.5 or more on your server that you haven't watched."),
			Row(NixieHomeRow.UnfinishedSeries, "Unfinished series", "Shows you started and haven't watched for a month."),
			Row(NixieHomeRow.Seasonal, "In season", "Halloween in October, Christmas in December."),
			Row(NixieHomeRow.Rotating, "A collection a day", "One of your Made by Nixie collections, a different one each day."),
		];
		// 1.7 keyless: not the awards: they come from Wikidata, whose switch is in Look up lists online ("from your server's own data")
		Kinds = [.. SmartCollectionKinds.PageOrder.Concat(SmartCollectionKinds.Picks).Where(kind => kind != SmartCollectionKind.Award).Select(kind => new CollectionSwitch(SmartCollectionKinds.Plural(kind), "",
			() => !CollectionSettings.KindsOff(settings).Contains(kind),
			on =>
			{
				CollectionSettings.Set(settings, CollectionSettingKeys.KindsOff, kind, on);
				_homeCache?.MarkStale();
			}))];
	}

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
