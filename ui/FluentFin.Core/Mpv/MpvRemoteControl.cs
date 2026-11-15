using System.Globalization;
using System.Text.Json.Nodes;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;

namespace FluentFin.Core.Mpv;

/// <summary>A bridge command to send.</summary>
public sealed record BridgeCommand(string Name, JsonObject? Args = null);

/// <summary>
/// FluentFin owns the session websocket; with the mpv player the remote-control messages the server sends to it become
/// bridge commands (docs/BRIDGE.md §4, §6).
/// </summary>
public static class MpvRemoteControl
{
	public const int VolumeStep = 5;
	public static readonly TimeSpan SkipForward = TimeSpan.FromSeconds(30);
	public static readonly TimeSpan SkipBack = TimeSpan.FromSeconds(10);

	/// <summary>The command for a Playstate or GeneralCommand message; null for anything the backend does not take.</summary>
	public static BridgeCommand? Translate(IOutboundWebSocketMessage message, BridgeState? state) => message switch
	{
		PlaystateMessage { Data: { } playstate } => Translate(playstate),
		GeneralCommandMessage { Data: { } command } => Translate(command, state),
		_ => null,
	};

	public static BridgeCommand? Translate(PlaystateRequest request) => request.Command switch
	{
		PlaystateRequest_Command.Stop => new(Bridge.Commands.Stop),
		PlaystateRequest_Command.Pause => new(Bridge.Commands.Pause),
		PlaystateRequest_Command.Unpause => new(Bridge.Commands.Unpause),
		PlaystateRequest_Command.PlayPause => new(Bridge.Commands.TogglePause),
		PlaystateRequest_Command.NextTrack => new(Bridge.Commands.Next),
		PlaystateRequest_Command.PreviousTrack => new(Bridge.Commands.Previous),
		PlaystateRequest_Command.Seek when request.SeekPositionTicks is { } ticks => new(Bridge.Commands.Seek, BridgeCommands.Seek(ticks)),
		PlaystateRequest_Command.FastForward => new(Bridge.Commands.SeekRelative, BridgeCommands.SeekRelative(SkipForward.TotalSeconds)),
		PlaystateRequest_Command.Rewind => new(Bridge.Commands.SeekRelative, BridgeCommands.SeekRelative(-SkipBack.TotalSeconds)),
		_ => null,
	};

	public static BridgeCommand? Translate(GeneralCommand command, BridgeState? state)
	{
		var volume = state?.Volume ?? 100;
		return command.Name switch
		{
			GeneralCommand_Name.SetVolume when Argument(command, "Volume") is { } value => new(Bridge.Commands.SetVolume, BridgeCommands.Volume(value)),
			GeneralCommand_Name.VolumeUp => new(Bridge.Commands.SetVolume, BridgeCommands.Volume(volume + VolumeStep)),
			GeneralCommand_Name.VolumeDown => new(Bridge.Commands.SetVolume, BridgeCommands.Volume(volume - VolumeStep)),
			GeneralCommand_Name.Mute => new(Bridge.Commands.SetMute, BridgeCommands.Mute(true)),
			GeneralCommand_Name.Unmute => new(Bridge.Commands.SetMute, BridgeCommands.Mute(false)),
			GeneralCommand_Name.ToggleMute => new(Bridge.Commands.SetMute, BridgeCommands.Mute(!(state?.Muted ?? false))),
			GeneralCommand_Name.SetAudioStreamIndex when Argument(command, "Index") is { } index => new(Bridge.Commands.SetAudio, BridgeCommands.Index(index)),
			GeneralCommand_Name.SetSubtitleStreamIndex when Argument(command, "Index") is { } index => new(Bridge.Commands.SetSubtitle, BridgeCommands.Index(index)),
			_ => null,
		};
	}

	/// <summary>
	/// A remote Play (casting to FluentFin) as <c>play</c> args. Only PlayNow-like commands replace what plays; the bridge
	/// has no way to add to the queue, so PlayNext/PlayLast give null.
	/// </summary>
	public static BridgePlayRequest? ToPlayRequest(PlayRequest request)
	{
		if (request.PlayCommand is PlayRequest_PlayCommand.PlayNext or PlayRequest_PlayCommand.PlayLast)
		{
			return null;
		}

		var ids = request.ItemIds?.Where(x => x is not null).Select(x => BridgePlayRequest.ToBridgeId(x!.Value)).ToList() ?? [];
		if (ids.Count == 0)
		{
			return null;
		}

		var startIndex = Math.Clamp(request.StartIndex ?? 0, 0, ids.Count - 1);
		return new BridgePlayRequest(ids, startIndex, Math.Max(0, request.StartPositionTicks ?? 0), request.MediaSourceId, request.AudioStreamIndex, request.SubtitleStreamIndex);
	}

	/// <summary>Arguments arrive as strings ("Volume": "50"), sometimes as numbers.</summary>
	private static int? Argument(GeneralCommand command, string name)
	{
		if (command.Arguments?.AdditionalData is not { } data || !data.TryGetValue(name, out var value) || value is null)
		{
			return null;
		}

		return value switch
		{
			int i => i,
			long l => (int)l,
			double d => (int)Math.Round(d),
			decimal m => (int)Math.Round(m),
			_ => int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
		};
	}
}
