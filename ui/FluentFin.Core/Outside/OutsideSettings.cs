using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;

namespace FluentFin.Core.Outside;

/// <summary>1.7: a link in a source's "What is sent" card (the service's own page).</summary>
public sealed record OutsideLink(string Text, Uri Uri);

/// <summary>
/// 1.7: one source's switch in Settings &gt; Look up lists online: its name (the toggle's Narrator name too), a one-line
/// description, its "What is sent" card and links, and a status line: Nixie's own words for the last refusal, or when its
/// lists were last fetched.
/// </summary>
public sealed partial class OutsideSourceSwitch : ObservableObject
{
	private readonly OutsideHub _hub;

	public OutsideSourceSwitch(OutsideHub hub, OutsideSourceId id, string name, string description, string whatIsSent, IReadOnlyList<OutsideLink> links)
	{
		_hub = hub;
		Id = id;
		Name = name;
		Description = description;
		WhatIsSent = whatIsSent;
		Links = links;
	}

	public OutsideSourceId Id { get; }

	public string Name { get; }

	public string Description { get; }

	/// <summary>The "What is sent" card's text: what leaves the PC and what never does.</summary>
	public string WhatIsSent { get; }

	public IReadOnlyList<OutsideLink> Links { get; }

	/// <summary>The first link (the XAML's one link button).</summary>
	public OutsideLink Link => Links[0];

	/// <summary>The status card's Narrator name: "Wikidata status".</summary>
	public string StatusName => $"{Name} status";

	public bool IsOn
	{
		get => _hub.IsOn(Id);
		set
		{
			if (value != _hub.IsOn(Id))
			{
				_hub.SetOn(Id, value);
				OnPropertyChanged();
				OnPropertyChanged(nameof(Status));
			}
		}
	}

	/// <summary>What the source last did, in Nixie's words: off, not fetched yet, the last refusal, or the lists' age.</summary>
	public string Status => OutsideStatus.Describe(_hub, Id);

	/// <summary>The hub's sources answered: the status line follows.</summary>
	public void Refresh() => OnPropertyChanged(nameof(Status));
}

/// <summary>1.7: a source's status line in Settings.</summary>
public static class OutsideStatus
{
	public static string Describe(OutsideHub hub, OutsideSourceId id, DateTimeOffset? now = null)
	{
		if (!hub.IsOn(id))
		{
			return "Off. Nothing is sent.";
		}

		var source = hub[id];
		// the last refusal in Nixie's words (a pause after a run of refusals says why it began); a 429's pause says when
		if (source.Last is { Ok: false, Message: { } message })
		{
			return message;
		}

		if (source.PausedUntil is { } paused)
		{
			return OutsideMessages.Paused(source.Info, paused, now ?? DateTimeOffset.UtcNow);
		}

		return source.Last is { Ok: true } ? "Lists up to date." : "On. Nixie fetches its lists when they are needed and keeps them on this PC.";
	}
}

/// <summary>
/// 1.7: Settings &gt; Look up lists online: stream K's two sources (TMDb export files and Wikidata). Stream T adds Trakt and
/// TMDb trending in the marked place of SettingsPage.xaml, with its own view model.
/// </summary>
public sealed partial class OutsideSourcesSettingsViewModel : ObservableObject
{
	private readonly Action<Action> _post;
	private readonly ILocalSettingsService? _settings;
	private bool _attached;

	/// <summary>
	/// <paramref name="post"/> runs a status update on the page's thread (the sources answer on others); <paramref name="settings"/>
	/// keeps Wikidata's endpoint.
	/// </summary>
	public OutsideSourcesSettingsViewModel(OutsideHub hub, Action<Action>? post = null, ILocalSettingsService? settings = null)
	{
		Hub = hub;
		_settings = settings;
		_post = post ?? (a => a());
		TmdbExports = new OutsideSourceSwitch(hub, OutsideSourceId.TmdbExports,
			"Franchise and keyword names from TMDb",
			"Names your films' franchises (“The Mummy Collection”) and keywords. A public file, fetched once a week. Off by default.",
			"Sent: a request for TMDb's public file of the day (the same file for everyone), and Nixie's name and version. Not sent: the server you use, your account, libraries or anything you watch. Nixie matches the names on this PC. " + OutsideKeep.Describe(OutsideSourceId.TmdbExports),
			[new OutsideLink("TMDb", new Uri("https://www.themoviedb.org/"))]);
		Wikidata = new OutsideSourceSwitch(hub, OutsideSourceId.Wikidata,
			"Awards and genres from Wikidata",
			"Best Picture, Palme d'Or and other award lists, and genres like heist and slasher films. Fetched once a week. Off by default.",
			"Sent: fixed questions for each public list (“every Best Picture winner”, the same for everyone), and Nixie's name and version. Not sent: the server you use, your account, libraries or anything you watch. Nixie matches the lists on this PC. " + OutsideKeep.Describe(OutsideSourceId.Wikidata),
			[new OutsideLink("Wikidata", new Uri("https://www.wikidata.org/"))]);
		Switches = [TmdbExports, Wikidata];
		Attach();
	}

	/// <summary>Follows the sources' answers (the page's Loaded; the constructor attaches too).</summary>
	public void Attach()
	{
		if (!_attached)
		{
			_attached = true;
			Hub.Answered += OnHubChanged;
			Hub.SwitchChanged += OnHubChanged;
			Refresh();
		}
	}

	/// <summary>Stops following them (the page's Unloaded): the hub lives as long as the app, the page doesn't.</summary>
	public void Detach()
	{
		if (_attached)
		{
			_attached = false;
			Hub.Answered -= OnHubChanged;
			Hub.SwitchChanged -= OnHubChanged;
		}
	}

	private void OnHubChanged(OutsideSourceId id) => _post(Refresh);

	public OutsideHub Hub { get; }

	public OutsideSourceSwitch TmdbExports { get; }

	public OutsideSourceSwitch Wikidata { get; }

	public IReadOnlyList<OutsideSourceSwitch> Switches { get; }

	// ---------- Wikidata's query endpoint (its service moves to a new backend: available through June 2027)

	/// <summary>The endpoints Settings offers, with what each one is.</summary>
	public static IReadOnlyList<WikidataEndpointChoice> WikidataEndpoints { get; } =
	[
		new(WikidataEndpoint.Wikidata, "query.wikidata.org", "Wikidata's query service, the default. Available through June 2027; it may slow down from February 2027."),
		new(WikidataEndpoint.Next, "query-next.wikidata.org", "Wikidata's new query service. In early access it refuses requests until it opens to everyone."),
		new(WikidataEndpoint.QLever, "QLever (University of Freiburg)", "A public copy of Wikidata's data on the QLever engine, run by the University of Freiburg."),
		new(WikidataEndpoint.Custom, "Another address", "An endpoint you choose, at an https address with no user name or password in it."),
	];

	public WikidataEndpointChoice SelectedWikidataEndpoint
	{
		get => WikidataEndpoints.FirstOrDefault(x => x.Value.ToString() == _settings?.ReadSetting(SettingKeys.WikidataEndpoint)) ?? WikidataEndpoints[0];
		set
		{
			if (value is not null && _settings is not null && value != SelectedWikidataEndpoint)
			{
				_settings.SaveSetting(SettingKeys.WikidataEndpoint, value.Value.ToString());
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsCustomEndpoint));
				OnPropertyChanged(nameof(WikidataCustomNote));
			}
		}
	}

	/// <summary>"Another address" is chosen: its address box shows.</summary>
	public bool IsCustomEndpoint => SelectedWikidataEndpoint.Value == WikidataEndpoint.Custom;

	public string WikidataCustomUrl
	{
		get => _settings?.ReadSetting(SettingKeys.WikidataCustomEndpoint) ?? "";
		set
		{
			var text = (value ?? "").Trim();
			if (_settings is not null && text != WikidataCustomUrl)
			{
				_settings.SaveSetting(SettingKeys.WikidataCustomEndpoint, text);
				OnPropertyChanged();
				OnPropertyChanged(nameof(WikidataCustomNote));
			}
		}
	}

	/// <summary>Under the address box: what is wrong with it, or that it is used; never a silent fallback.</summary>
	public string WikidataCustomNote => WikidataLists.CheckCustom(WikidataCustomUrl, out _) is { } why
		? why + " Until then, Nixie asks query.wikidata.org."
		: "Nixie asks this address for Wikidata's lists.";

	private void Refresh()
	{
		foreach (var item in Switches)
		{
			item.Refresh();
		}
	}
}

/// <summary>1.7: the credits Settings &gt; About shows for the outside lists, in the words the sources' terms ask for.</summary>
public static class OutsideCredits
{
	/// <summary>TMDb's API terms' notice (the longer wording, the terms re-check of 2026-11-21), with the app's name.</summary>
	public const string TmdbNotice = AppIdentity.Name + " uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.";

	/// <summary>Wikidata's credit (CC0: appreciated, not required).</summary>
	public const string WikidataCredit = "Awards and genres from Wikidata, under CC0.";

	public static Uri TmdbUri { get; } = new("https://www.themoviedb.org/");

	public static Uri WikidataUri { get; } = new("https://www.wikidata.org/");
}

/// <summary>1.7: one of Settings' Wikidata endpoints.</summary>
public sealed record WikidataEndpointChoice(WikidataEndpoint Value, string Name, string Description);
