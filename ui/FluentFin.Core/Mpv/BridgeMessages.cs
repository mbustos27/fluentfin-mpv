using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FluentFin.Core.Mpv;

/// <summary>Names from the bridge protocol, docs/BRIDGE.md.</summary>
public static class Bridge
{
	public const int ProtocolVersion = 1;

	public static class Commands
	{
		public const string Ping = "ping";
		public const string Connect = "connect";
		public const string Play = "play";
		public const string Pause = "pause";
		public const string Unpause = "unpause";
		public const string TogglePause = "toggle_pause";
		public const string Stop = "stop";
		public const string Next = "next";
		public const string Previous = "previous";
		public const string Seek = "seek";
		public const string SeekRelative = "seek_relative";
		public const string SetAudio = "set_audio";
		public const string SetSubtitle = "set_subtitle";
		public const string SetVolume = "set_volume";
		public const string SetMute = "set_mute";
		/// <summary>fluentfin-mpv: the subtitle text size in percent (docs/BRIDGE.md §4).</summary>
		public const string SetSubtitleSize = "set_subtitle_size";
		public const string SetFullscreen = "set_fullscreen";
		public const string SetControls = "set_controls";
		/// <summary>fluentfin-mpv: skips the segment on offer (<see cref="BridgeSegments.Offered"/>); result <see cref="BridgeSkipResult"/> (docs/BRIDGE.md §4).</summary>
		public const string SkipSegment = "skip_segment";
		public const string GetState = "get_state";
		public const string Shutdown = "shutdown";
		public const string PlayFile = "play_file";
	}

	public static class Events
	{
		public const string Hello = "hello";
		public const string State = "state";
		public const string Position = "position";
		public const string Item = "item";
		public const string Tracks = "tracks";
		public const string Ended = "ended";
		/// <summary>The backend is done with a play session's Stopped report; any <c>outcome</c> closes it (docs/BRIDGE.md §5).</summary>
		public const string Reported = "reported";
		public const string FullscreenRequest = "fullscreen_request";
		public const string Error = "error";
		/// <summary>fluentfin-mpv: the item's media segments and the one on offer now, <see cref="BridgeSegments"/> (docs/BRIDGE.md §5).</summary>
		public const string Segments = "segments";
		/// <summary>fluentfin-mpv: the items <c>next</c> and <c>previous</c> would play, <see cref="BridgeNext"/> (docs/BRIDGE.md §5).</summary>
		public const string Next = "next";
	}

	/// <summary>fluentfin-mpv: what the backend's settings do with a segment type (<see cref="BridgeSegment.Action"/>).</summary>
	public static class SegmentActions
	{
		/// <summary>Offer a skip: the segment is <see cref="BridgeSegments.Offered"/> while it plays.</summary>
		public const string Ask = "ask";

		/// <summary>The backend skips it by itself, whoever draws the controls.</summary>
		public const string Always = "always";
	}

	public static class States
	{
		public const string Idle = "idle";
		public const string Opening = "opening";
		public const string Playing = "playing";
		public const string Paused = "paused";
		public const string Error = "error";
	}

	public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}

/// <summary>An event line from the backend, <c>{"event": name, "data": {...}}</c>.</summary>
public sealed record BridgeEvent(string Name, JsonNode? Data)
{
	public T? DataAs<T>() => Data is null ? default : Data.Deserialize<T>(Bridge.JsonOptions);
}

/// <summary>The <c>state</c> snapshot (event data and <c>get_state</c> result).</summary>
public sealed class BridgeState
{
	public string State { get; set; } = Bridge.States.Idle;
	public bool Connected { get; set; }
	public string? ItemId { get; set; }
	public string? MediaSourceId { get; set; }
	public string? PlaySessionId { get; set; }
	public string? PlayMethod { get; set; }
	public long PositionTicks { get; set; }
	public long? DurationTicks { get; set; }
	public int Volume { get; set; } = 100;
	public bool Muted { get; set; }
	public int? AudioStreamIndex { get; set; }
	public int? SubtitleStreamIndex { get; set; }
	public BridgeQueue? Queue { get; set; }
	public bool Fullscreen { get; set; }
	public string? Window { get; set; }

	public bool IsActive => State is Bridge.States.Opening or Bridge.States.Playing or Bridge.States.Paused;
}

public sealed class BridgeQueue
{
	public List<string> ItemIds { get; set; } = [];
	public int Index { get; set; }
}

public sealed record BridgeHello(int Protocol, string? Backend, string? Version, string? Bridge);

/// <summary>fluentfin-mpv: the <c>item</c> event, when a queue item starts (docs/BRIDGE.md §5).</summary>
public sealed record BridgeItem(string? ItemId, string? Name, string? Type, string? SeriesName = null, int? SeasonNumber = null, int? EpisodeNumber = null, long? RunTimeTicks = null);

/// <summary>fluentfin-mpv: the <c>tracks</c> event, Jellyfin's own stream indices (docs/BRIDGE.md §5).</summary>
public sealed record BridgeTracks(IReadOnlyList<BridgeTrack>? Audio, IReadOnlyList<BridgeTrack>? Subtitle);

/// <summary>
/// One audio or subtitle stream. <see cref="Title"/> is the server's DisplayTitle; <see cref="StreamTitle"/> the stream's
/// own title tag only (absent from older backends).
/// </summary>
public sealed record BridgeTrack(int Index, string? Language = null, string? Title = null, string? StreamTitle = null, string? Codec = null, int? Channels = null,
	bool IsDefault = false, bool? IsForced = null, bool? IsHearingImpaired = null, bool IsExternal = false);

/// <summary>
/// fluentfin-mpv: one media segment of the <c>segments</c> event: Jellyfin's <c>MediaSegmentType</c> (<c>Intro</c>,
/// <c>Outro</c>, <c>Commercial</c>, <c>Preview</c>, <c>Recap</c>), its span in ticks, and <see cref="Bridge.SegmentActions"/>.
/// </summary>
public sealed record BridgeSegment(string Type, long StartTicks, long EndTicks, string Action)
{
	/// <summary>The skip button's text, the backend's own words for the type ("Skip Credits" for an outro).</summary>
	[JsonIgnore]
	public string SkipLabel => Type switch
	{
		"Outro" => "Skip Credits",
		"Commercial" => "Skip Commercial",
		"Preview" => "Skip Preview",
		"Recap" => "Skip Recap",
		_ => "Skip Intro",
	};
}

/// <summary>
/// fluentfin-mpv: the <c>segments</c> event (docs/BRIDGE.md §5): the item's segments that are not set to off, and
/// <see cref="Current"/>, the index of the one on offer now (an <c>ask</c> segment playing), else null. Sent when an item
/// starts and whenever either changes; the offer follows the position at the backend's sampling (about every 0.5 s).
/// </summary>
public sealed record BridgeSegments(string? ItemId, IReadOnlyList<BridgeSegment>? Segments, int? Current)
{
	/// <summary>The segment to offer a skip for now (<see cref="Bridge.Commands.SkipSegment"/>), null when there is none.</summary>
	[JsonIgnore]
	public BridgeSegment? Offered => Current is int index && Segments is { } segments && index >= 0 && index < segments.Count ? segments[index] : null;
}

/// <summary>
/// fluentfin-mpv: the <c>next</c> event (docs/BRIDGE.md §5): the items <c>next</c> and <c>previous</c> would play now (null at
/// an end of the queue), and whether the backend plays <see cref="NextItemId"/> by itself when this item ends.
/// </summary>
public sealed record BridgeNext(string? ItemId, string? NextItemId = null, string? PreviousItemId = null, bool AutoPlay = false);

/// <summary>fluentfin-mpv: the result of <c>skip_segment</c>: false when nothing was on offer (and nothing was done).</summary>
public sealed record BridgeSkipResult(bool Skipped);

/// <summary>The <c>position</c> event.</summary>
public sealed record BridgePosition(long PositionTicks, long? DurationTicks);

public sealed record BridgeEnded(string? ItemId, string? Reason);

public sealed record BridgeFullscreenRequest(bool Fullscreen);

public sealed record BridgeError(string? Code, string? Message, bool Fatal);

public class BridgeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The backend answered <c>ok: false</c>.</summary>
public sealed class BridgeCommandException(string command, string code, string? detail)
	: BridgeException($"The backend refused '{command}': {code}{(string.IsNullOrEmpty(detail) ? "" : $" ({detail})")}")
{
	public string Command { get; } = command;
	public string Code { get; } = code;
	public string? Detail { get; } = detail;
}

/// <summary>No response arrived in time; the request is forgotten, a late response is ignored.</summary>
public sealed class BridgeTimeoutException(string command, TimeSpan timeout)
	: BridgeException($"The backend did not answer '{command}' within {timeout.TotalSeconds:0.#} s")
{
	public string Command { get; } = command;
}

/// <summary>The connection is gone (the backend exited or closed its output) before a response arrived.</summary>
public sealed class BridgeClosedException(string message, Exception? inner = null) : BridgeException(message, inner);
