using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Contracts.Services;

public interface IJellyfinAuthenticationService
{
	/// <returns><c>null</c> on success, otherwise a message describing why sign-in failed.</returns>
	Task<string?> Authenticate(SavedServer server, string username, string password, bool remember);
	Task<bool> Authenticate(SavedServer server, SavedUser user);

	/// <summary>
	/// 174-sessions (BS-M3): a saved sign-in, given up on when <paramref name="cancellationToken"/> is cancelled before the
	/// server answered (the start's 10 s): an address that doesn't answer took 22 s, saying nothing.
	/// </summary>
	Task<bool> Authenticate(SavedServer server, SavedUser user, CancellationToken cancellationToken);

	/// <summary>
	/// Why the last <see cref="Authenticate(SavedServer, SavedUser)"/> didn't work, in a line the next page shows, naming the
	/// server ("The saved sign-in to Den no longer works. Sign in again."; 174-sessions, BS-M3: "Couldn't sign in to Den. Can't
	/// reach …" for every other failure, which said nothing); null when it worked.
	/// </summary>
	string? SavedSignInProblem { get; }

	/// <summary>
	/// 174-sessions (BS-M4): the last <see cref="Authenticate(SavedServer, SavedUser)"/> was refused (a 401; a 403 is the
	/// account not let in now, and keeps it): its saved password or token is forgotten (Jellyfin disables an account after a few failed sign-ins, and only the server's
	/// administrator can turn it on again), its name kept, and the next page is the server's sign-in page.
	/// </summary>
	bool SavedSignInRefused { get; }

	/// <summary>
	/// 1.7.5 (the bug hunt): the sessions Nixie keeps on <paramref name="server"/> for these saved sign-ins (a Quick Connect
	/// token, kept alive by Switch user) are ended there (/Sessions/Logout with each token), before a Remove forgets them:
	/// once forgotten, nothing could end them. Bounded; one that can't be ended (the server away) is logged, and the removal
	/// goes on.
	/// </summary>
	Task EndKeptSessions(SavedServer server, IEnumerable<SavedUser> users);

	/// <summary>174-signin: <paramref name="remember"/> keeps the sign-in for the next start, as a password one's does ("Remember me").</summary>
	Task<bool> Authenticate(SavedServer server, QuickConnectResult result, bool remember);
	Task<QuickConnectResult?> GetQuickConnectCode(SavedServer server);
	Task<QuickConnectResult?> CheckQuickConnectStatus(SavedServer server, QuickConnectResult result);
}
