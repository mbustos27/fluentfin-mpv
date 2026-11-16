namespace FluentFin.Core.Journal;

/// <summary>What to do with a finished play session.</summary>
public sealed record AutoLogDecision(bool Log, bool OnServer, string Reason)
{
	public static AutoLogDecision Ignore(string reason) => new(false, false, reason);

	public static AutoLogDecision Logged(string reason, bool onServer = true) => new(true, onServer, reason);
}

/// <summary>
/// When a play session becomes a diary entry (docs/JOURNAL.md, decision 3). Pure functions over what the tracker saw and
/// the user data the server had before and after.
/// <list type="number">
/// <item><c>reported: sent</c>: the server took the Stopped report; its user data now says whether the play counted. Logged
/// when the item is <c>Played</c> now and either it was not before (Played flipped), or its <c>PlayCount</c> rose and this
/// session ended at the end (eof or 90 %). Jellyfin raises <c>PlayCount</c> when a play <i>starts</i> (and resets
/// <c>Played</c> for videos), so a risen count alone would log an abandoned rewatch. With no "before" (its read failed) or
/// no way to ask (the server read failed, or FluentFin switched server or user since), the session's own end decides.</item>
/// <item><c>reported: failed</c>: the server may not know; logged locally when the session ended at the end (eof or 90 %),
/// and the mark-played is queued for later.</item>
/// <item>Plays shorter than a few minutes are ignored (seeking to the end of something just opened), unless the session
/// resumed past the middle (finishing an earlier play).</item>
/// <item><c>none</c> (never reached the server) and outcomes this version does not know: nothing.</item>
/// </list>
/// </summary>
public static class AutoLogRules
{
	/// <summary>"A few minutes": a play that watched less than this (or half the runtime, if shorter) is ignored.</summary>
	public static readonly TimeSpan ShortPlay = TimeSpan.FromMinutes(3);

	/// <summary>Watched to here counts as finished (Jellyfin's default "Max resume %" is 90).</summary>
	public const double FinishedFraction = 0.9;

	public static bool IsShortPlay(PlayCompleted play)
	{
		var runtime = play.RuntimeTicks;
		var threshold = runtime is { } r ? Math.Min(ShortPlay.Ticks, r / 2) : ShortPlay.Ticks;
		if (play.WatchedTicks >= threshold)
		{
			return false;
		}

		// the end of a play that started on another day: it resumed past the middle
		return !(runtime is { } total && play.StartPositionTicks is { } start && start >= total / 2);
	}

	/// <summary>The session itself shows it was watched to the end: mpv reached the end of the file, or the last position was 90 % or more.</summary>
	public static bool EndedAtTheEnd(PlayCompleted play) =>
		string.Equals(play.EndedReason, "eof", StringComparison.Ordinal) ||
		(play.RuntimeTicks is { } runtime && play.LastPositionTicks is { } last && last >= runtime * FinishedFraction);

	/// <param name="before">The user data when the session opened; null when it could not be read.</param>
	/// <param name="after">The user data after the report; null when it could not be read.</param>
	/// <param name="canAskServer">FluentFin is still signed in as the server and user the session was played as.</param>
	public static AutoLogDecision Decide(PlayCompleted play, JournalUserData? before, JournalUserData? after, bool canAskServer)
	{
		if (IsShortPlay(play))
		{
			return AutoLogDecision.Ignore("shorter than a few minutes");
		}

		switch (play.Outcome)
		{
			case BridgeReported.Sent:
				if (!canAskServer || after is null)
				{
					return EndedAtTheEnd(play)
						? AutoLogDecision.Logged("the server took the report and the session ended at the end (its user data not read)")
						: AutoLogDecision.Ignore("the server's user data could not be read and the session did not end at the end");
				}

				if (!after.Played)
				{
					return AutoLogDecision.Ignore("the server did not count it as played");
				}

				if (before is null)
				{
					return EndedAtTheEnd(play)
						? AutoLogDecision.Logged("played on the server and the session ended at the end")
						: AutoLogDecision.Ignore("played on the server, but no earlier state to compare and the session did not end at the end");
				}

				if (!before.Played)
				{
					return AutoLogDecision.Logged("played flipped");
				}

				return after.PlayCount > before.PlayCount && EndedAtTheEnd(play)
					? AutoLogDecision.Logged("play count rose and the session ended at the end")
					: AutoLogDecision.Ignore("already played before, and this session did not finish it");

			case BridgeReported.Failed:
				return EndedAtTheEnd(play)
					? AutoLogDecision.Logged("the report failed but the session ended at the end; logged locally", onServer: false)
					: AutoLogDecision.Ignore("the report failed and the session did not end at the end");

			default:
				return AutoLogDecision.Ignore($"outcome '{play.Outcome}'");
		}
	}
}
