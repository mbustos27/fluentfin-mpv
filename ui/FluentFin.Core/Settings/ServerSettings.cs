using System.Collections.ObjectModel;

namespace FluentFin.Core.Settings;

public class SavedServer
{
	public string DisplayName { get; set; } = "";
	public string PublicUrl { get; set; } = "";
	public string LocalUrl { get; set; } = "";
	public List<string> LocalNetworkNames { get; set; } = [];
	public string Id { get; set; } = "";
	public ObservableCollection<SavedUser> Users { get; set; } = [];

	/// <summary>
	/// 174-sessions (BS-M2): the user of the last sign-in here when it was kept with "Remember me"; "" when none, or when the
	/// last one wasn't remembered. It stays when that sign-in goes (Sign out, a password the server refused): the sign-in page
	/// opens with it. Removing the user's saved sign-in in Settings takes it. Nixie 1.7.3 and earlier ignore it.
	/// </summary>
	public string LastUser { get; set; } = "";

	/// <summary>
	/// 174-sessions (BS-M2): when (UTC) the last sign-in here was, remembered or not. The start decides by the server with the
	/// latest one, and signs in only as its <see cref="LastUser"/> (<see cref="SavedSignIns.ForStart"/>); null on every server
	/// (a data folder from 1.7.3 or earlier) keeps the old rule: one server with one saved user.
	/// </summary>
	public DateTimeOffset? LastSignIn { get; set; }
}

public class SavedUser
{
	public string Username { get; set; } = "";
	public byte[] Password { get; set; } = [];

	/// <summary>
	/// 174-signin (a friend's report: Quick Connect signed them out at every start): a Quick Connect sign-in kept with "Remember
	/// me" has no password, so its token is kept instead, protected as a password is (DPAPI, the settings' entropy). Empty
	/// for a password sign-in. Such an entry's <see cref="Password"/> is an empty one, protected: Nixie 1.7.3 and earlier
	/// read every saved user's password at the start, and an empty array is no DPAPI blob (they stopped at "Startup failed").
	/// Sign out and Switch user end the token on the server and forget it here (<see cref="SavedSignIns.ForgetToken"/>).
	/// </summary>
	public byte[] Token { get; set; } = [];
}

/// <summary>174-signin: the saved sign-ins of a server: one per user, whatever the case of the name.</summary>
public static class SavedSignIns
{
	/// <summary>
	/// A sign-in kept for the next start, in place of every one saved before for the same user. Jellyfin's user names ignore
	/// case, and a password sign-in saves the name as typed ("Alice") while Quick Connect saves the server's ("alice"): two
	/// entries, and the start signs in by itself only with one. The new one takes the first one's place; the rest go.
	/// </summary>
	public static void Remember(SavedServer server, SavedUser user)
	{
		// replaced or removed, not changed in place, so the Users collection's change is saved (Settings.ListenToChanges)
		var same = server.Users.Where(x => SameUser(x.Username, user.Username)).ToList();
		if (same.Count == 0)
		{
			server.Users.Add(user);
			return;
		}

		server.Users[server.Users.IndexOf(same[0])] = user;
		foreach (var older in same.Skip(1))
		{
			server.Users.Remove(older);
		}
	}

	/// <summary>
	/// Switch user ended the session of <paramref name="userName"/> on <paramref name="server"/>, or the server refused the
	/// saved token: a saved Quick Connect sign-in (<see cref="SavedUser.Token"/>; it has no password of its own) is
	/// forgotten. A saved password stays (Switch user; 174-sessions: Sign out forgets it too, <see cref="Forget"/>). True
	/// when something was forgotten.
	/// </summary>
	public static bool ForgetToken(SavedServer? server, string? userName)
	{
		if (server is null || string.IsNullOrEmpty(userName) ||
			server.Users.FirstOrDefault(x => SameUser(x.Username, userName) && x.Token.Length > 0) is not { } saved)
		{
			return false;
		}

		server.Users.Remove(saved);
		return true;
	}

	// ---- 174-sessions: the basics sweep's BS-M1, BS-M2, BS-M4, BS-m1 and BS-m3

	/// <summary>
	/// Whether a saved user can sign in by itself: a protected password (an empty password is one too: an account without a
	/// password) or a Quick Connect token. Nothing else is saved now (a forgotten sign-in goes from the list), but the start
	/// asks only such users.
	/// </summary>
	public static bool CanSignIn(SavedUser user) => user.Password.Length > 0 || user.Token.Length > 0;

	/// <summary>
	/// The saved sign-in of <paramref name="userName"/> on <paramref name="server"/> goes, whatever it holds (a password or a
	/// Quick Connect token), with every entry of that name: Sign out (BS-M1), a password the server refused (BS-M4), "Remember
	/// me" unticked for that name on the sign-in page (BS-m1). <see cref="SavedServer.LastUser"/> stays, so the sign-in page
	/// opens with the name (<see cref="ForgetName"/> takes it too). True when something went.
	/// </summary>
	public static bool Forget(SavedServer? server, string? userName)
	{
		if (server is null || string.IsNullOrWhiteSpace(userName))
		{
			return false;
		}

		var saved = server.Users.Where(x => SameUser(x.Username, userName)).ToList();
		foreach (var user in saved)
		{
			server.Users.Remove(user);
		}

		return saved.Count > 0;
	}

	/// <summary>
	/// A sign-in to <paramref name="server"/> as <paramref name="userName"/> worked: it is the latest of all
	/// (<see cref="SavedServer.LastSignIn"/>, later than every other server's even within one clock tick), so the next start
	/// decides by it (BS-M2). Kept ("Remember me", or a saved sign-in used again; the caller saves it, <see cref="Remember"/>):
	/// the server's <see cref="SavedServer.LastUser"/> is that user, and the next start signs in as them. Not kept: no name is
	/// kept, so the next start signs in as nobody (the review's m2: it signed in as the user remembered before) and the saved
	/// sign-ins stay as they are (the review's Major: an unticked box forgot the password of the user it signed in). Call it
	/// before the change to the server's users is saved, or save after.
	/// </summary>
	public static void SignedIn(IEnumerable<SavedServer> servers, SavedServer server, string userName, bool kept)
	{
		if (string.IsNullOrWhiteSpace(userName))
		{
			return;
		}

		server.LastUser = kept ? userName.Trim() : "";
		var now = DateTimeOffset.UtcNow;
		var latest = servers.Where(x => !ReferenceEquals(x, server)).Select(x => x.LastSignIn).Max();
		server.LastSignIn = latest is { } other && other >= now ? other.AddTicks(1) : now;
	}

	/// <summary>
	/// The sign-in the start makes by itself, or none. The server signed in to last (<see cref="SavedServer.LastSignIn"/>)
	/// decides: its <see cref="SavedServer.LastUser"/>, when that user's sign-in is still saved; otherwise none (signed out,
	/// refused, or not remembered: the start never signs in as someone else). With no sign-in recorded on any server (a
	/// data folder from 1.7.3 or earlier), the old rule: one server with one saved user. Only users that can sign in count.
	/// </summary>
	public static (SavedServer Server, SavedUser User)? ForStart(IReadOnlyCollection<SavedServer> servers)
	{
		if (servers.Where(x => x.LastSignIn is not null).MaxBy(x => x.LastSignIn) is { } last)
		{
			return last.Users.FirstOrDefault(x => CanSignIn(x) && SameUser(x.Username, last.LastUser)) is { } user ? (last, user) : null;
		}

		if (servers.Count == 1 && servers.First() is var only && only.Users.Count == 1 && CanSignIn(only.Users[0]))
		{
			return (only, only.Users[0]);
		}

		return null;
	}

	/// <summary>
	/// The username a server's sign-in page opens with (BS-m3, it opened empty): the last one remembered there (kept after
	/// Sign out), else its one saved user, else none.
	/// </summary>
	public static string UsernameFor(SavedServer? server) =>
		server is null ? ""
		: !string.IsNullOrWhiteSpace(server.LastUser) ? server.LastUser.Trim()
		: server.Users.Count == 1 ? server.Users[0].Username.Trim()
		: "";

	/// <summary>
	/// The review's m3: <paramref name="userName"/> is no longer the server's last user (Settings removed its saved sign-in, or
	/// "Remember me" was unticked for it): the sign-in page stops opening with the name, ticked.
	/// </summary>
	public static void ForgetName(SavedServer? server, string? userName)
	{
		if (server is not null && SameUser(server.LastUser, userName))
		{
			server.LastUser = "";
		}
	}

	/// <summary>Whether two user names are one user's: Jellyfin's names ignore case (and Nixie the spaces around them).</summary>
	public static bool SameUserName(string? a, string? b) => SameUser(a, b);

	/// <summary>Whether <paramref name="userName"/> was remembered on <paramref name="server"/>: its saved sign-in, or its last one.</summary>
	public static bool WasRemembered(SavedServer? server, string? userName) =>
		server is not null && !string.IsNullOrWhiteSpace(userName) &&
		(SameUser(server.LastUser, userName) || server.Users.Any(x => SameUser(x.Username, userName)));

	private static bool SameUser(string? a, string? b) =>
		!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

public record CustomNavigationViewItem
{
	public string Name { get; set; } = "";
	public string Key { get; set; } = "";
	public Guid? Parameter { get; set; }
	public string? Glyph { get; set; } = "";
	public List<CommandModel> Commands { get; set; } = [];
	public bool Persistent { get; set; }
}

public record CommandModel
{
	public string Name { get; set; } = "";
	public string DisplayName { get; set; } = "";
	public string? Glyph { get; set; }
}