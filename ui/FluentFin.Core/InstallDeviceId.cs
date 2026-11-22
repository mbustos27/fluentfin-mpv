using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceId;

namespace FluentFin.Core;

/// <summary>
/// Nixie 1.5.1 (BH-150-M2): the device id this copy gives Jellyfin, one per install and Windows user rather than one per PC.
/// Jellyfin ends a device's older session when the same user signs in again from the same device id, and up to 1.5.0 every
/// copy on a PC sent the PC's own id (<see cref="PcId"/>): two Windows accounts signed in as one Jellyfin user, an installed
/// and a portable copy, or a second Nixie signed each other out, and the one signed out only heard 401s. The id is the PC's
/// id hashed with this copy's data folder, its program folder and the Windows user (<see cref="For"/>).
/// <para>
/// 1.6.1 (BH-151-m5, BH-151-m3): the id is worked out once and then kept in the data folder (<see cref="FileName"/>, one id
/// per PC and Windows user, and one for the installed copy apart from the portable ones, which share the default data
/// folder), so a portable copy unzipped to a new version folder, or a USB copy given another drive letter,
/// stays the same device: an administrator's per-device allow list keeps taking it. The first 1.6.1 start keeps the id 1.5.1
/// to 1.6.0 sent from this data and program folder (migrated, not reset), so an update asks nothing and stays allowed. Two
/// windows of one copy no longer share an id: each running window takes the first free slot (<see cref="ClaimWindow"/>), and
/// the second one's id is <see cref="ForWindow"/> of the first, the same every time, so neither ends the other's session.
/// </para>
/// </summary>
public static class InstallDeviceId
{
	/// <summary>1.6.1: the file in the data folder that keeps the id, per PC and Windows user (<see cref="Stored"/>).</summary>
	public const string FileName = "device-id.json";

	/// <summary>1.6.1: how many windows of one copy get an id of their own (beyond that they share the last one).</summary>
	public const int WindowSlots = 16;

	private static readonly Lock Gate = new();
	private static string? _dataFolder;
	private static bool _installed;
	private static string? _current;
	private static IDisposable? _window;

	/// <summary>
	/// The app's start: the data folder this run uses (<see cref="KnownFolders.ApplicationData"/>). From then on the id is the
	/// one kept there (<see cref="Stored"/>), for this run's window slot (<see cref="ClaimWindow"/>). 1.6.1 (the review):
	/// <paramref name="installed"/>, the copy the MSI installed (<see cref="Updates.InstallDetector.IsMsiInstall"/>), keeps an
	/// id of its own beside the portable copies' in the same folder.
	/// </summary>
	public static void UseDataFolder(string? dataFolder, bool installed = false)
	{
		lock (Gate)
		{
			_dataFolder = dataFolder;
			_installed = installed;
			_current = null;
			_window?.Dispose();
			_window = null;
		}
	}

	/// <summary>
	/// This copy's device id. Before <see cref="UseDataFolder"/> (tests and tools) it is worked out for the default data folder
	/// and nothing is kept or claimed; after it, the id kept in that folder for this window (<see cref="Stored"/>).
	/// </summary>
	public static string Current
	{
		get
		{
			lock (Gate)
			{
				if (_current is not null)
				{
					return _current;
				}

				var user = CurrentUser;
				if (_dataFolder is null)
				{
					return _current = For(SafePcId(), KnownFolders.DefaultRoot, AppContext.BaseDirectory, user);
				}

				// 1.6.1 (the review): never throws: every sign-in asks for it outside its own try (a failure here broke sign-in)
				try
				{
					var id = Stored(_dataFolder, PcId(), AppContext.BaseDirectory, user, _installed);
					var (slot, claim) = ClaimWindow(id);
					_window = claim;
					return _current = ForWindow(id, slot);
				}
				catch (Exception)
				{
					return _current = For(SafePcId(), _dataFolder, AppContext.BaseDirectory, user);
				}
			}
		}
	}

	private static string CurrentUser => $@"{Environment.UserDomainName}\{Environment.UserName}";

	/// <summary>The PC's id, the one Nixie 1.5.0 and earlier (and FluentFin) sent for every copy on the PC.</summary>
	public static string PcId() => new DeviceIdBuilder().OnWindows(windows => windows.AddWindowsDeviceId()).ToString();

	/// <summary>1.6.1: <see cref="PcId"/>, or the PC's name should Windows not say it (<see cref="Current"/> never throws).</summary>
	private static string SafePcId()
	{
		try
		{
			return PcId();
		}
		catch (Exception)
		{
			return Environment.MachineName;
		}
	}

	/// <summary>
	/// The id of the copy run from <paramref name="programFolder"/> with <paramref name="dataFolder"/> by <paramref name="user"/> on
	/// the PC <paramref name="pcId"/>: 32 lowercase hex digits. The folders count as Windows compares them (case, a trailing
	/// separator and "." parts don't matter). One-way: the id says nothing of the folders, the user or the PC's id. 1.6.1: only
	/// the first time a data folder is used on a PC (<see cref="Stored"/>); this is also the id 1.5.1 to 1.6.0 sent every time.
	/// </summary>
	public static string For(string pcId, string dataFolder, string programFolder, string user)
	{
		var text = string.Join('\n', "nixie-device-id-1", pcId, Folder(dataFolder), Folder(programFolder), user.ToUpperInvariant());
		return Hex(text);
	}

	/// <summary>
	/// 1.6.1: the id kept in <paramref name="dataFolder"/> for this PC and Windows user, and for the installed copy
	/// (<paramref name="installed"/>) or the portable ones: both use the default data folder, and a portable 1.6.1 tried
	/// before the installed copy was updated must not give the installed copy its id (its 1.6.0 one is the device an
	/// administrator allowed). The first time (no file, no entry for them, or an entry that isn't an id) it is <see cref="For"/>,
	/// the id 1.5.1 to 1.6.0 sent from these folders, and it is written there, keeping the other entries (a data folder on a USB
	/// drive or copied to a new PC is not the old PC's device). Later starts read it whatever the program folder is. A folder
	/// that can't be written still gives the id.
	/// </summary>
	public static string Stored(string dataFolder, string pcId, string programFolder, string user, bool installed = false)
	{
		var file = Path.Combine(dataFolder, FileName);
		var key = PcKey(pcId, user, installed);
		JsonObject? root = null;
		try
		{
			if (File.Exists(file))
			{
				root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 1.6.1 (the review): locked, or not ours to read: this run sends the id worked out and writes nothing (rewritten, the
			// file lost its id and every other entry)
			return For(pcId, dataFolder, programFolder, user);
		}
		catch (JsonException)
		{
			root = null; // not JSON: worked out again and written over (nothing in it could be read)
		}

		if (root?["ids"] is JsonObject known && known[key] is JsonValue value && value.TryGetValue<string>(out var kept) && IsId(kept))
		{
			return kept;
		}

		var id = For(pcId, dataFolder, programFolder, user);
		string? temp = null;
		try
		{
			root ??= [];
			if (root["ids"] is not JsonObject ids)
			{
				root["ids"] = ids = [];
			}

			root["version"] = 1;
			ids[key] = id;
			Directory.CreateDirectory(dataFolder);
			// write then rename, as the settings file is, so a crash mid-write can't leave half a file
			temp = file + "." + Environment.ProcessId + ".tmp";
			File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			File.Move(temp, file, true);
			temp = null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// a read-only data folder: this start still sends the id, and the next tries to keep it again. 1.6.1 (the review): its
			// half of the write goes (a .tmp was left on every start)
			if (temp is not null)
			{
				try
				{
					File.Delete(temp);
				}
				catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
				{
				}
			}
		}

		return id;
	}

	/// <summary>
	/// 1.6.1 (BH-151-m3): the id of window <paramref name="slot"/> of the copy whose id is <paramref name="id"/>: slot 0 is the
	/// copy's own id; a second window running at the same time (slot 1) is a device of its own, the same one every time.
	/// </summary>
	public static string ForWindow(string id, int slot) => slot <= 0 ? id : Hex(string.Join('\n', "nixie-device-window-1", id, slot.ToString(System.Globalization.CultureInfo.InvariantCulture)));

	/// <summary>
	/// 1.6.1 (BH-151-m3): the first window slot no running window of this copy (<paramref name="id"/>) holds, held until the
	/// claim is disposed or the process ends (a named object of the Windows session: a window that crashed frees its slot).
	/// Past <see cref="WindowSlots"/> windows the last slot is shared. 1.6.1 (the review): a slot this window may not open (a window
	/// of the copy running as administrator made it) counts as taken.
	/// </summary>
	public static (int Slot, IDisposable Claim) ClaimWindow(string id)
	{
		for (var slot = 0; slot < WindowSlots; slot++)
		{
			Mutex claim;
			bool createdNew;
			try
			{
				claim = new Mutex(false, $@"Local\Nixie-device-{id}-{slot}", out createdNew);
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
			{
				continue;
			}

			if (createdNew)
			{
				return (slot, claim);
			}

			claim.Dispose();
		}

		return (WindowSlots - 1, new Mutex(false));
	}

	private static string PcKey(string pcId, string user, bool installed) =>
		Hex(string.Join('\n', "nixie-device-pc-1", pcId, user.ToUpperInvariant(), installed ? "installed" : "portable"));

	private static bool IsId(string? text) => text is { Length: 32 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

	private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);

	private static string Folder(string folder) =>
		Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
}
