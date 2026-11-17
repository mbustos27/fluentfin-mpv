namespace FluentFin.Core.Mpv;

/// <summary>
/// The state logic of switching the player's window into and out of full screen, kept apart from the window so it can be
/// tested: one switch runs at a time (the presenter change needs a few frames to settle: layout, the picture's hold, the
/// swap chain), and a request that arrives meanwhile (F twice quickly, F then Esc) does not start a second one on top of
/// it. The last request wins: it is applied once the running switch has <see cref="Settled"/>. UI thread only.
/// </summary>
public sealed class FullScreenSwitch(Func<bool> isFullScreen, Action<bool> apply)
{
	private bool _busy;
	private bool _applying;
	private bool? _pending;

	public enum Outcome
	{
		/// <summary>The window is being switched now.</summary>
		Applied,

		/// <summary>A switch is running: this one follows it (a later request replaces it).</summary>
		Queued,

		/// <summary>The window is already in the state asked for.</summary>
		Unchanged,
	}

	/// <summary>A switch has begun and has not settled yet.</summary>
	public bool IsSwitching => _busy;

	/// <summary>
	/// The state the window will be in once everything asked for has been applied: also while the running switch has not
	/// changed the presenter yet (Seaglass 1.1.3: the cover is drawn for a frame first), so leaving the page then still
	/// restores the window.
	/// </summary>
	public bool Target => _pending ?? (_busy ? _applying : isFullScreen());

	public Outcome Request(bool fullScreen)
	{
		if (_busy)
		{
			// the last word counts; asking for what the running switch makes anyway is nothing to follow it with
			_pending = fullScreen;
			return Outcome.Queued;
		}

		if (isFullScreen() == fullScreen)
		{
			return Outcome.Unchanged;
		}

		_busy = true;
		_applying = fullScreen;
		try
		{
			apply(fullScreen);
		}
		catch
		{
			_busy = false;
			throw;
		}

		return Outcome.Applied;
	}

	/// <summary>The running switch is done (a clean frame at the final size, or its time ran out): a request that waited goes now.</summary>
	public void Settled()
	{
		if (!_busy)
		{
			return;
		}

		_busy = false;
		if (_pending is { } next)
		{
			_pending = null;
			Request(next);
		}
	}
}
