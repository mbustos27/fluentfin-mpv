using System.Security.Cryptography;
using System.Text;
using DeviceId;

namespace FluentFin.Core;

/// <summary>
/// Nixie 1.5.1 (BH-150-M2): the device id this copy gives Jellyfin, one per install and Windows user rather than one per PC.
/// Jellyfin ends a device's older session when the same user signs in again from the same device id, and up to 1.5.0 every
/// copy on a PC sent the PC's own id (<see cref="PcId"/>): two Windows accounts signed in as one Jellyfin user, an installed
/// and a portable copy, or a second Nixie signed each other out, and the one signed out only heard 401s. The id is now the
/// PC's id hashed with this copy's data folder, its program folder and the Windows user, so each copy is a device of its
/// own, and a copy keeps the same id from start to start. Nothing is stored: a saved sign-in (a user name and a protected
/// password, never a token) signs in again at the next start as before, now with this id, so an update asks nothing.
/// </summary>
public static class InstallDeviceId
{
	private static string? _dataFolder;
	private static string? _current;

	/// <summary>The app's start: the data folder this run uses (<see cref="KnownFolders.ApplicationData"/>).</summary>
	public static void UseDataFolder(string dataFolder)
	{
		_dataFolder = dataFolder;
		_current = null;
	}

	/// <summary>This copy's device id (the default data folder until <see cref="UseDataFolder"/>).</summary>
	public static string Current => _current ??= For(PcId(), _dataFolder ?? KnownFolders.DefaultRoot, AppContext.BaseDirectory,
		$@"{Environment.UserDomainName}\{Environment.UserName}");

	/// <summary>The PC's id, the one Nixie 1.5.0 and earlier (and FluentFin) sent for every copy on the PC.</summary>
	public static string PcId() => new DeviceIdBuilder().OnWindows(windows => windows.AddWindowsDeviceId()).ToString();

	/// <summary>
	/// The id of the copy run from <paramref name="programFolder"/> with <paramref name="dataFolder"/> by <paramref name="user"/> on
	/// the PC <paramref name="pcId"/>: 32 lowercase hex digits. The folders count as Windows compares them (case, a trailing
	/// separator and "." parts don't matter). One-way: the id says nothing of the folders, the user or the PC's id.
	/// </summary>
	public static string For(string pcId, string dataFolder, string programFolder, string user)
	{
		var text = string.Join('\n', "nixie-device-id-1", pcId, Folder(dataFolder), Folder(programFolder), user.ToUpperInvariant());
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);
	}

	private static string Folder(string folder) =>
		Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
}
