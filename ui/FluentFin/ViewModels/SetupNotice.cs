using FluentFin.Core.Settings;

namespace FluentFin.ViewModels;

/// <summary>
/// 174-signin: one plain line for the server list or a server's sign-in page to show as it opens (a navigation parameter):
/// a saved sign-in the server no longer takes, or one Sign out couldn't remove from this PC. <see cref="Server"/>: the
/// sign-in page's server (Switch user); the server list needs none. 174-sessions: <see cref="Username"/>, the name the
/// sign-in page opens with (a saved sign-in the server refused: its password is asked once, BS-M4; "" for Switch user's
/// empty page); null leaves it to the server's saved sign-ins. An empty <see cref="Message"/> shows no line.
/// </summary>
public sealed record SetupNotice(string Message, SavedServer? Server = null, string? Username = null);
