namespace FluentFin.Core.Mpv;

/// <summary>
/// A click on the video plays or pauses at once; a double click is full screen and puts the play state back to what it was
/// before its first click. 1.2.0 (the interaction audit, item 8): 1.1.3 made the click wait the system's double-click time
/// (500 ms by default) so a double click never paused, and the picture went on for half a second after every click. YouTube,
/// Netflix and the Windows Media Player app pause on the click itself. The restore is an absolute pause or play, so a state
/// the backend sent before the click's own toggle landed cannot make it wrong. UI thread only.
/// </summary>
public sealed class VideoClicks
{
	private DateTimeOffset? _clickedAt;
	private bool _pausedBefore;

	/// <summary>The system's double-click time: a second click within it is a double click.</summary>
	public TimeSpan DoubleClickTime { get; }

	public VideoClicks(TimeSpan doubleClickTime) =>
		DoubleClickTime = doubleClickTime > TimeSpan.Zero && doubleClickTime <= TimeSpan.FromSeconds(5) ? doubleClickTime : TimeSpan.FromMilliseconds(500);

	/// <summary>A click at <paramref name="now"/>, which plays or pauses at once; <paramref name="pausedBefore"/> is the state before it.</summary>
	public void Clicked(DateTimeOffset now, bool pausedBefore)
	{
		_clickedAt = now;
		_pausedBefore = pausedBefore;
	}

	/// <summary>Another play or pause (a key, the button) or a new item, or the page going: a double click after it restores nothing.</summary>
	public void Cancel() => _clickedAt = null;

	/// <summary>
	/// A double click at <paramref name="now"/> (full screen follows): the play state to put back (true: paused), when its
	/// first click came within the double-click time; null when there is nothing to put back.
	/// </summary>
	public bool? DoubleClicked(DateTimeOffset now)
	{
		var restore = _clickedAt is { } at && now - at <= DoubleClickTime ? _pausedBefore : (bool?)null;
		_clickedAt = null;
		return restore;
	}
}
