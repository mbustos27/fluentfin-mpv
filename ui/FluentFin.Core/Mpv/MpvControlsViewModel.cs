using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FluentFin.Core.Mpv;

/// <summary>One entry of the controls' audio or subtitles menu: a Jellyfin stream index (-1: subtitles off), its name, whether it plays.</summary>
public sealed record MpvTrackChoice(int Index, string Label, bool IsSelected);

/// <summary>What a key over the player does with FluentFin's controls (docs/research/player-controls.md).</summary>
public enum MpvControlsAction
{
	TogglePause,
	SeekBack,
	SeekForward,
	SeekBackLong,
	SeekForwardLong,
	VolumeUp,
	VolumeDown,
	ToggleMute,
	ToggleFullScreen,
	Escape,
	Next,
	Previous,
	SkipSegment,
	SeekBackFar,
	SeekForwardFar,
	/// <summary>Home: back to the start.</summary>
	SeekStart,
	ToggleSubtitles,
	CycleSubtitles,
	CycleAudio,
	/// <summary>The digits: a tenth of the length each. Keep the ten in order (<see cref="MpvControlsViewModel.PercentOf"/>).</summary>
	Percent0, Percent1, Percent2, Percent3, Percent4, Percent5, Percent6, Percent7, Percent8, Percent9,
}

/// <summary>
/// fluentfin-mpv: the state and the actions of FluentFin's own player controls over the embedded mpv. Fed the backend's
/// events (<see cref="OnEvent"/>), it sends bridge commands through the function it is given; the page drives
/// <see cref="Tick"/> (a few times a second) for auto-hide and the moving position. UI thread only, no timers of its own,
/// so every behaviour can be stepped through in a test with a clock that does not move by itself.
/// </summary>
public sealed partial class MpvControlsViewModel : ObservableObject
{
	/// <summary>The controls hide after this long without input (Jellyfin web, Stremio: 3 s).</summary>
	public static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(3);

	/// <summary>A seek just sent: the position shown stays where it went until the backend reports it there, at most this long.</summary>
	public static readonly TimeSpan SeekSettle = TimeSpan.FromSeconds(2);

	/// <summary>
	/// A volume or mute change just made here: for this long the backend's state does not set them back (a state sent
	/// before the change landed would, say, show 95 between two wheel notches that went 100, 95, 90).
	/// </summary>
	public static readonly TimeSpan AudioSettle = TimeSpan.FromSeconds(1.5);

	public const int SmallSeekSeconds = 5;
	public const int LargeSeekSeconds = 10;
	public const int SkipBackSeconds = 10;
	public const int SkipForwardSeconds = 30;
	/// <summary>The default of <see cref="Step"/>.</summary>
	public const int VolumeStep = 5;

	/// <summary>How much a wheel notch or Up/Down changes the volume, 1 to 20 (Settings > Playback, "Volume step").</summary>
	public int Step
	{
		get => field;
		set => field = Math.Clamp(value, 1, 20);
	} = VolumeStep;

	/// <summary>The wheel over the player is volume (Settings > Playback, "Change the volume with the mouse wheel"); off, it does nothing.</summary>
	public bool WheelChangesVolume { get; set; } = true;

	private readonly Func<string, JsonObject?, Task> _send;
	private readonly Func<DateTimeOffset> _now;

	private DateTimeOffset _lastActivity;
	private long _reportedPosition;
	private DateTimeOffset _reportedAt;
	private long? _seekTarget;
	private DateTimeOffset _seekAt;
	private bool _scrubbing;
	private int _menusOpen;
	private double _bottomBarFraction;
	private double? _sentInset;
	private bool _announced;
	private int? _pendingVolume;
	private bool _pendingUnmute;
	private bool _volumeInFlight;
	private DateTimeOffset _audioChangedAt;

	/// <param name="send">Sends one bridge command; never throws (the player page's sender logs and swallows).</param>
	/// <param name="now">The clock; the page passes the real one, tests one they move.</param>
	public MpvControlsViewModel(Func<string, JsonObject?, Task> send, Func<DateTimeOffset>? now = null)
	{
		_send = send;
		_now = now ?? (() => DateTimeOffset.Now);
		_lastActivity = _now();
	}

	/// <summary>Set by the page: full screen to a given state (the window, the title bar, the pane, and the backend told).</summary>
	public Action<bool>? SetFullScreen { get; set; }

	/// <summary>Set by the page: Esc, which leaves full screen, or else the page.</summary>
	public Action? Escape { get; set; }

	/// <summary>Set by the page: back (the top bar's arrow).</summary>
	public Action? GoBack { get; set; }

	[ObservableProperty]
	public partial bool IsPaused { get; set; }

	[ObservableProperty]
	public partial bool IsOpening { get; set; } = true;

	[ObservableProperty]
	public partial long PositionTicks { get; set; }

	[ObservableProperty]
	public partial long DurationTicks { get; set; }

	[ObservableProperty]
	public partial string ElapsedText { get; set; } = "0:00";

	[ObservableProperty]
	public partial string RemainingText { get; set; } = "";

	/// <summary>The series, or the film's name.</summary>
	[ObservableProperty]
	public partial string Title { get; set; } = "";

	/// <summary>"S6E4 · Episode title" for an episode, else empty.</summary>
	[ObservableProperty]
	public partial string Subtitle { get; set; } = "";

	[ObservableProperty]
	public partial bool CanPrevious { get; set; }

	[ObservableProperty]
	public partial bool CanNext { get; set; }

	[ObservableProperty]
	public partial int Volume { get; set; } = 100;

	[ObservableProperty]
	public partial bool IsMuted { get; set; }

	/// <summary>The subtitle text size in percent (<see cref="SubtitleSizes"/>): the remembered one, sent when the controls take over.</summary>
	[ObservableProperty]
	public partial int SubtitleSize { get; set; } = SubtitleSizes.Default;

	/// <summary>"120%".</summary>
	public string SubtitleSizeText => SubtitleSizes.Text(SubtitleSize);

	/// <summary>Told the new size (to remember it); null: nothing remembered.</summary>
	public Action<int>? SubtitleSizeChanged { get; set; }

	partial void OnSubtitleSizeChanged(int value) => OnPropertyChanged(nameof(SubtitleSizeText));

	[ObservableProperty]
	public partial bool IsFullScreen { get; set; }

	/// <summary>The top and bottom bars are shown.</summary>
	[ObservableProperty]
	public partial bool ControlsVisible { get; set; } = true;

	/// <summary>The pointer is over the controls (a bar, a menu): they do not hide under it.</summary>
	[ObservableProperty]
	public partial bool PointerOverControls { get; set; }

	/// <summary>
	/// The keyboard is on one of the controls (Tab reached a button or the seek bar): they do not hide under it, or the
	/// focused control would vanish and take the keyboard with it. Set by the view.
	/// </summary>
	[ObservableProperty]
	public partial bool KeyboardInControls { get; set; }

	/// <summary>The mouse pointer is hidden over the video (the controls hid).</summary>
	[ObservableProperty]
	public partial bool CursorHidden { get; set; }

	/// <summary>The current item is a Jellyfin item (not the self-test's file): what is played is known.</summary>
	public string? ItemId { get; private set; }

	/// <summary>Anything of the controls' is on screen (the window they draw in is shown only then).</summary>
	public bool AnythingVisible => ControlsVisible || VolumeIndicatorVisible || IsSkipVisible;

	partial void OnControlsVisibleChanged(bool value)
	{
		OnPropertyChanged(nameof(AnythingVisible));
		_ = SendInsetIfChanged();
	}

	/// <summary>Mouse movement or a click over the video or the controls: they show, and stay for <see cref="HideAfter"/>.</summary>
	public void NotifyActivity()
	{
		_lastActivity = _now();
		CursorHidden = false;
		ControlsVisible = true;
	}

	/// <summary>A menu of the controls (audio, subtitles, volume) opened or closed: they do not hide while one is open.</summary>
	public void MenuOpened(bool open)
	{
		_menusOpen = Math.Max(0, _menusOpen + (open ? 1 : -1));
		NotifyActivity();
	}

	/// <summary>Why the controls stay up now, whatever the time since the last input; null when nothing holds them.</summary>
	public string? HeldBy => IsPaused ? "paused" : _menusOpen > 0 ? "a menu is open" : PointerOverControls ? "the pointer is on them"
		: KeyboardInControls ? "the keyboard is on them" : _scrubbing ? "seeking" : null;

	/// <summary>The page's heartbeat: auto-hide, and the position moving between the backend's position events.</summary>
	public void Tick()
	{
		var now = _now();
		if (ControlsVisible && HeldBy is null && now - _lastActivity >= HideAfter)
		{
			ControlsVisible = false;
			CursorHidden = true;
		}

		if (VolumeIndicatorVisible && now >= _volumeIndicatorUntil)
		{
			VolumeIndicatorVisible = false;
		}

		if (!_scrubbing)
		{
			ShowPosition(CurrentPosition(now));
		}
	}

	/// <summary>Where playback is now: the last report, moved on by the time since while playing, or a seek not yet reported.</summary>
	private long CurrentPosition(DateTimeOffset now)
	{
		if (_seekTarget is { } target)
		{
			if (now - _seekAt < SeekSettle)
			{
				return target;
			}

			_seekTarget = null;
		}

		var position = _reportedPosition;
		if (!IsPaused && !IsOpening && _reportedAt != default)
		{
			position += (now - _reportedAt).Ticks;
		}

		return DurationTicks > 0 ? Math.Min(position, DurationTicks) : position;
	}

	private void ShowPosition(long ticks)
	{
		PositionTicks = Math.Max(0, ticks);
		ElapsedText = FormatTime(TimeSpan.FromTicks(PositionTicks));
		RemainingText = DurationTicks > 0 ? "-" + FormatTime(TimeSpan.FromTicks(Math.Max(0, DurationTicks - PositionTicks))) : "";
	}

	// -- backend events ------------------------------------------------------------------------------------------------

	/// <summary>One backend event (on the UI thread).</summary>
	public void OnEvent(BridgeEvent e)
	{
		switch (e.Name)
		{
			case Bridge.Events.State when e.DataAs<BridgeState>() is { } state:
				OnState(state);
				break;
			case Bridge.Events.Position when e.DataAs<BridgePosition>() is { } position:
				OnPosition(position);
				break;
			case Bridge.Events.Item when e.DataAs<BridgeItem>() is { } item:
				OnItem(item);
				break;
			case Bridge.Events.Tracks when e.DataAs<BridgeTracks>() is { } tracks:
				OnTracks(tracks);
				break;
			case Bridge.Events.Segments when e.DataAs<BridgeSegments>() is { } segments:
				OnSegments(segments);
				break;
			case Bridge.Events.Next when e.DataAs<BridgeNext>() is { } next:
				OnNext(next);
				break;
		}
	}

	public void OnState(BridgeState state)
	{
		var now = _now();
		var position = CurrentPosition(now);
		IsOpening = state.State == Bridge.States.Opening;
		IsPaused = state.State == Bridge.States.Paused;
		if (!_volumeInFlight && now - _audioChangedAt >= AudioSettle)
		{
			Volume = state.Volume;
			IsMuted = state.Muted;
		}

		DropOfferNotOf(state);
		OnTrackIndices(state.AudioStreamIndex, state.SubtitleStreamIndex);
		if (state.Queue is { ItemIds.Count: > 0 } queue)
		{
			CanPrevious = queue.Index > 0;
			_queueCount = queue.ItemIds.Count;
			if (!_nextKnown)
			{
				// until the item's next event says (an older backend never does)
				CanNext = queue.Index < queue.ItemIds.Count - 1;
			}

			UpdateShowsNext();
		}

		if (state.DurationTicks is long duration && duration > 0)
		{
			DurationTicks = duration;
		}

		// the position moves on from here at the new pace (paused: not at all)
		_reportedPosition = position;
		_reportedAt = now;
		ShowPosition(position);
	}

	public void OnPosition(BridgePosition position)
	{
		var now = _now();
		if (position.DurationTicks is long duration && duration > 0)
		{
			DurationTicks = duration;
		}

		if (_seekTarget is { } target && Math.Abs(position.PositionTicks - target) < TimeSpan.FromSeconds(2).Ticks)
		{
			// the seek has landed
			_seekTarget = null;
		}

		_reportedPosition = position.PositionTicks;
		_reportedAt = now;
		if (!_scrubbing)
		{
			ShowPosition(CurrentPosition(now));
		}
	}

	public void OnItem(BridgeItem item)
	{
		ItemId = item.ItemId;
		(Title, Subtitle) = FormatTitle(item);
		if (item.RunTimeTicks is long runtime && runtime > 0)
		{
			DurationTicks = runtime;
		}

		// a new item: its own segments and next item come with the events that follow this one
		_offered = null;
		_skipped = null;
		UpdateSkip();
		_isEpisode = item.Type == "Episode";
		_nextKnown = false;
		_nextItemId = null;
		NextToolTip = NextEpisode;
		UpdateShowsNext();

		// its own tracks come with the tracks event that follows this one
		_audioTracks = [];
		_subtitleTracks = [];
		_audioIndex = null;
		_subtitleIndex = null;
		RebuildTrackChoices();

		// a new item starts from its own position, not the last one's
		_seekTarget = null;
		_reportedPosition = 0;
		_reportedAt = default;
		ShowPosition(0);
		NotifyActivity();
	}

	// -- actions --------------------------------------------------------------------------------------------------------

	public Task TogglePause()
	{
		NotifyActivity();
		IsPaused = !IsPaused;
		_reportedPosition = CurrentPosition(_now());
		_reportedAt = _now();
		return _send(Bridge.Commands.TogglePause, null);
	}

	public Task SeekBy(int seconds)
	{
		NotifyActivity();
		var target = Math.Max(0, CurrentPosition(_now()) + TimeSpan.FromSeconds(seconds).Ticks);
		if (DurationTicks > 0)
		{
			target = Math.Min(target, DurationTicks);
		}

		HoldSeek(target);
		OfferAgainIfBackIn(target);
		return _send(Bridge.Commands.SeekRelative, BridgeCommands.SeekRelative(seconds));
	}

	/// <summary>The seek bar is being dragged: the position follows the thumb, not the backend.</summary>
	public void BeginScrub()
	{
		_scrubbing = true;
		NotifyActivity();
	}

	/// <summary>The thumb moved while dragging: elapsed and remaining follow it (nothing is sent yet).</summary>
	public void Scrub(long ticks)
	{
		NotifyActivity();
		ShowPosition(Clamp(ticks));
	}

	/// <summary>The thumb was let go (or the bar clicked): one seek, there.</summary>
	public Task SeekTo(long ticks)
	{
		_scrubbing = false;
		NotifyActivity();
		var target = Clamp(ticks);
		HoldSeek(target);
		OfferAgainIfBackIn(target);
		return _send(Bridge.Commands.Seek, BridgeCommands.Seek(target));
	}

	private long Clamp(long ticks) => Math.Max(0, DurationTicks > 0 ? Math.Min(ticks, DurationTicks) : ticks);

	private void HoldSeek(long target)
	{
		_seekTarget = target;
		_seekAt = _now();
		ShowPosition(target);
	}

	public Task Next()
	{
		NotifyActivity();
		return CanNext ? _send(Bridge.Commands.Next, null) : Task.CompletedTask;
	}

	public Task Previous()
	{
		NotifyActivity();
		return CanPrevious ? _send(Bridge.Commands.Previous, null) : Task.CompletedTask;
	}

	public Task ToggleMute()
	{
		NotifyActivity();
		IsMuted = !IsMuted;
		ShowVolumeIndicator();
		_audioChangedAt = _now();
		// a slider move's unmute still waiting would undo this mute
		_pendingUnmute = false;
		return _send(Bridge.Commands.SetMute, BridgeCommands.Mute(IsMuted));
	}

	/// <summary>
	/// The volume slider moved (0-100). Unmutes, like every player. The slider and the wheel fire fast: one command at a
	/// time, the unmute first and then the latest volume, each sent when the one before has been answered, so the last
	/// value sent is always the newest (a notch made while the unmute was on its way never lands before an older one).
	/// </summary>
	public Task SetVolume(int volume, bool wakeControls = true)
	{
		if (wakeControls)
		{
			NotifyActivity();
		}

		volume = Math.Clamp(volume, 0, 100);
		Volume = volume;
		_audioChangedAt = _now();
		_pendingVolume = volume;
		if (IsMuted && volume > 0)
		{
			IsMuted = false;
			_pendingUnmute = true;
		}

		return SendAudio();
	}

	/// <summary>Unmutes without touching the volume (a step up from a volume FluentFin cannot raise any further).</summary>
	private Task Unmute()
	{
		IsMuted = false;
		_audioChangedAt = _now();
		_pendingUnmute = true;
		return SendAudio();
	}

	/// <summary>Sends what is pending, one command at a time, unless a call before this one is already sending (it sends it too).</summary>
	private async Task SendAudio()
	{
		if (_volumeInFlight)
		{
			return;
		}

		_volumeInFlight = true;
		try
		{
			while (true)
			{
				if (_pendingUnmute)
				{
					_pendingUnmute = false;
					await _send(Bridge.Commands.SetMute, BridgeCommands.Mute(false));
				}
				else if (_pendingVolume is { } next)
				{
					_pendingVolume = null;
					await _send(Bridge.Commands.SetVolume, BridgeCommands.Volume(next));
				}
				else
				{
					break;
				}
			}
		}
		finally
		{
			_volumeInFlight = false;
		}
	}

	/// <summary>
	/// The volume <paramref name="steps"/> times <see cref="Step"/> up (or down), 0 to 100 (the protocol's range). A step up
	/// never lowers it: a volume above 100 (an mpv.conf's) stays, and only a mute is undone.
	/// </summary>
	private Task StepVolume(int steps, bool wakeControls)
	{
		ShowVolumeIndicator();
		if (steps > 0 && Volume >= 100)
		{
			if (wakeControls)
			{
				NotifyActivity();
			}

			return IsMuted ? Unmute() : Task.CompletedTask;
		}

		var volume = Math.Clamp(Volume + steps * Step, 0, 100);
		return volume == Volume && !IsMuted ? Task.CompletedTask : SetVolume(volume, wakeControls);
	}

	public void ToggleFullScreen()
	{
		NotifyActivity();
		SetFullScreen?.Invoke(!IsFullScreen);
	}

	// -- Skip intro / Skip credits (the backend's segments) -----------------------------------------------------------

	/// <summary>The segment the backend offers a skip for now (<see cref="BridgeSegments.Offered"/>), null when none.</summary>
	private BridgeSegment? _offered;

	/// <summary>The offer a skip was sent for: the button stays away from it until the backend's offer changes.</summary>
	private BridgeSegment? _skipped;

	/// <summary>The item the offer came with (its segments event's), so a state of another item can drop it.</summary>
	private string? _offerItemId;

	/// <summary>
	/// Settings > Playback, "Show Skip intro and Skip credits": off, the Skip button never shows. S still skips what is on
	/// offer, as S does in FluentFin's built-in players, whose S (KeyboardMediaPlayerController) does not read the setting
	/// either (VideoPlayerViewModel.Skip). Which segments are offered at all is the backend's settings' (ask, always: it
	/// skips them by itself, off: not listed), and reaches here only as its offer (docs/BRIDGE.md §5 segments).
	/// </summary>
	public bool ShowSkipPrompt
	{
		get => field;
		set
		{
			field = value;
			UpdateSkip();
		}
	} = true;

	/// <summary>The Skip button shows: a segment is on offer, not skipped yet, and the setting allows it. Also while the bars are hidden.</summary>
	[ObservableProperty]
	public partial bool IsSkipVisible { get; set; }

	/// <summary>The Skip button's text and accessible name: "Skip intro", "Skip credits" (<see cref="SkipTextFor"/>).</summary>
	[ObservableProperty]
	public partial string SkipText { get; set; } = SkipTextFor("Intro");

	partial void OnIsSkipVisibleChanged(bool value) => OnPropertyChanged(nameof(AnythingVisible));

	/// <summary>
	/// The <c>segments</c> event (docs/BRIDGE.md §5): the backend's offer follows the position at its own sampling (about
	/// every 0.5 s), so the button shows for as long as the offer lasts, whatever the bars do.
	/// </summary>
	public void OnSegments(BridgeSegments segments)
	{
		if (!IsThisItem(segments.ItemId))
		{
			return;
		}

		var offered = segments.Offered;
		if (offered is null || offered != _skipped)
		{
			// the skipped offer is over (or another one came): the next one shows
			_skipped = null;
		}

		_offered = offered;
		_offerItemId = segments.ItemId ?? ItemId;
		UpdateSkip();
	}

	/// <summary>
	/// The Skip button or S: <c>skip_segment</c> for the segment on offer (the backend seeks to its end). The button goes at
	/// once and the time shows the segment's end; nothing on offer (or already skipped), nothing is sent.
	/// </summary>
	public Task SkipSegment()
	{
		NotifyActivity();
		if (_offered is not { } segment || segment == _skipped)
		{
			return Task.CompletedTask;
		}

		_skipped = segment;
		UpdateSkip();
		HoldSeek(Clamp(segment.EndTicks));
		return _send(Bridge.Commands.SkipSegment, null);
	}

	/// <summary>
	/// A seek of the user's back into a segment that was skipped: its skip is offered again. The backend's offer may never
	/// have left it (its sampling, about every 0.5 s, can miss the time at the segment's end between a skip and a quick
	/// seek back), so waiting for the offer to change would leave the button away, and S doing nothing, for the segment.
	/// </summary>
	private void OfferAgainIfBackIn(long target)
	{
		if (_skipped is { } skipped && target >= skipped.StartTicks && target < skipped.EndTicks)
		{
			_skipped = null;
			UpdateSkip();
		}
	}

	/// <summary>
	/// The offer ends with its item. The backend drops it at <c>ended</c> without a <c>segments</c> event (and the controls do
	/// not get <c>ended</c>), so a state that is no longer playback, or that names another item (the next one opening), drops
	/// it here. Not every opening: the backend writes a new item's segments before that tick's state, so an opening of the
	/// offer's own item keeps it (an intro from 0:00).
	/// </summary>
	private void DropOfferNotOf(BridgeState state)
	{
		if (_offered is null && _skipped is null)
		{
			return;
		}

		var otherItem = state.ItemId is { } id && _offerItemId is { } offerItem && !string.Equals(id, offerItem, StringComparison.OrdinalIgnoreCase);
		if (!state.IsActive || otherItem)
		{
			_offered = null;
			_skipped = null;
			UpdateSkip();
		}
	}

	private void UpdateSkip()
	{
		if (_offered is { } offered)
		{
			SkipText = SkipTextFor(offered.Type);
		}

		IsSkipVisible = ShowSkipPrompt && _offered is { } shown && shown != _skipped;
	}

	/// <summary>The Skip button's words for a Jellyfin segment type, in FluentFin's sentence case ("Skip credits" for an outro).</summary>
	public static string SkipTextFor(string? type) => type switch
	{
		"Outro" => "Skip credits",
		"Commercial" => "Skip commercial",
		"Preview" => "Skip preview",
		"Recap" => "Skip recap",
		_ => "Skip intro",
	};

	/// <summary>An event about <paramref name="itemId"/> is about the item playing now (or no item is known yet).</summary>
	private bool IsThisItem(string? itemId) => ItemId is null || itemId is null || string.Equals(itemId, ItemId, StringComparison.OrdinalIgnoreCase);

	// -- Next episode (the backend's next event) ------------------------------------------------------------------------

	private const string NextEpisode = "Next episode";

	private bool _nextKnown;
	private string? _nextItemId;
	private bool _isEpisode;
	private int _queueCount;

	/// <summary>
	/// Set by the page: the title of an item by its id (the <c>next</c> event carries ids only, docs/BRIDGE.md §5), for the
	/// Next button's tooltip. Null or a null answer: the tooltip stays "Next episode".
	/// </summary>
	public Func<string, Task<string?>>? NameItem { get; set; }

	/// <summary>The Next button is there: an episode, or a queue of more than one item (it may be disabled, <see cref="CanNext"/>).</summary>
	[ObservableProperty]
	public partial bool ShowsNext { get; set; }

	/// <summary>"Next: S1E2 · Second" once the next item's name is known; "Next episode" before, "No next episode" at the end of the queue.</summary>
	[ObservableProperty]
	public partial string NextToolTip { get; set; } = NextEpisode;

	private void UpdateShowsNext() => ShowsNext = CanNext || _isEpisode || _queueCount > 1;

	partial void OnCanNextChanged(bool value) => UpdateShowsNext();

	/// <summary>
	/// The <c>next</c> event: what <c>next</c> would play from this item. It enables the Next button (the queue in the
	/// state only until this event came) and names the item in its tooltip.
	/// </summary>
	public void OnNext(BridgeNext next)
	{
		if (!IsThisItem(next.ItemId))
		{
			return;
		}

		_nextKnown = true;
		CanNext = next.NextItemId is not null;
		if (next.NextItemId == _nextItemId && NextToolTip != NextEpisode)
		{
			return;
		}

		_nextItemId = next.NextItemId;
		NextToolTip = next.NextItemId is null ? "No next episode" : NextEpisode;
		if (next.NextItemId is { } id)
		{
			_ = NameNext(id);
		}
	}

	private async Task NameNext(string id)
	{
		if (NameItem is not { } name)
		{
			return;
		}

		string? title;
		try
		{
			title = await name(id);
		}
		catch (Exception)
		{
			// the page's lookup logs its own failures: the tooltip just stays unnamed
			return;
		}

		if (_nextItemId == id && !string.IsNullOrWhiteSpace(title))
		{
			NextToolTip = $"Next: {title.Trim()}";
		}
	}

	/// <summary>How the Next button's tooltip names an item: "S1E2 · Second" for an episode (as the top bar's second line), else its name.</summary>
	public static string NextTitle(BridgeItem item)
	{
		var (title, subtitle) = FormatTitle(item);
		return subtitle.Length > 0 ? subtitle : title;
	}

	// -- audio and subtitle tracks -------------------------------------------------------------------------------------

	private IReadOnlyList<BridgeTrack> _audioTracks = [];
	private IReadOnlyList<BridgeTrack> _subtitleTracks = [];
	private int? _audioIndex;
	private int? _subtitleIndex;

	/// <summary>The audio menu: one entry per track of the item, the one playing selected.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<MpvTrackChoice> AudioChoices { get; set; } = [];

	/// <summary>The subtitles menu: "Off", then one entry per track, the one showing selected.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<MpvTrackChoice> SubtitleChoices { get; set; } = [];

	/// <summary>There is something to choose between (two audio tracks or more).</summary>
	public bool HasAudioChoice => AudioChoices.Count > 1;

	/// <summary>There is a subtitle track to turn on or off.</summary>
	public bool HasSubtitleChoice => SubtitleChoices.Count > 1;

	partial void OnAudioChoicesChanged(IReadOnlyList<MpvTrackChoice> value) => OnPropertyChanged(nameof(HasAudioChoice));

	partial void OnSubtitleChoicesChanged(IReadOnlyList<MpvTrackChoice> value) => OnPropertyChanged(nameof(HasSubtitleChoice));

	/// <summary>The <c>tracks</c> event: the item's streams, by Jellyfin index (docs/BRIDGE.md §5).</summary>
	public void OnTracks(BridgeTracks tracks)
	{
		_audioTracks = tracks.Audio ?? [];
		_subtitleTracks = tracks.Subtitle ?? [];
		RebuildTrackChoices();
	}

	/// <summary>Which streams play now (the <c>state</c>'s indices); null is "not known", -1 subtitles off.</summary>
	private void OnTrackIndices(int? audio, int? subtitle)
	{
		if (audio == _audioIndex && subtitle == _subtitleIndex)
		{
			return;
		}

		_audioIndex = audio;
		_subtitleIndex = subtitle;
		RebuildTrackChoices();
	}

	private void RebuildTrackChoices()
	{
		AudioChoices = [.. _audioTracks.Select(t => new MpvTrackChoice(t.Index, AudioLabel(t), t.Index == _audioIndex))];
		SubtitleChoices = _subtitleTracks.Count == 0
			? []
			: [new MpvTrackChoice(-1, "Off", _subtitleIndex is null or -1), .. _subtitleTracks.Select(t => new MpvTrackChoice(t.Index, SubtitleLabel(t), t.Index == _subtitleIndex))];
	}

	/// <summary>The digit keys: a tenth of the length per digit (0 is the start). Nothing is sent until the length is known.</summary>
	public Task SeekToPercent(int tenths)
	{
		NotifyActivity();
		return tenths is >= 0 and <= 9 && (tenths == 0 || DurationTicks > 0) ? SeekTo(DurationTicks / 10 * tenths) : Task.CompletedTask;
	}

	/// <summary>Which digit a Percent action is.</summary>
	public static int PercentOf(MpvControlsAction action) => action - MpvControlsAction.Percent0;

	private int _lastSubtitle = -1;

	/// <summary>V: the next subtitle choice (off, then each track, then off again). Nothing when the item has none.</summary>
	public Task CycleSubtitles() => CycleTo(SubtitleChoices, SetSubtitle);

	/// <summary>B: the next audio track. Nothing with fewer than two.</summary>
	public Task CycleAudio() => CycleTo(AudioChoices, SetAudio);

	/// <summary>C: subtitles off if showing, else back on to the track that showed last (the first when none did).</summary>
	public Task ToggleSubtitles()
	{
		if (!HasSubtitleChoice)
		{
			return Task.CompletedTask;
		}

		var current = SubtitleChoices.FirstOrDefault(c => c.IsSelected)?.Index ?? -1;
		if (current >= 0)
		{
			_lastSubtitle = current;
			return SetSubtitle(-1);
		}

		var back = SubtitleChoices.FirstOrDefault(c => c.Index == _lastSubtitle && c.Index >= 0) ?? SubtitleChoices.First(c => c.Index >= 0);
		return SetSubtitle(back.Index);
	}

	private Task CycleTo(IReadOnlyList<MpvTrackChoice> choices, Func<int, Task> pick)
	{
		NotifyActivity();
		if (choices.Count < 2)
		{
			return Task.CompletedTask;
		}

		var at = -1;
		for (var i = 0; i < choices.Count; i++)
		{
			if (choices[i].IsSelected)
			{
				at = i;
				break;
			}
		}

		return pick(choices[(at + 1) % choices.Count].Index);
	}

	/// <summary>The audio menu's pick (a Jellyfin stream index). The one playing already: nothing is sent.</summary>
	public Task SetAudio(int index)
	{
		NotifyActivity();
		if (index == _audioIndex)
		{
			return Task.CompletedTask;
		}

		_audioIndex = index;
		RebuildTrackChoices();
		return _send(Bridge.Commands.SetAudio, BridgeCommands.Index(index));
	}

	/// <summary>The subtitles menu's Text size slider: the new size (snapped to 50..200 in 10s) now, remembered, and sent.</summary>
	public Task SetSubtitleSize(int percent)
	{
		NotifyActivity();
		var size = SubtitleSizes.Clamp(percent);
		if (size == SubtitleSize)
		{
			return Task.CompletedTask;
		}

		SubtitleSize = size;
		SubtitleSizeChanged?.Invoke(size);
		return _send(Bridge.Commands.SetSubtitleSize, BridgeCommands.SubtitleSize(size));
	}

	/// <summary>The subtitles menu's pick: a Jellyfin stream index, or -1 for off.</summary>
	public Task SetSubtitle(int index)
	{
		NotifyActivity();
		var current = _subtitleIndex ?? -1;
		if (index == current)
		{
			return Task.CompletedTask;
		}

		_subtitleIndex = index;
		RebuildTrackChoices();
		return _send(Bridge.Commands.SetSubtitle, BridgeCommands.Index(index));
	}

	/// <summary>
	/// An audio track's name: the server's display title ("English - Dolby Digital - 5.1"), else the language, else the
	/// stream's position; the stream's own title ("Commentary") added when the display title does not already say it.
	/// </summary>
	public static string AudioLabel(BridgeTrack track) => WithStreamTitle(BaseLabel(track), track);

	/// <summary>A subtitle track's name, as <see cref="AudioLabel"/>, with Forced, SDH and External added when the name does not say so.</summary>
	public static string SubtitleLabel(BridgeTrack track)
	{
		var label = WithStreamTitle(BaseLabel(track), track);
		var tags = new List<string>();
		if (track.IsForced == true && !Says(label, "forced"))
		{
			tags.Add("Forced");
		}

		if (track.IsHearingImpaired == true && !Says(label, "sdh") && !Says(label, "hearing"))
		{
			tags.Add("SDH");
		}

		if (track.IsExternal && !Says(label, "external"))
		{
			tags.Add("External");
		}

		return tags.Count == 0 ? label : $"{label} · {string.Join(" · ", tags)}";
	}

	private static string BaseLabel(BridgeTrack track) =>
		!string.IsNullOrWhiteSpace(track.Title) ? track.Title.Trim()
		: !string.IsNullOrWhiteSpace(track.Language) ? LanguageName(track.Language.Trim())
		: string.Create(CultureInfo.InvariantCulture, $"Track {track.Index}");

	private static string WithStreamTitle(string label, BridgeTrack track) =>
		string.IsNullOrWhiteSpace(track.StreamTitle) || Says(label, track.StreamTitle.Trim()) ? label : $"{label} ({track.StreamTitle.Trim()})";

	private static bool Says(string label, string word) => label.Contains(word, StringComparison.OrdinalIgnoreCase);

	/// <summary>"eng" or "en" as the language's English name; the code itself when .NET does not know it.</summary>
	public static string LanguageName(string code)
	{
		try
		{
			var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
				.FirstOrDefault(c => string.Equals(c.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(c.TwoLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase));
			return culture is null || string.IsNullOrEmpty(culture.Name) ? code : culture.EnglishName;
		}
		catch (CultureNotFoundException)
		{
			return code;
		}
	}

	// -- mouse wheel: volume -------------------------------------------------------------------------------------------

	/// <summary>How long the volume indicator stays after the last wheel notch.</summary>
	public static readonly TimeSpan VolumeIndicatorFor = TimeSpan.FromSeconds(1.5);

	/// <summary>One wheel notch, as Windows counts it (WHEEL_DELTA).</summary>
	public const int WheelNotch = 120;

	private DateTimeOffset _volumeIndicatorUntil;
	private int _wheelRemainder;

	/// <summary>The brief volume indicator a wheel turn shows, also while the bars are hidden.</summary>
	[ObservableProperty]
	public partial bool VolumeIndicatorVisible { get; set; }

	partial void OnVolumeIndicatorVisibleChanged(bool value) => OnPropertyChanged(nameof(AnythingVisible));

	/// <summary>"Volume 45 %", or "Muted".</summary>
	public string VolumeText => IsMuted ? "Muted" : string.Create(CultureInfo.InvariantCulture, $"{Volume} %");

	/// <summary>How much of the indicator's bar is filled, 0 to 1: nothing while muted.</summary>
	public double VolumeFraction => IsMuted ? 0 : Math.Clamp(Volume, 0, 100) / 100.0;

	partial void OnVolumeChanged(int value)
	{
		OnPropertyChanged(nameof(VolumeText));
		OnPropertyChanged(nameof(VolumeFraction));
	}

	partial void OnIsMutedChanged(bool value)
	{
		OnPropertyChanged(nameof(VolumeText));
		OnPropertyChanged(nameof(VolumeFraction));
	}

	/// <summary>Shows the indicator for <see cref="VolumeIndicatorFor"/> more (the wheel, the volume keys and the mute key all do).</summary>
	private void ShowVolumeIndicator()
	{
		_volumeIndicatorUntil = _now() + VolumeIndicatorFor;
		VolumeIndicatorVisible = true;
	}

	/// <summary>
	/// The mouse wheel turned over the video or the controls (<paramref name="delta"/> in Windows' units, 120 a notch, up
	/// positive): the volume goes up or down <see cref="Step"/> a notch, 0 to 100 (up never lowers one above 100), and the
	/// indicator shows. It never seeks and never scrolls anything; fine-grained wheels add up until they make a notch. It
	/// does not wake the bars: the indicator is enough, and the picture stays clear.
	/// </summary>
	public Task Wheel(int delta)
	{
		if (!WheelChangesVolume)
		{
			// turned off in Settings: the wheel does nothing over the player (it still never seeks)
			return Task.CompletedTask;
		}

		_wheelRemainder += delta;
		var notches = _wheelRemainder / WheelNotch;
		_wheelRemainder -= notches * WheelNotch;
		ShowVolumeIndicator();
		if (notches == 0)
		{
			return Task.CompletedTask;
		}

		return StepVolume(notches, wakeControls: false);
	}

	// -- subtitles keep clear of the bottom bar ------------------------------------------------------------------------

	/// <summary>
	/// The view measured its bottom bar: this share of the video's height. While the controls show, the backend keeps the
	/// subtitles above it (<c>set_controls</c>, docs/BRIDGE.md §4).
	/// </summary>
	public Task SetBottomBarFraction(double fraction)
	{
		_bottomBarFraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, BridgeCommands.MaxSubtitleInset) : 0;
		return SendInsetIfChanged();
	}

	/// <summary>
	/// Tells the backend FluentFin draws the controls (and where the subtitles go), now and on every change after: the
	/// page calls it once its playback was taken. Before that nothing is sent (no backend may be running yet).
	/// </summary>
	public Task SendControls()
	{
		_announced = true;
		// the remembered text size goes with it: applied at every playback start
		var size = _send(Bridge.Commands.SetSubtitleSize, BridgeCommands.SubtitleSize(SubtitleSize));
		return Task.WhenAll(size, SendInsetIfChanged(force: true));
	}

	private Task SendInsetIfChanged(bool force = false)
	{
		if (!_announced)
		{
			return Task.CompletedTask;
		}

		var inset = BridgeCommands.RoundInset(ControlsVisible ? _bottomBarFraction : 0);
		if (!force && _sentInset == inset)
		{
			return Task.CompletedTask;
		}

		_sentInset = inset;
		return _send(Bridge.Commands.SetControls, BridgeCommands.Controls(ui: true, inset));
	}

	// -- keyboard -------------------------------------------------------------------------------------------------------

	/// <summary>
	/// A key pressed over the player (the page or the controls window). True when it is one of the player's keys and was
	/// handled. The whole map is in docs/SHORTCUTS.md (and <see cref="Shortcuts.ShortcutCatalog"/>): Space/K play-pause,
	/// Left/Right 5 s (Ctrl: 10 s, Shift: 30 s), J/L 10 s, digits and Home seek, Up/Down volume, M mute, C/V subtitles, B audio,
	/// F/F11 full screen, Shift+N/Shift+P next/previous, S skips the segment on offer, Esc.
	/// </summary>
	public bool HandleKey(int virtualKey, bool control = false, bool shift = false, bool alt = false)
	{
		if (ActionFor(virtualKey, control, shift, alt) is not { } action)
		{
			return false;
		}

		NotifyActivity();
		_ = Run(action);
		return true;
	}

	public Task Run(MpvControlsAction action)
	{
		switch (action)
		{
			case MpvControlsAction.TogglePause: return TogglePause();
			case MpvControlsAction.SeekBack: return SeekBy(-SmallSeekSeconds);
			case MpvControlsAction.SeekForward: return SeekBy(SmallSeekSeconds);
			case MpvControlsAction.SeekBackLong: return SeekBy(-LargeSeekSeconds);
			case MpvControlsAction.SeekForwardLong: return SeekBy(LargeSeekSeconds);
			case MpvControlsAction.VolumeUp: return StepVolume(1, wakeControls: true);
			case MpvControlsAction.VolumeDown: return StepVolume(-1, wakeControls: true);
			case MpvControlsAction.ToggleMute: return ToggleMute();
			case MpvControlsAction.ToggleFullScreen: ToggleFullScreen(); return Task.CompletedTask;
			case MpvControlsAction.Escape: Escape?.Invoke(); return Task.CompletedTask;
			case MpvControlsAction.Next: return Next();
			case MpvControlsAction.Previous: return Previous();
			case MpvControlsAction.SkipSegment: return SkipSegment();
			case MpvControlsAction.SeekBackFar: return SeekBy(-SkipForwardSeconds);
			case MpvControlsAction.SeekForwardFar: return SeekBy(SkipForwardSeconds);
			case MpvControlsAction.SeekStart: return SeekToPercent(0);
			case MpvControlsAction.ToggleSubtitles: return ToggleSubtitles();
			case MpvControlsAction.CycleSubtitles: return CycleSubtitles();
			case MpvControlsAction.CycleAudio: return CycleAudio();
			case >= MpvControlsAction.Percent0 and <= MpvControlsAction.Percent9: return SeekToPercent(PercentOf(action));
			default: return Task.CompletedTask;
		}
	}

	/// <summary>Win32 virtual-key codes (the values of Windows.System.VirtualKey).</summary>
	public static class Keys
	{
		public const int Escape = 0x1B, Space = 0x20, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;
		public const int Home = 0x24, D0 = 0x30, D9 = 0x39, Numpad0 = 0x60, Numpad9 = 0x69;
		public const int B = 0x42, C = 0x43, V = 0x56, F11 = 0x7A;
		public const int F = 0x46, J = 0x4A, K = 0x4B, L = 0x4C, M = 0x4D, N = 0x4E, P = 0x50, S = 0x53;
		public const int MediaNext = 0xB0, MediaPrevious = 0xB1, MediaPlayPause = 0xB3;
	}

	/// <summary>The key map (pure): which action a key and its modifiers mean, null for none of the player's.</summary>
	public static MpvControlsAction? ActionFor(int virtualKey, bool control, bool shift, bool alt)
	{
		if (alt)
		{
			return null;
		}

		return virtualKey switch
		{
			Keys.Space or Keys.K or Keys.MediaPlayPause when !control && !shift => MpvControlsAction.TogglePause,
			// the bar's -10 s is J and Ctrl+Left, its +30 s is Shift+Right (Shift+Left the same far, the other way)
			Keys.Left when shift => control ? null : MpvControlsAction.SeekBackFar,
			Keys.Right when shift => control ? null : MpvControlsAction.SeekForwardFar,
			Keys.Left => control ? MpvControlsAction.SeekBackLong : MpvControlsAction.SeekBack,
			Keys.Right => control ? MpvControlsAction.SeekForwardLong : MpvControlsAction.SeekForward,
			Keys.Home when !control && !shift => MpvControlsAction.SeekStart,
			>= Keys.D0 and <= Keys.D9 when !control && !shift => MpvControlsAction.Percent0 + (virtualKey - Keys.D0),
			>= Keys.Numpad0 and <= Keys.Numpad9 when !control && !shift => MpvControlsAction.Percent0 + (virtualKey - Keys.Numpad0),
			Keys.C when !control && !shift => MpvControlsAction.ToggleSubtitles,
			Keys.V when !control && !shift => MpvControlsAction.CycleSubtitles,
			Keys.B when !control && !shift => MpvControlsAction.CycleAudio,
			Keys.J when !control && !shift => MpvControlsAction.SeekBackLong,
			Keys.L when !control && !shift => MpvControlsAction.SeekForwardLong,
			Keys.Up when !control && !shift => MpvControlsAction.VolumeUp,
			Keys.Down when !control && !shift => MpvControlsAction.VolumeDown,
			Keys.M when !control && !shift => MpvControlsAction.ToggleMute,
			Keys.F when !control && !shift => MpvControlsAction.ToggleFullScreen,
			Keys.F11 when !control && !shift => MpvControlsAction.ToggleFullScreen,
			// the backend's own key for its Skip (docs/BRIDGE.md §4 skip_segment); nothing on offer, nothing is sent
			Keys.S when !control && !shift => MpvControlsAction.SkipSegment,
			Keys.N when shift && !control => MpvControlsAction.Next,
			Keys.P when shift && !control => MpvControlsAction.Previous,
			Keys.MediaNext => MpvControlsAction.Next,
			Keys.MediaPrevious => MpvControlsAction.Previous,
			Keys.Escape when !control && !shift => MpvControlsAction.Escape,
			_ => null,
		};
	}

	// -- text -----------------------------------------------------------------------------------------------------------

	/// <summary>"1:02:03", or "4:05" under an hour.</summary>
	public static string FormatTime(TimeSpan value)
	{
		if (value < TimeSpan.Zero)
		{
			value = TimeSpan.Zero;
		}

		return value.TotalHours >= 1
			? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}")
			: string.Create(CultureInfo.InvariantCulture, $"{value.Minutes}:{value.Seconds:00}");
	}

	/// <summary>
	/// The title line: an episode is its series, then "S6E4 · Episode title" (the episode's name left out when it is empty
	/// or only repeats the series); anything else is its own name.
	/// </summary>
	public static (string Title, string Subtitle) FormatTitle(BridgeItem item)
	{
		var name = item.Name?.Trim() ?? "";
		if (item.Type != "Episode" || string.IsNullOrWhiteSpace(item.SeriesName))
		{
			return (name, "");
		}

		var series = item.SeriesName.Trim();
		var number = (item.SeasonNumber, item.EpisodeNumber) switch
		{
			({ } season, { } episode) => string.Create(CultureInfo.InvariantCulture, $"S{season}E{episode}"),
			(null, { } episode) => string.Create(CultureInfo.InvariantCulture, $"E{episode}"),
			_ => "",
		};
		var episodeName = name.Length == 0 || string.Equals(name, series, StringComparison.OrdinalIgnoreCase) ? "" : name;
		var subtitle = (number, episodeName) switch
		{
			("", "") => "",
			("", _) => episodeName,
			(_, "") => number,
			_ => $"{number} · {episodeName}",
		};
		return (series, subtitle);
	}
}
