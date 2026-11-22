using System.Globalization;
using System.Text.Json.Nodes;

namespace FluentFin.Core.Mpv;

/// <summary>
/// 1.6 player: one quality for the player's Quality menu and Settings &gt; Playback (docs/BRIDGE.md §4 <c>set_quality</c>):
/// <see cref="Auto"/> (no choice: the backend's own settings, today's behaviour and the default), <see cref="Original"/>
/// (no cap: the file as it is), or a cap in bits per second, which the server compares with the file as jellyfin-web's
/// quality menu does (a file under it still plays directly).
/// </summary>
public sealed record QualityChoice(long? Bitrate, bool IsOriginal = false)
{
	public static QualityChoice Auto { get; } = new(null, false);

	public static QualityChoice Original { get; } = new(null, true);

	public bool IsAuto => Bitrate is null && !IsOriginal;

	public bool IsCap => Bitrate is not null;

	/// <summary>"Auto", "Original", "8 Mbps", "1.5 Mbps".</summary>
	public string Label => IsOriginal ? "Original" : Bitrate is long bitrate ? QualityChoices.Mbps(bitrate) : "Auto";

	/// <summary><c>set_quality</c>'s args, and one side of <c>set_quality_defaults</c>.</summary>
	public JsonObject ToJson() => new() { ["bitrate"] = Bitrate, ["original"] = IsOriginal };

	/// <summary>How Settings keeps it: "auto", "original" or the cap in bits per second.</summary>
	public string ToSetting() => IsOriginal ? "original" : Bitrate is long bitrate ? bitrate.ToString(CultureInfo.InvariantCulture) : "auto";

	/// <summary>A kept value back; anything not one of the offered choices (a hand edit, an older build's) reads Auto.</summary>
	public static QualityChoice FromSetting(string? value)
	{
		if (string.Equals(value, "original", StringComparison.OrdinalIgnoreCase))
		{
			return Original;
		}

		return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bitrate)
			&& QualityChoices.Caps.Any(cap => cap.Bitrate == bitrate)
			? new QualityChoice(bitrate)
			: Auto;
	}

	/// <summary>The choice a <c>stream</c> event says is in effect; Auto when it says nothing.</summary>
	public static QualityChoice From(BridgeQuality? quality) =>
		quality is null ? Auto : quality.Original ? Original : quality.Bitrate is long bitrate && bitrate > 0 ? new QualityChoice(bitrate) : Auto;
}

/// <summary>1.6 player: the <c>stream</c> event's choice in effect (docs/BRIDGE.md §5).</summary>
public sealed record BridgeQuality(long? Bitrate, bool Original);

/// <summary>
/// 1.6 player: the <c>stream</c> event (docs/BRIDGE.md §5): how the item is served (the play method, the server's reasons,
/// the bitrate sent) and the file's own bitrate and size, with the quality chosen for it and whether its server is at home.
/// </summary>
public sealed record BridgeStream(
	string? ItemId,
	string? PlaySessionId = null,
	string? PlayMethod = null,
	IReadOnlyList<string>? TranscodeReasons = null,
	long? Bitrate = null,
	long? SourceBitrate = null,
	int? SourceWidth = null,
	int? SourceHeight = null,
	BridgeQuality? Quality = null,
	bool IsLocal = true);

/// <summary>One entry of the player's Quality menu: what it reads, what Narrator says, and whether it is in effect.</summary>
public sealed record QualityMenuItem(QualityChoice Choice, string Label, string? Detail, bool IsCurrent)
{
	/// <summary>"8 Mbps, 1080p, current".</summary>
	public string Name => string.Join(", ", new[] { Label, Detail, IsCurrent ? "current" : null }.Where(x => !string.IsNullOrEmpty(x)));

	/// <summary>What Narrator reads for the menu's item (a list item is named after its data).</summary>
	public override string ToString() => Name;
}

/// <summary>
/// 1.6 player: the Quality menu's choices and words (the owner, 2026-11-21: "adjust the quality of the stream adjust bitrate").
/// The caps are Jellyfin's own list, grouped by the picture they suit: only those at or below the file's own size are offered
/// when its size is known.
/// </summary>
public static partial class QualityChoices
{
	/// <summary>A cap and the picture height it suits (2160 for 4K, then 1080, 720 and 480).</summary>
	public sealed record Cap(long Bitrate, int ResolutionClass);

	/// <summary>
	/// The roadmap's list, and jellyfin-web's 720 kbps at the foot of it (its own "480p - 720 kbps"): the one cap under the
	/// bitrate of a small file (a 640 × 360 film of about 1 Mbps), for a connection that can carry little.
	/// </summary>
	public static IReadOnlyList<Cap> Caps { get; } =
	[
		new(120_000_000, 2160), new(80_000_000, 2160), new(60_000_000, 2160),
		new(40_000_000, 1080), new(20_000_000, 1080), new(10_000_000, 1080), new(8_000_000, 1080),
		new(6_000_000, 720), new(4_000_000, 720),
		new(3_000_000, 480), new(1_500_000, 480), new(720_000, 480),
	];

	/// <summary>
	/// The file's size class by its width (a 1920 × 800 scope film is 1080p), else by its height: 2160 (4K), 1080, 720 or
	/// 480; null when neither is known. jellyfin-web's own thresholds: 3800 wide for 4K, 1900 for 1080p, 1260 for 720p.
	/// </summary>
	public static int? ResolutionClass(int? width, int? height)
	{
		if (width is int w && w > 0)
		{
			return w >= 3800 ? 2160 : w >= 1900 ? 1080 : w >= 1260 ? 720 : 480;
		}

		if (height is int h && h > 0)
		{
			return h >= 2000 ? 2160 : h >= 1000 ? 1080 : h >= 700 ? 720 : 480;
		}

		return null;
	}

	/// <summary>"4K", "1080p", "720p", "480p".</summary>
	public static string ClassLabel(int resolutionClass) => resolutionClass >= 2160 ? "4K" : string.Create(CultureInfo.InvariantCulture, $"{resolutionClass}p");

	/// <summary>Auto, Original, then the caps at or below the file's size class (all of them when it is not known).</summary>
	public static IReadOnlyList<QualityChoice> For(int? sourceWidth, int? sourceHeight)
	{
		var size = ResolutionClass(sourceWidth, sourceHeight);
		return [QualityChoice.Auto, QualityChoice.Original, .. Caps.Where(cap => size is not int s || cap.ResolutionClass <= s).Select(cap => new QualityChoice(cap.Bitrate))];
	}

	/// <summary>The menu for what plays now: <see cref="For"/>'s choices, each with its size class, the one in effect marked.</summary>
	public static IReadOnlyList<QualityMenuItem> Menu(BridgeStream? stream)
	{
		var current = QualityChoice.From(stream?.Quality);
		return [.. For(stream?.SourceWidth, stream?.SourceHeight).Select(choice => new QualityMenuItem(choice, choice.Label, DetailOf(choice), choice == current))];
	}

	private static string? DetailOf(QualityChoice choice)
	{
		if (choice.IsAuto)
		{
			return null;
		}

		if (choice.IsOriginal)
		{
			return "the file as it is";
		}

		return Caps.FirstOrDefault(cap => cap.Bitrate == choice.Bitrate) is { } cap ? ClassLabel(cap.ResolutionClass) : null;
	}

	/// <summary>"8 Mbps", "1.5 Mbps", "12 Mbps", "800 kbps".</summary>
	public static string Mbps(long bitsPerSecond)
	{
		if (bitsPerSecond < 1_000_000)
		{
			return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (long)Math.Round(bitsPerSecond / 1000.0))} kbps");
		}

		var mbps = bitsPerSecond / 1_000_000.0;
		return mbps >= 10 || Math.Abs(mbps - Math.Round(mbps)) < 0.05
			? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(mbps):0} Mbps")
			: string.Create(CultureInfo.InvariantCulture, $"{mbps:0.#} Mbps");
	}

	/// <summary>The play method as the menu and the stats say it.</summary>
	public static string? MethodText(string? playMethod) => playMethod switch
	{
		"DirectPlay" => "Direct play",
		"DirectStream" => "Direct stream",
		"Transcode" => "Transcoding",
		_ => null,
	};

	/// <summary>
	/// The Quality menu's header, what plays now: "Direct play · 1080p · 12 Mbps", "Transcoding · 8 Mbps · the file is over
	/// the quality cap". Empty before the item's first <c>stream</c> event.
	/// </summary>
	public static string Header(BridgeStream? stream)
	{
		if (stream is null || MethodText(stream.PlayMethod) is not { } method)
		{
			return "";
		}

		var parts = new List<string> { method };
		if (stream.PlayMethod != "Transcode" && ResolutionClass(stream.SourceWidth, stream.SourceHeight) is int size)
		{
			parts.Add(ClassLabel(size));
		}

		if (stream.Bitrate is long bitrate && bitrate > 0)
		{
			parts.Add(Mbps(bitrate));
		}

		if (stream.PlayMethod != "DirectPlay" && Reason(stream.TranscodeReasons) is { } reason)
		{
			parts.Add(reason);
		}

		return string.Join(" · ", parts);
	}

	/// <summary>
	/// The server's main reason in plain words; null when it gave none. A bitrate reason comes first: under a cap it is the
	/// one that decided (Jellyfin 12.4 also lists the codecs of the transcode's target beside it, seen on the QA server).
	/// </summary>
	public static string? Reason(IReadOnlyList<string>? reasons) =>
		Ordered(reasons).FirstOrDefault() is { } first ? ReasonText(first) : null;

	/// <summary>Every reason the server gave, in plain words, the bitrate first, without repeats.</summary>
	public static string? Reasons(IReadOnlyList<string>? reasons)
	{
		var words = Ordered(reasons).Select(ReasonText).Distinct().ToList();
		return words.Count == 0 ? null : string.Join("; ", words);
	}

	private static IEnumerable<string> Ordered(IReadOnlyList<string>? reasons) =>
		(reasons ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).OrderBy(r => BitrateReasons.Contains(r.Trim()) ? 0 : 1);

	private static readonly HashSet<string> BitrateReasons = ["ContainerBitrateExceedsLimit", "VideoBitrateNotSupported", "AudioBitrateNotSupported"];

	/// <summary>One of Jellyfin's <c>TranscodeReason</c> names in plain words; an unknown one as its words, lower case.</summary>
	public static string ReasonText(string reason) => reason.Trim() switch
	{
		"ContainerBitrateExceedsLimit" or "VideoBitrateNotSupported" => "the file is over the bitrate limit",
		"AudioBitrateNotSupported" => "the audio is over the bitrate limit",
		"ContainerNotSupported" => "the file's container isn't supported",
		"VideoCodecNotSupported" or "VideoCodecTagNotSupported" => "the video format isn't supported",
		"AudioCodecNotSupported" => "the audio format isn't supported",
		"SubtitleCodecNotSupported" => "the subtitles are drawn into the picture",
		"AudioIsExternal" => "the audio track is a separate file",
		"SecondaryAudioNotSupported" => "the second audio track isn't supported",
		"VideoProfileNotSupported" or "VideoLevelNotSupported" or "VideoResolutionNotSupported" or "VideoBitDepthNotSupported"
			or "VideoFramerateNotSupported" or "RefFramesNotSupported" or "AnamorphicVideoNotSupported"
			or "InterlacedVideoNotSupported" or "VideoRangeTypeNotSupported" => "the video can't be sent as it is",
		"AudioChannelsNotSupported" or "AudioProfileNotSupported" or "AudioSampleRateNotSupported"
			or "AudioBitDepthNotSupported" => "the audio can't be sent as it is",
		"UnknownVideoStreamInfo" or "UnknownAudioStreamInfo" => "the server doesn't know enough about the file",
		"DirectPlayError" => "it couldn't play directly",
		"StreamCountExceedsLimit" => "the file has too many streams",
		// a name the server added later reads as its words; anything that is not a plain name is not shown as it is
		var other when PlainName().IsMatch(other) => SplitWords(other),
		_ => "another reason",
	};

	[System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z]{1,64}$")]
	private static partial System.Text.RegularExpressions.Regex PlainName();

	private static string SplitWords(string name)
	{
		var builder = new System.Text.StringBuilder();
		foreach (var c in name)
		{
			if (char.IsUpper(c) && builder.Length > 0)
			{
				builder.Append(' ');
			}

			builder.Append(char.ToLowerInvariant(c));
		}

		return builder.ToString();
	}

	/// <summary><c>set_quality_defaults</c>'s args: the default at home and the one away.</summary>
	public static JsonObject DefaultsCommand(QualityChoice home, QualityChoice away) => new() { ["home"] = home.ToJson(), ["away"] = away.ToJson() };

	/// <summary>The words for a refusal (docs/BRIDGE.md §2 <c>not_allowed</c>) when the backend's own are missing.</summary>
	public const string RefusedText = "This server doesn't allow a lower quality for your account. Playing at the original quality.";

	/// <summary>The <c>error</c> code of a quality the server will not convert for this account (docs/BRIDGE.md §2).</summary>
	public const string NotAllowed = "not_allowed";
}
