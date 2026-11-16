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
		public const string SetFullscreen = "set_fullscreen";
		public const string SetControls = "set_controls";
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
