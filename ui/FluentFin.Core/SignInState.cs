namespace FluentFin.Core;

/// <summary>
/// Nixie 1.5.1 (BH-150-M2): whether the server still takes this sign-in. A token the server stops accepting (the user signed
/// out of every device on the server, an administrator removed the device, the password changed, or a copy of Nixie 1.5.0 on
/// this PC signed in as the same user) made every page say "Check that the server is running", and Try again could never
/// work. <see cref="Services.SignInWatch"/> marks it <see cref="Ended"/> once the server has refused the token twice; the
/// pages' load state then asks for a new sign-in instead (<see cref="ViewModels.LoadState"/>), and so does the player when its
/// backend's connect is refused. A sign-in clears it.
/// </summary>
public static class SignInState
{
	private static readonly Lock Gate = new();
	private static int _ended;
	private static int _generation;

	/// <summary>The server refused this sign-in's token: nothing more will load until the user signs in again.</summary>
	public static bool Ended => Volatile.Read(ref _ended) == 1;

	/// <summary>
	/// 1.6.1: which sign-in this is (one more at every <see cref="SignedIn"/>). A check that began under one sign-in may end only
	/// that one (<see cref="MarkEnded(int)"/>): its 401 was for the old token.
	/// </summary>
	public static int Generation => Volatile.Read(ref _generation);

	/// <summary>Raised (on any thread) when <see cref="Ended"/> changes.</summary>
	public static event Action? Changed;

	/// <summary>
	/// The app's "Sign in again" (set once the window is up): stops what plays and takes the user to the server's sign-in,
	/// with the saved sign-in when there is one. Null in tests and before the app is up.
	/// </summary>
	public static Func<Task>? SignInAgain { get; set; }

	/// <summary>The server refused the token. True the first time only (the caller logs it).</summary>
	public static bool MarkEnded() => MarkEnded(Generation);

	/// <summary>
	/// 1.6.1: the server refused the token of sign-in <paramref name="generation"/> (<see cref="Generation"/> when the refused
	/// request was seen). False, and nothing ends, when a newer sign-in came meanwhile, or when it had already ended.
	/// </summary>
	public static bool MarkEnded(int generation)
	{
		lock (Gate)
		{
			if (generation != _generation || _ended == 1)
			{
				return false;
			}

			Volatile.Write(ref _ended, 1);
		}

		Changed?.Invoke();
		return true;
	}

	/// <summary>A sign-in (or a sign-out): whatever ended before is over.</summary>
	public static void SignedIn()
	{
		bool was;
		lock (Gate)
		{
			Interlocked.Increment(ref _generation);
			was = _ended == 1;
			Volatile.Write(ref _ended, 0);
		}

		if (was)
		{
			Changed?.Invoke();
		}
	}

	/// <summary>The words every page shows for it (the title).</summary>
	public static string Title() => $"Your sign-in to {ViewModels.LoadMessages.ServerName()} ended";

	/// <summary>The words every page shows for it (what happened and what to do).</summary>
	public static string Message() =>
		$"{ViewModels.LoadMessages.ServerName()} no longer accepts this sign-in. Someone may have signed this device out on the server, " +
		"or the password changed. Select Sign in again.";

	/// <summary>The button's words.</summary>
	public const string ButtonText = "Sign in again";
}
