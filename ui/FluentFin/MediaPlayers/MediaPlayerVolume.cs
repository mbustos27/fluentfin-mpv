using FluentFin.Core.Contracts.Services;

namespace FluentFin.MediaPlayers;

/// <summary>
/// fluentfin-mpv: the top of each built-in player's volume scale, for the wheel and arrow-key steps (Settings > Playback):
/// Windows' MediaPlayer counts 0 to 1, VLC and Flyleaf 0 to 100.
/// </summary>
internal static class MediaPlayerVolume
{
	public static double FullVolume(this IMediaPlayerController player) => player is WindowsMediaPlayerController ? 1 : 100;
}
