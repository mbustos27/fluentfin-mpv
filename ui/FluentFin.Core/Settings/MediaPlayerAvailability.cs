using FluentFin.Core.Contracts.Services;

namespace FluentFin.Core.Settings;

/// <summary>
/// fluentfin-mpv (release 1.0): which players this copy of FluentFin can run. Upstream's VLC and Flyleaf players need their
/// native libraries beside FluentFin.exe (<c>libvlc\win-x64\libvlc.dll</c>, <c>FFMpeg\avcodec-*.dll</c>). The release
/// package leaves both out (tools\release\build-release.ps1), so there only MPV and Windows Media Player are offered and a
/// saved VLC or Flyleaf choice plays with MPV; a dev build has both folders and offers all four.
/// </summary>
public sealed class MediaPlayerAvailability(Func<MediaPlayerType, bool> isAvailable)
{
	/// <summary>The default player, and the one a choice this copy cannot run falls back to.</summary>
	public const MediaPlayerType Fallback = MediaPlayerType.Mpv;

	/// <summary>For the running app: its own folder, looked at once.</summary>
	public static MediaPlayerAvailability ForThisApp { get; } = InFolder(AppContext.BaseDirectory, File.Exists, Directory.EnumerateFiles);

	public static MediaPlayerAvailability InFolder(string appDirectory, Func<string, bool> fileExists, Func<string, string, IEnumerable<string>> filesMatching)
	{
		var vlc = fileExists(Path.Combine(appDirectory, "libvlc", "win-x64", "libvlc.dll"));
		var ffmpeg = Path.Combine(appDirectory, "FFMpeg");
		bool flyleaf;
		try
		{
			flyleaf = filesMatching(ffmpeg, "avcodec-*.dll").Any();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			flyleaf = false;
		}

		return new(type => type switch
		{
			MediaPlayerType.Vlc => vlc,
			MediaPlayerType.Flyleaf => flyleaf,
			_ => true,
		});
	}

	public bool IsAvailable(MediaPlayerType type) => isAvailable(type);

	/// <summary>The player to use for a saved choice: that one if this copy can run it, else <see cref="Fallback"/>.</summary>
	public MediaPlayerType OrFallback(MediaPlayerType saved) => IsAvailable(saved) ? saved : Fallback;
}
