using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv (release 1.0): the release has no VLC or Flyleaf libraries, so it offers only MPV and Windows Media Player,
/// and a saved VLC or Flyleaf choice plays with MPV instead of failing. A dev build, with both folders, offers all four.
/// </summary>
public sealed class MediaPlayerAvailabilityTests : IDisposable
{
	private const string App = @"C:\FluentFinMPV";
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));

	private static MediaPlayerAvailability Folder(bool vlc, bool ffmpeg) => MediaPlayerAvailability.InFolder(App,
		path => vlc && path == Path.Combine(App, "libvlc", "win-x64", "libvlc.dll"),
		(folder, pattern) => ffmpeg && folder == Path.Combine(App, "FFMpeg") && pattern == "avcodec-*.dll"
			? [Path.Combine(folder, "avcodec-63.dll")]
			: throw new DirectoryNotFoundException(folder));

	[Fact]
	public void ARelease_OffersMpvAndWindowsMediaPlayerOnly()
	{
		var release = Folder(vlc: false, ffmpeg: false);

		Assert.Equal([MediaPlayerType.Mpv, MediaPlayerType.WindowsMediaPlayer], SettingsViewModel.OfferedPlayers(release).Select(x => x.Type));
		Assert.Equal("mpv", SettingsViewModel.OfferedPlayers(release)[0].Name);
	}

	[Fact]
	public void ADevBuild_WithBothFolders_OffersAllFour()
	{
		var dev = Folder(vlc: true, ffmpeg: true);

		Assert.Equal(SettingsViewModel.AllMediaPlayers.Select(x => x.Type), SettingsViewModel.OfferedPlayers(dev).Select(x => x.Type));
	}

	[Fact]
	public void EachPlayer_NeedsOnlyItsOwnLibraries()
	{
		var vlcOnly = Folder(vlc: true, ffmpeg: false);
		Assert.True(vlcOnly.IsAvailable(MediaPlayerType.Vlc));
		Assert.False(vlcOnly.IsAvailable(MediaPlayerType.Flyleaf));

		var ffmpegOnly = Folder(vlc: false, ffmpeg: true);
		Assert.False(ffmpegOnly.IsAvailable(MediaPlayerType.Vlc));
		Assert.True(ffmpegOnly.IsAvailable(MediaPlayerType.Flyleaf));
	}

	[Theory]
	[InlineData(MediaPlayerType.Vlc, MediaPlayerType.Mpv)]
	[InlineData(MediaPlayerType.Flyleaf, MediaPlayerType.Mpv)]
	[InlineData(MediaPlayerType.WindowsMediaPlayer, MediaPlayerType.WindowsMediaPlayer)]
	[InlineData(MediaPlayerType.Mpv, MediaPlayerType.Mpv)]
	public void ASavedChoice_TheReleaseCannotRun_PlaysWithMpv(MediaPlayerType saved, MediaPlayerType used)
	{
		var service = new LocalSettingsService(new KnownFolders(_root));
		service.SaveSetting(SettingKeys.MediaPlayerType, saved);

		var settings = new Settings.Settings(service, Folder(vlc: false, ffmpeg: false));

		Assert.Equal(used, settings.MediaPlayer);
		// the saved value stays: the same data folder with a dev build still has its choice
		Assert.Equal(saved, service.ReadSetting(SettingKeys.MediaPlayerType));
	}

	[Fact]
	public void ADevBuild_KeepsASavedVlcChoice()
	{
		var service = new LocalSettingsService(new KnownFolders(_root));
		service.SaveSetting(SettingKeys.MediaPlayerType, MediaPlayerType.Vlc);

		Assert.Equal(MediaPlayerType.Vlc, new Settings.Settings(service, Folder(vlc: true, ffmpeg: true)).MediaPlayer);
	}

	[Fact]
	public void AFolderThatCannotBeRead_HasNoFlyleaf()
	{
		var players = MediaPlayerAvailability.InFolder(App, _ => false, (_, _) => throw new UnauthorizedAccessException());

		Assert.False(players.IsAvailable(MediaPlayerType.Flyleaf));
		Assert.Equal(MediaPlayerType.Mpv, players.OrFallback(MediaPlayerType.Flyleaf));
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (DirectoryNotFoundException)
		{
		}
	}
}
