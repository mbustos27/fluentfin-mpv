using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluentFin.Core.Mpv;

/// <summary>1.6 player: the <c>get_stats</c> result (docs/BRIDGE.md §4); every field but the two flags may be null.</summary>
public sealed record BridgeStats(
	string? ItemId = null,
	string? PlayMethod = null,
	IReadOnlyList<string>? TranscodeReasons = null,
	string? Container = null,
	string? StreamFormat = null,
	string? VideoCodec = null,
	string? VideoProfile = null,
	string? StreamVideoCodec = null,
	int? SourceWidth = null,
	int? SourceHeight = null,
	int? Width = null,
	int? Height = null,
	double? FrameRate = null,
	string? Hdr = null,
	string? HardwareDecoding = null,
	long? DroppedFrames = null,
	long? DelayedFrames = null,
	string? AudioCodec = null,
	int? AudioChannels = null,
	int? AudioSampleRate = null,
	string? AudioTrack = null,
	string? SubtitleFormat = null,
	bool SubtitlesOff = false,
	long? StreamBitrate = null,
	long? MeasuredBitrate = null,
	double? BufferSeconds = null,
	long? BufferBytes = null,
	long? NetworkSpeed = null,
	bool IsLocal = true,
	string? MpvVersion = null);

/// <summary>One labelled row of the Playback stats panel; <see cref="Name"/> is what Narrator reads.</summary>
public sealed record PlaybackStatsRow(string Label, string Value)
{
	public string Name => $"{Label}: {Value}";
}

/// <summary>A row as the panel shows it: its label fixed, its value changed in place as the stats come in.</summary>
public sealed partial class PlaybackStatsRowView(PlaybackStatsRow row) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
	public string Label { get; } = row.Label;

	public string Value
	{
		get => field;
		set
		{
			if (SetProperty(ref field, value))
			{
				OnPropertyChanged(nameof(Name));
			}
		}
	} = row.Value;

	/// <summary>"Container: MKV".</summary>
	public string Name => $"{Label}: {Value}";
}

/// <summary>
/// 1.6 player: "Playback stats" (Nixie's own name; the owner: "a little activatable for nerds window like in youtube"): the
/// rows the panel shows, in plain words, and the text its Copy button puts on the clipboard, which never carries the access
/// token, the server address's credentials or an <c>ApiKey=</c>, whatever the backend sent.
/// </summary>
public static partial class PlaybackStats
{
	public const string Unknown = "Unknown";

	/// <summary>The panel's rows for one <c>get_stats</c> answer, always the same rows in the same order.</summary>
	public static IReadOnlyList<PlaybackStatsRow> Rows(BridgeStats stats)
	{
		var rows = new List<PlaybackStatsRow>
		{
			new("Playing", QualityChoices.MethodText(stats.PlayMethod) ?? Unknown),
		};
		if (stats.PlayMethod is "Transcode" or "DirectStream")
		{
			rows.Add(new("Why", QualityChoices.Reasons(stats.TranscodeReasons) ?? "The server gave no reason"));
		}

		// the file's container, then what mpv reads when that differs (a transcode's HLS)
		var streamFormat = string.Equals(Upper(stats.StreamFormat), Upper(stats.Container), StringComparison.Ordinal) ? null : Upper(stats.StreamFormat);
		rows.Add(new("Container", Join(" → ", Upper(stats.Container), streamFormat) ?? Unknown));
		// the file's codec and profile, then what mpv decodes when that differs (a transcode's H264 from an HEVC file)
		var decoded = string.Equals(Upper(stats.StreamVideoCodec), Upper(stats.VideoCodec), StringComparison.Ordinal) ? null : Upper(stats.StreamVideoCodec);
		rows.Add(new("Video", Join(" → ", Join(" ", Upper(stats.VideoCodec), stats.VideoProfile), decoded) ?? Unknown));
		rows.Add(new("Resolution", Resolution(stats) ?? Unknown));
		rows.Add(new("Frame rate", stats.FrameRate is double fps && fps > 0 ? string.Create(CultureInfo.InvariantCulture, $"{fps:0.###} fps") : Unknown));
		rows.Add(new("HDR", stats.Hdr ?? Unknown));
		rows.Add(new("Hardware decoding", stats.HardwareDecoding is { Length: > 0 } decoder ? $"On ({decoder})" : "Off"));
		rows.Add(new("Frames", string.Create(CultureInfo.InvariantCulture, $"{Count(stats.DroppedFrames)} dropped · {Count(stats.DelayedFrames)} delayed")));
		rows.Add(new("Audio", Join(" · ", Upper(stats.AudioCodec), Channels(stats.AudioChannels), SampleRate(stats.AudioSampleRate)) ?? Unknown));
		rows.Add(new("Audio track", stats.AudioTrack ?? Unknown));
		rows.Add(new("Subtitles", stats.SubtitlesOff ? "Off" : Upper(stats.SubtitleFormat) ?? Unknown));
		rows.Add(new("Server bitrate", Bitrate(stats.StreamBitrate) ?? Unknown));
		rows.Add(new("Measured bitrate", Bitrate(stats.MeasuredBitrate) ?? Unknown));
		rows.Add(new("Buffer", Join(" · ", stats.BufferSeconds is double seconds ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0} s") : null, Bytes(stats.BufferBytes)) ?? Unknown));
		rows.Add(new("Network speed", Bitrate(stats.NetworkSpeed is long speed ? speed * 8 : null) ?? Unknown));
		rows.Add(new("Server", stats.IsLocal ? "At home" : "Away"));
		rows.Add(new("mpv", stats.MpvVersion ?? Unknown));
		return rows;
	}

	/// <summary>The rows before the first answer, or when the backend has none to give.</summary>
	public static IReadOnlyList<PlaybackStatsRow> Waiting { get; } = [new("Playing", "Reading the stats…")];

	public static IReadOnlyList<PlaybackStatsRow> NotAvailable { get; } = [new("Playing", "This player can't show stats for what's playing.")];

	/// <summary>The answer of <c>get_stats</c>, null when it is not one.</summary>
	public static BridgeStats? Parse(JsonNode? result)
	{
		if (result is not JsonObject)
		{
			return null;
		}

		try
		{
			return result.Deserialize<BridgeStats>(Bridge.JsonOptions);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string? Join(string separator, params string?[] parts)
	{
		var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
		return present.Count == 0 ? null : string.Join(separator, present);
	}

	private static string? Upper(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

	private static string Count(long? value) => value is long n && n >= 0 ? n.ToString(CultureInfo.InvariantCulture) : "?";

	private static string? Resolution(BridgeStats stats)
	{
		var source = Size(stats.SourceWidth, stats.SourceHeight);
		var output = Size(stats.Width, stats.Height);
		return source is null ? output : output is null || output == source ? source : $"{source} → {output}";
	}

	private static string? Size(int? width, int? height) =>
		width is int w && height is int h && w > 0 && h > 0 ? string.Create(CultureInfo.InvariantCulture, $"{w}×{h}") : null;

	/// <summary>"5.1", "7.1", "Stereo", "Mono", "3 channels".</summary>
	public static string? Channels(int? channels) => channels switch
	{
		null or <= 0 => null,
		1 => "Mono",
		2 => "Stereo",
		6 => "5.1",
		8 => "7.1",
		int n => string.Create(CultureInfo.InvariantCulture, $"{n} channels"),
	};

	private static string? SampleRate(int? hertz) => hertz is int hz && hz > 0
		? string.Create(CultureInfo.InvariantCulture, $"{hz / 1000.0:0.#} kHz")
		: null;

	/// <summary>Bits per second: "7.9 Mbps", "380 kbps".</summary>
	public static string? Bitrate(long? bitsPerSecond)
	{
		if (bitsPerSecond is not long bps || bps <= 0)
		{
			return null;
		}

		return bps >= 1_000_000
			? string.Create(CultureInfo.InvariantCulture, $"{bps / 1_000_000.0:0.0} Mbps")
			: string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (long)Math.Round(bps / 1000.0))} kbps");
	}

	/// <summary>"8.6 MB", "640 KB".</summary>
	public static string? Bytes(long? bytes)
	{
		if (bytes is not long b || b < 0)
		{
			return null;
		}

		return b >= 1_000_000
			? string.Create(CultureInfo.InvariantCulture, $"{b / 1_000_000.0:0.0} MB")
			: string.Create(CultureInfo.InvariantCulture, $"{Math.Round(b / 1000.0):0} KB");
	}

	// -- Copy ----------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The Copy button's text: a title line, then "Label: value" per row. Scrubbed (<see cref="Scrub"/>) with the session's
	/// access token and server address, so a bug report made from it never carries either.
	/// </summary>
	public static string CopyText(IReadOnlyList<PlaybackStatsRow> rows, string? appVersion = null, string? accessToken = null, string? serverUrl = null)
	{
		var text = new StringBuilder();
		text.Append(AppIdentity.Name).Append(" playback stats");
		if (!string.IsNullOrWhiteSpace(appVersion))
		{
			text.Append(' ').Append(appVersion.Trim());
		}

		text.Append('\n');
		foreach (var row in rows)
		{
			text.Append(row.Label).Append(": ").Append(row.Value).Append('\n');
		}

		return Scrub(text.ToString(), accessToken, serverUrl);
	}

	/// <summary>
	/// Text with no secret left: the token itself (also URL-encoded), anything <see cref="BridgeRedaction.Redact"/> knows
	/// (<c>api_key=</c>, <c>ApiKey=</c>, <c>X-Emby-Token</c>, <c>Authorization</c>), any address with a user or a password in
	/// it, and the server address's own credentials wherever they appear. Addresses lose their query.
	/// </summary>
	public static string Scrub(string text, string? accessToken = null, string? serverUrl = null)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}

		// case-blind: a row may show a value upper-cased (the codecs, the container)
		foreach (var secret in Secrets(accessToken, serverUrl))
		{
			text = text.Replace(secret, BridgeRedaction.Mask, StringComparison.OrdinalIgnoreCase);
		}

		text = BridgeRedaction.Redact(text);
		text = UrlUserInfo().Replace(text, m => m.Groups["scheme"].Value + BridgeRedaction.Mask + "@");
		text = UrlQuery().Replace(text, m => m.Groups["address"].Value);
		text = ApiKeyAnywhere().Replace(text, m => m.Groups["key"].Value + "=" + BridgeRedaction.Mask);
		return text;
	}

	private static IEnumerable<string> Secrets(string? accessToken, string? serverUrl)
	{
		var secrets = new List<string>();
		if (!string.IsNullOrWhiteSpace(accessToken) && accessToken.Length >= 6)
		{
			secrets.Add(accessToken);
			secrets.Add(Uri.EscapeDataString(accessToken));
		}

		if (!string.IsNullOrWhiteSpace(serverUrl) && Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
		{
			// the whole user info and the password (not the user name alone: it may be an ordinary word in a title)
			secrets.Add(uri.UserInfo);
			secrets.Add(Uri.UnescapeDataString(uri.UserInfo));
			if (uri.UserInfo.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 && uri.UserInfo[(colon + 1)..] is { Length: >= 3 } password)
			{
				secrets.Add(password);
				secrets.Add(Uri.UnescapeDataString(password));
			}
		}

		return secrets.Distinct().OrderByDescending(s => s.Length);
	}

	[GeneratedRegex("""(?<scheme>\b[a-z][a-z0-9+.-]*://)[^/@\s]+@""", RegexOptions.IgnoreCase)]
	private static partial Regex UrlUserInfo();

	[GeneratedRegex("""(?<address>\b[a-z][a-z0-9+.-]*://[^\s?#]+)[?#][^\s]*""", RegexOptions.IgnoreCase)]
	private static partial Regex UrlQuery();

	[GeneratedRegex("""(?<key>\b(?:api_?key|access_?token|x-emby-token|token))\s*=\s*[^&\s'",;]+""", RegexOptions.IgnoreCase)]
	private static partial Regex ApiKeyAnywhere();
}

/// <summary>
/// 1.6 player: when the Playback stats panel asks the backend (<c>get_stats</c>): about once a second while it is open,
/// never while it is closed, one question at a time. Driven by the controls' heartbeat with the clock it is given, so a test
/// steps through it without a timer.
/// </summary>
public sealed class PlaybackStatsPoller(Func<Task<JsonNode?>> ask, Func<DateTimeOffset> now)
{
	/// <summary>How often the open panel asks.</summary>
	public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

	private DateTimeOffset _askedAt = DateTimeOffset.MinValue;
	private bool _inFlight;
	private int _openings;

	public bool IsOpen { get; private set; }

	/// <summary>How many times the backend was asked, ever (the tests' proof that a closed panel reads nothing).</summary>
	public int Asked { get; private set; }

	/// <summary>The latest rows: <see cref="PlaybackStats.Waiting"/> until the first answer.</summary>
	public IReadOnlyList<PlaybackStatsRow> Rows { get; private set; } = PlaybackStats.Waiting;

	/// <summary>New rows arrived (on the caller's context).</summary>
	public event EventHandler? RowsChanged;

	/// <summary>Opened: asks at once. Closed: no question is sent again, and an answer still on its way is dropped.</summary>
	public void SetOpen(bool open)
	{
		if (open == IsOpen)
		{
			return;
		}

		IsOpen = open;
		_openings++;
		if (open)
		{
			Rows = PlaybackStats.Waiting;
			RowsChanged?.Invoke(this, EventArgs.Empty);
			_askedAt = DateTimeOffset.MinValue;
			_ = Poll();
		}
	}

	/// <summary>The heartbeat: asks when open, a second or more since the last question, and none is on its way.</summary>
	public Task Poll()
	{
		if (!IsOpen || _inFlight || now() - _askedAt < Every)
		{
			return Task.CompletedTask;
		}

		return AskOnce();
	}

	private async Task AskOnce()
	{
		_inFlight = true;
		_askedAt = now();
		Asked++;
		var opening = _openings;
		JsonNode? answer = null;
		var failed = false;
		try
		{
			answer = await ask();
		}
		catch (Exception)
		{
			failed = true;
		}
		finally
		{
			_inFlight = false;
		}

		if (!IsOpen || opening != _openings)
		{
			return;     // closed (or closed and opened again) meanwhile: not this opening's answer
		}

		if (!failed && PlaybackStats.Parse(answer) is { } stats)
		{
			Rows = PlaybackStats.Rows(stats);
		}
		else if (ReferenceEquals(Rows, PlaybackStats.Waiting))
		{
			// an older backend (unknown_cmd), or nothing playing: said once; later misses (between two items) keep the last rows
			Rows = PlaybackStats.NotAvailable;
		}
		else
		{
			return;
		}

		RowsChanged?.Invoke(this, EventArgs.Empty);
	}
}
