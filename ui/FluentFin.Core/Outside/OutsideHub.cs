using System.Net;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7: the app's outside sources in one place: one <see cref="OutsideSource"/> per source (each with its own pause and its
/// one-at-a-time gate), the shared cache of public lists, the routing (real hosts, or the harness's fake), and the switches.
/// Nothing is sent for a source that is off (<see cref="IsOn"/>), and what is sent never depends on the library (plan §3).
/// </summary>
public sealed class OutsideHub
{
	private readonly Dictionary<OutsideSourceId, OutsideSource> _sources;
	private readonly ILocalSettingsService? _settings;

	/// <param name="infos">The sources' rules (tests: a shorter timeout); <see cref="OutsideSources.All"/> by default.</param>
	public OutsideHub(HttpClient http, OutsideRouting routing, string version, OutsideCache cache, ILocalSettingsService? settings, Func<DateTimeOffset>? clock = null,
		IEnumerable<OutsideSourceInfo>? infos = null)
	{
		Routing = routing;
		Cache = cache;
		_settings = settings;
		_sources = OutsideSources.All.Select(x => infos?.FirstOrDefault(i => i.Id == x.Id) ?? x).ToDictionary(x => x.Id, x => new OutsideSource(x, http, routing, version, clock));
		foreach (var source in _sources.Values)
		{
			source.Answered += (s, _) => Answered?.Invoke(s.Info.Id);
			source.Noted += note => Noted?.Invoke(note);
		}
	}

	/// <summary>
	/// The client the app's sources share: gzip is undone by <see cref="OutsideSource"/> itself (so a test's handler sees the
	/// real header), no redirects followed by the client (<see cref="OutsideSource"/> checks each address before it follows one),
	/// no cookies, and no overall timeout (each source has its own).
	/// </summary>
	public static HttpClient CreateHttpClient() => new(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

	/// <summary>The handler under <see cref="CreateHttpClient"/>.</summary>
	public static SocketsHttpHandler CreateHandler() => new()
	{
		AutomaticDecompression = DecompressionMethods.None,
		AllowAutoRedirect = false,
		UseCookies = false,
		PooledConnectionLifetime = TimeSpan.FromMinutes(10),
		ConnectTimeout = TimeSpan.FromSeconds(20),
	};

	public OutsideRouting Routing { get; }

	public OutsideCache Cache { get; }

	public OutsideSource this[OutsideSourceId id] => _sources[id];

	public IEnumerable<OutsideSource> Sources => _sources.Values;

	/// <summary>Whether the user turned <paramref name="id"/> on in Settings &gt; Look up lists online (all off by default).</summary>
	public bool IsOn(OutsideSourceId id) => _settings is not null && _settings.ReadSetting(OutsideSettingKeys.For(id));

	/// <summary>Turns a source on or off (Settings), then raises <see cref="SwitchChanged"/>.</summary>
	public void SetOn(OutsideSourceId id, bool on)
	{
		if (_settings is null || IsOn(id) == on)
		{
			return;
		}

		_settings.SaveSetting(OutsideSettingKeys.For(id), on);
		SwitchChanged?.Invoke(id);
	}

	/// <summary>A source was turned on or off.</summary>
	public event Action<OutsideSourceId>? SwitchChanged;

	/// <summary>A source answered (a list, or a refusal: its <see cref="OutsideSource.Last"/>).</summary>
	public event Action<OutsideSourceId>? Answered;

	/// <summary>Log lines (never a query, a key or anything of the user's).</summary>
	public event Action<string>? Noted;
}

/// <summary>1.7: the switch of each source (<see cref="SettingKeys"/>' "1.7 keyless" block).</summary>
public static class OutsideSettingKeys
{
	public static Key<bool> For(OutsideSourceId id) => id switch
	{
		OutsideSourceId.TmdbExports => SettingKeys.LookUpTmdbExports,
		OutsideSourceId.Wikidata => SettingKeys.LookUpWikidata,
		OutsideSourceId.Trakt => SettingKeys.LookUpTrakt,
		OutsideSourceId.TmdbTrending => SettingKeys.LookUpTmdbTrending,
		_ => throw new ArgumentOutOfRangeException(nameof(id)),
	};
}
