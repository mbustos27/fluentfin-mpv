using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FluentFin.Core.Mpv;

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
	SkipSegment,
	Next,
	Previous,
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

	public const int SmallSeekSeconds = 5;
	public const int LargeSeekSeconds = 10;
	public const int SkipBackSeconds = 10;
	public const int SkipForwardSeconds = 30;
	public const int VolumeStep = 5;

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
	private int? _pendingVolume;
	private bool _volumeInFlight;

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

	[ObservableProperty]
	public partial bool IsFullScreen { get; set; }

	/// <summary>The top and bottom bars are shown.</summary>
	[ObservableProperty]
	public partial bool ControlsVisible { get; set; } = true;

	/// <summary>The pointer is over the controls (a bar, a menu): they do not hide under it.</summary>
	[ObservableProperty]
	public partial bool PointerOverControls { get; set; }

	/// <summary>The mouse pointer is hidden over the video (the controls hid).</summary>
	[ObservableProperty]
	public partial bool CursorHidden { get; set; }

	/// <summary>The current item is a Jellyfin item (not the self-test's file): what is played is known.</summary>
	public string? ItemId { get; private set; }

	/// <summary>Anything of the controls' is on screen (the window they draw in is shown only then).</summary>
	public bool AnythingVisible => ControlsVisible || VolumeIndicatorVisible;

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
	public string? HeldBy => IsPaused ? "paused" : _menusOpen > 0 ? "a menu is open" : PointerOverControls ? "the pointer is on them" : _scrubbing ? "seeking" : null;

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
		}
	}

	public void OnState(BridgeState state)
	{
		var now = _now();
		var position = CurrentPosition(now);
		IsOpening = state.State == Bridge.States.Opening;
		IsPaused = state.State == Bridge.States.Paused;
		Volume = state.Volume;
		IsMuted = state.Muted;
		if (state.Queue is { ItemIds.Count: > 0 } queue)
		{
			CanPrevious = queue.Index > 0;
			CanNext = queue.Index < queue.ItemIds.Count - 1;
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
		return _send(Bridge.Commands.SetMute, BridgeCommands.Mute(IsMuted));
	}

	/// <summary>
	/// The volume slider moved (0-100). Unmutes, like every player. The slider fires on every pixel: one <c>set_volume</c>
	/// at a time, the latest value sent when the one before has been answered.
	/// </summary>
	public async Task SetVolume(int volume, bool wakeControls = true)
	{
		if (wakeControls)
		{
			NotifyActivity();
		}

		volume = Math.Clamp(volume, 0, 100);
		Volume = volume;
		if (IsMuted && volume > 0)
		{
			IsMuted = false;
			await _send(Bridge.Commands.SetMute, BridgeCommands.Mute(false));
		}

		_pendingVolume = volume;
		if (_volumeInFlight)
		{
			return;
		}

		_volumeInFlight = true;
		try
		{
			while (_pendingVolume is { } next)
			{
				_pendingVolume = null;
				await _send(Bridge.Commands.SetVolume, BridgeCommands.Volume(next));
			}
		}
		finally
		{
			_volumeInFlight = false;
		}
	}

	public void ToggleFullScreen()
	{
		NotifyActivity();
		SetFullScreen?.Invoke(!IsFullScreen);
	}

	// -- mouse wheel: volume -------------------------------------------------------------------------------------------

	/// <summary>How long the volume indicator stays after the last wheel notch.</summary>
	public static readonly TimeSpan VolumeIndicatorFor = TimeSpan.FromSeconds(1.2);

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

	partial void OnVolumeChanged(int value) => OnPropertyChanged(nameof(VolumeText));

	partial void OnIsMutedChanged(bool value) => OnPropertyChanged(nameof(VolumeText));

	/// <summary>
	/// The mouse wheel turned over the video or the controls (<paramref name="delta"/> in Windows' units, 120 a notch, up
	/// positive): the volume goes up or down <see cref="VolumeStep"/> a notch, 0 to 100, and the indicator shows. It never
	/// seeks and never scrolls anything; fine-grained wheels add up until they make a notch. It does not wake the bars:
	/// the indicator is enough, and the picture stays clear.
	/// </summary>
	public Task Wheel(int delta)
	{
		_wheelRemainder += delta;
		var notches = _wheelRemainder / WheelNotch;
		_wheelRemainder -= notches * WheelNotch;
		_volumeIndicatorUntil = _now() + VolumeIndicatorFor;
		VolumeIndicatorVisible = true;
		if (notches == 0)
		{
			return Task.CompletedTask;
		}

		var volume = Math.Clamp(Volume + notches * VolumeStep, 0, 100);
		return volume == Volume && !IsMuted ? Task.CompletedTask : SetVolume(volume, wakeControls: false);
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

	/// <summary>Tells the backend FluentFin draws the controls (and where the subtitles go), once and on every change.</summary>
	public Task SendControls() => SendInsetIfChanged(force: true);

	private Task SendInsetIfChanged(bool force = false)
	{
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
	/// handled. Space/K play-pause, Left/Right 5 s (Ctrl: 10 s), J/L 10 s, Up/Down volume, M mute, F full screen,
	/// S skip a segment, Shift+N/Shift+P next/previous, Esc (docs/research/player-controls.md).
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
			case MpvControlsAction.VolumeUp: return SetVolume(Volume + VolumeStep);
			case MpvControlsAction.VolumeDown: return SetVolume(Volume - VolumeStep);
			case MpvControlsAction.ToggleMute: return ToggleMute();
			case MpvControlsAction.ToggleFullScreen: ToggleFullScreen(); return Task.CompletedTask;
			case MpvControlsAction.Escape: Escape?.Invoke(); return Task.CompletedTask;
			case MpvControlsAction.Next: return Next();
			case MpvControlsAction.Previous: return Previous();
			default: return Task.CompletedTask;
		}
	}

	/// <summary>Win32 virtual-key codes (the values of Windows.System.VirtualKey).</summary>
	public static class Keys
	{
		public const int Escape = 0x1B, Space = 0x20, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;
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
			Keys.Left when !shift => control ? MpvControlsAction.SeekBackLong : MpvControlsAction.SeekBack,
			Keys.Right when !shift => control ? MpvControlsAction.SeekForwardLong : MpvControlsAction.SeekForward,
			Keys.J when !control && !shift => MpvControlsAction.SeekBackLong,
			Keys.L when !control && !shift => MpvControlsAction.SeekForwardLong,
			Keys.Up when !control && !shift => MpvControlsAction.VolumeUp,
			Keys.Down when !control && !shift => MpvControlsAction.VolumeDown,
			Keys.M when !control && !shift => MpvControlsAction.ToggleMute,
			Keys.F when !control && !shift => MpvControlsAction.ToggleFullScreen,
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
