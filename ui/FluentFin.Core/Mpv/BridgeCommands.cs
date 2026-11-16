using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;

namespace FluentFin.Core.Mpv;

/// <summary>Who the backend talks to the server as: FluentFin's own login and device (docs/BRIDGE.md §4).</summary>
public sealed record BridgeIdentity(
	string ServerUrl,
	string? ServerId,
	string? ServerName,
	string UserId,
	string? UserName,
	string AccessToken,
	string DeviceId,
	string DeviceName,
	string ClientName,
	string ClientVersion)
{
	/// <summary>Never print the token (records print every member by default), nor credentials or a query in the server URL.</summary>
	public override string ToString() => $"{UserName} ({UserId}) on {ServerName} {BridgeRedaction.ServerForLog(ServerUrl)} as {ClientName} {ClientVersion} / {DeviceName}";
}

/// <summary>
/// Where mpv draws: <c>embedded</c> into a window FluentFin owns, <c>attached</c> (fluentfin-mpv player controls): the
/// backend attaches to an mpv FluentFin runs itself, listening on the pipe <see cref="Ipc"/>; or <c>standalone</c>.
/// </summary>
public sealed record BridgeWindow(string Mode, long? Hwnd, string? Ipc = null)
{
	/// <summary>
	/// HWNDs carry 32 significant bits; sent as an unsigned 32-bit number so it can never come out negative (mpv opens a
	/// detached window for a negative <c>--wid</c>, mpv #10189).
	/// </summary>
	public static BridgeWindow Embedded(nint hwnd) => new("embedded", unchecked((uint)hwnd));

	public static BridgeWindow Standalone { get; } = new("standalone", null);

	/// <summary>fluentfin-mpv player controls: FluentFin's own mpv, answering JSON IPC on <paramref name="pipe"/> (the name, without <c>\\.\pipe\</c>).</summary>
	public static BridgeWindow Attached(string pipe) => new("attached", null, pipe);
}

/// <summary>Arguments of <c>play</c>, mirroring the server's remote Play command.</summary>
public sealed record BridgePlayRequest(
	IReadOnlyList<string> ItemIds,
	int StartIndex = 0,
	long StartPositionTicks = 0,
	string? MediaSourceId = null,
	int? AudioStreamIndex = null,
	int? SubtitleStreamIndex = null)
{
	public string? StartItemId => StartIndex >= 0 && StartIndex < ItemIds.Count ? ItemIds[StartIndex] : null;

	/// <summary>
	/// The queue of a playlist that starts at <paramref name="startIndex"/>, with the choice made on an item page if any.
	/// <see cref="PlaybackSelection.NoSubtitles"/> (-1) is also the protocol's "subtitles off".
	/// </summary>
	public static BridgePlayRequest FromPlaylist(IEnumerable<Guid> itemIds, int startIndex, long startPositionTicks, PlaybackSelection? selection) =>
		new([.. itemIds.Select(ToBridgeId)], Math.Max(0, startIndex), Math.Max(0, startPositionTicks), null, selection?.AudioStreamIndex, selection?.SubtitleStreamIndex);

	/// <summary>Jellyfin ids as the web client sends them: 32 hex digits, no dashes.</summary>
	public static string ToBridgeId(Guid id) => id.ToString("N");
}

/// <summary>The stream indices a play from an item page sends with the mpv player (docs/BRIDGE.md §4 <c>play</c>).</summary>
public static class MpvTrackPick
{
	/// <summary>
	/// None unless the user changed a picker on the page: the pickers start at FluentFin's own defaults, and sending those
	/// would override the backend's remembered tracks and language defaults with a "deliberate" pick nobody made. When the
	/// user did pick, both indices, so the backend takes them as a deliberate pick: the side without a choice on the page
	/// becomes the server default (what FluentFin's own players use), subtitles off if there is none.
	/// </summary>
	public static PlaybackSelection? ForPlay(Jellyfin.Sdk.Generated.Models.BaseItemDto dto, PlaybackSelection? selection, bool userPicked)
	{
		if (!userPicked || selection is null)
		{
			return null;
		}

		var source = dto.MediaSources is { Count: > 0 } sources ? sources[0] : null;
		var audio = selection.AudioStreamIndex
			?? source?.DefaultAudioStreamIndex
			?? dto.MediaStreams?.FirstOrDefault(x => x.Type == Jellyfin.Sdk.Generated.Models.MediaStream_Type.Audio)?.Index;
		var subtitle = selection.SubtitleStreamIndex ?? source?.DefaultSubtitleStreamIndex ?? PlaybackSelection.NoSubtitles;
		return new PlaybackSelection(audio, subtitle);
	}
}

public static class BridgeCommands
{
	/// <summary>
	/// <c>connect</c> args. Without an identity only <c>protocol</c> and <c>window</c> are sent, which a backend started with
	/// <c>--dev</c> accepts for <c>play_file</c>. <paramref name="prewarm"/>: connected ahead of playback, mpv without its
	/// video window until the first play (docs/BRIDGE.md §4); sent only when true.
	/// </summary>
	public static JsonObject Connect(BridgeIdentity? identity, BridgeWindow window, bool prewarm = false)
	{
		var args = new JsonObject
		{
			["protocol"] = Bridge.ProtocolVersion,
		};

		if (identity is not null)
		{
			args["server"] = WithoutNulls(new JsonObject
			{
				["url"] = identity.ServerUrl,
				["id"] = identity.ServerId,
				["name"] = identity.ServerName,
			});
			args["user"] = WithoutNulls(new JsonObject
			{
				["id"] = identity.UserId,
				["name"] = identity.UserName,
			});
			args["accessToken"] = identity.AccessToken;
			args["device"] = new JsonObject
			{
				["id"] = identity.DeviceId,
				["name"] = identity.DeviceName,
			};
			args["client"] = new JsonObject
			{
				["name"] = identity.ClientName,
				["version"] = identity.ClientVersion,
			};
		}

		args["window"] = WithoutNulls(new JsonObject
		{
			["mode"] = window.Mode,
			["hwnd"] = window.Hwnd,
			["ipc"] = window.Ipc,
		});

		if (prewarm)
		{
			args["prewarm"] = true;
		}

		return args;
	}

	public static JsonObject Play(BridgePlayRequest request)
	{
		if (request.ItemIds.Count == 0)
		{
			throw new ArgumentException("play needs at least one item", nameof(request));
		}

		return WithoutNulls(new JsonObject
		{
			["itemIds"] = new JsonArray([.. request.ItemIds.Select(id => (JsonNode)JsonValue.Create(id))]),
			["startIndex"] = request.StartIndex,
			["startPositionTicks"] = request.StartPositionTicks,
			["mediaSourceId"] = request.MediaSourceId,
			["audioStreamIndex"] = request.AudioStreamIndex,
			["subtitleStreamIndex"] = request.SubtitleStreamIndex,
		});
	}

	public static JsonObject PlayFile(string url, string? title) => WithoutNulls(new JsonObject
	{
		["url"] = url,
		["title"] = title,
	});

	public static JsonObject Seek(long positionTicks) => new() { ["positionTicks"] = positionTicks };

	public static JsonObject SeekRelative(double seconds) => new() { ["seconds"] = seconds };

	public static JsonObject Index(int index) => new() { ["index"] = index };

	public static JsonObject Volume(int volume) => new() { ["volume"] = Math.Clamp(volume, 0, 100) };

	public static JsonObject Mute(bool muted) => new() { ["muted"] = muted };

	public static JsonObject Fullscreen(bool fullscreen) => new() { ["fullscreen"] = fullscreen };

	/// <summary>
	/// fluentfin-mpv: <c>set_controls</c> (docs/BRIDGE.md §4). <paramref name="ui"/>: FluentFin draws the controls, mpv's OSC
	/// is hidden, and the subtitles keep clear of the bottom <paramref name="subtitleInset"/> of the video's height (clamped
	/// to 0..0.5, rounded to 0.001 so a bar that did not really move sends the same value). Otherwise mpv's own controls.
	/// </summary>
	public static JsonObject Controls(bool ui, double subtitleInset = 0) => ui
		? new JsonObject { ["controls"] = "ui", ["subtitleInset"] = RoundInset(subtitleInset) }
		: new JsonObject { ["controls"] = "mpv" };

	/// <summary>The inset as sent: 0..0.5, three decimals; anything not a number is 0.</summary>
	public static double RoundInset(double inset) => double.IsFinite(inset) ? Math.Round(Math.Clamp(inset, 0, MaxSubtitleInset), 3) : 0;

	/// <summary>The most of the video's height the bottom controls may claim (docs/BRIDGE.md §4).</summary>
	public const double MaxSubtitleInset = 0.5;

	private static JsonObject WithoutNulls(JsonObject value)
	{
		foreach (var key in value.Where(x => x.Value is null).Select(x => x.Key).ToList())
		{
			value.Remove(key);
		}

		return value;
	}
}
