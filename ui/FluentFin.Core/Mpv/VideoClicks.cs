namespace FluentFin.Core.Mpv;

/// <summary>
/// Seaglass 1.1.3: a click on the video plays or pauses, a double click is full screen only. The click's play/pause waits
/// the system's double-click time (the page's timer), and a double click within it cancels it. 1.1.2 paused at the first
/// click and unpaused at the second, so going full screen with a double click made the playback stutter. UI thread only.
/// </summary>
public sealed class VideoClicks
{
	private bool _pending;
	private DateTimeOffset? _toggledAt;

	/// <summary>The system's double-click time (and the click's wait).</summary>
	public TimeSpan DoubleClickTime { get; }

	public VideoClicks(TimeSpan doubleClickTime) =>
		DoubleClickTime = doubleClickTime > TimeSpan.Zero && doubleClickTime <= TimeSpan.FromSeconds(5) ? doubleClickTime : TimeSpan.FromMilliseconds(500);

	/// <summary>A click is waiting for its play/pause.</summary>
	public bool IsPending => _pending;

	/// <summary>A click on the video: its play/pause waits (the caller starts its timer for <see cref="DoubleClickTime"/>).</summary>
	public void Clicked() => _pending = true;

	/// <summary>The click's wait is over at <paramref name="now"/>: true when its play/pause is to happen now.</summary>
	public bool WaitOver(DateTimeOffset now)
	{
		if (!_pending)
		{
			return false;
		}

		_pending = false;
		_toggledAt = now;
		return true;
	}

	/// <summary>
	/// A double click at <paramref name="now"/> (full screen follows): the waiting play/pause is dropped. True only when the
	/// click's play/pause already happened within the double-click time (its timer ran a little early): undo it then.
	/// </summary>
	public bool DoubleClicked(DateTimeOffset now)
	{
		if (_pending)
		{
			_pending = false;
			return false;
		}

		var undo = _toggledAt is { } at && now - at < DoubleClickTime;
		_toggledAt = null;
		return undo;
	}
}
