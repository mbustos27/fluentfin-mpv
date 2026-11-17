using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Converters;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace FluentFin.ViewModels;

/// <summary>
/// The player page with the mpv player: the backend does PlaybackInfo, the stream, track defaults and every report to
/// the server (docs/BRIDGE.md §6), so none of FluentFin's own playback code runs here — no GetMediaUrl, no
/// Playing/Progress/Stop reports, no segments, no SyncPlay. This side asks for playback, forwards remote control,
/// handles fullscreen requests and leaves the page when playback ends.
/// </summary>
public partial class VideoPlayerViewModel
{
	private readonly CompositeDisposable _mpvDisposables = [];
	private long _mpvGeneration;
	private bool _mpvSawPlayback;
	private bool _mpvLeaving;
	private string? _mpvLastEndReason;
	private string? _mpvLastError;
	private bool _mpvFocused;

	/// <summary>The items this page asked for (null = any, the self-test): states of other items are not this page's playback.</summary>
	private HashSet<string>? _mpvItems;

	/// <summary>The page shows the mpv video area instead of FluentFin's own player.</summary>
	[ObservableProperty]
	public partial bool IsMpv { get; set; }

	/// <summary>The video window is shown over the video area (hidden while nothing plays, so messages there can be read).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsMpvStartScreenVisible))]
	[NotifyCanExecuteChangedFor(nameof(LeaveMpvCommand))]
	public partial bool IsMpvVideoVisible { get; set; }

	/// <summary>
	/// fluentfin-mpv: the loading screen (and its Cancel / Go back) is there only while the video is not: under mpv's window
	/// it would still hold the keyboard, take Space or Enter as a click and be read out by Narrator.
	/// </summary>
	public bool IsMpvStartScreenVisible => !IsMpvVideoVisible;

	/// <summary>Shown in the bar above the video area.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasMpvMessage))]
	public partial string? MpvMessage { get; set; }

	/// <summary>
	/// fluentfin-mpv: the message bar is open. A bool for its binding: x:Bind does not call a function whose argument is null,
	/// so a bar opened by a function of the text stayed open, and empty, once the text was gone.
	/// </summary>
	public bool HasMpvMessage => !string.IsNullOrEmpty(MpvMessage);

	[ObservableProperty]
	public partial bool MpvMessageIsError { get; set; }

	/// <summary>
	/// A brief message while the video plays on (a non-fatal error once playback runs, such as a track or quality change the
	/// backend could not apply, docs/BRIDGE.md §5): shown above the video area, gone after <see cref="MpvNoticeDuration"/>
	/// or when dismissed (<see cref="DismissMpvNotice"/>). Playback is not touched.
	/// </summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasMpvNotice))]
	public partial string? MpvNotice { get; set; }

	/// <summary>fluentfin-mpv: the notice bar is open (as <see cref="HasMpvMessage"/>: it closes when the notice goes).</summary>
	public bool HasMpvNotice => !string.IsNullOrEmpty(MpvNotice);

	/// <summary>How long <see cref="MpvNotice"/> stays up (a newer notice starts the time again).</summary>
	public TimeSpan MpvNoticeDuration { get; set; } = TimeSpan.FromSeconds(8);

	/// <summary>Waits <see cref="MpvNoticeDuration"/> (tests replace it).</summary>
	internal Func<TimeSpan, Task> MpvNoticeDelay { get; set; } = wait => Task.Delay(wait);

	/// <summary>Counts notices shown and dismissed: a notice hides itself only if it is still the one it showed.</summary>
	private int _mpvNotices;

	/// <summary>
	/// fluentfin-mpv player controls: FluentFin draws the controls over an mpv inside FluentFin (Settings, Player controls:
	/// FluentFin, and libmpv is there), decided when the page opens. Otherwise mpv's own controls in its own window.
	/// </summary>
	[ObservableProperty]
	public partial bool UsesFluentFinControls { get; set; }

	/// <summary>fluentfin-mpv player controls: the controls' state and actions while <see cref="UsesFluentFinControls"/>.</summary>
	[ObservableProperty]
	public partial MpvControlsViewModel? MpvControls { get; set; }

	/// <summary>Whether FluentFin's controls are chosen and can be had (tests replace it). Null whyNot: not chosen, or fine. Null: the real choice.</summary>
	internal Func<(bool Use, string? WhyNot)>? FluentFinControlsChoice { get; set; }

	/// <summary>Why libmpv cannot be used, null when it can: the real probe of the libmpv next to FluentFin (tests probe another file).</summary>
	internal Func<string?> LibmpvUnavailable { get; set; } = FluentFin.Mpv.Libmpv.Unavailable;

	/// <summary>The same answer the video surface gives the backend at connect (<see cref="FluentFin.Mpv.MpvVideoSurfaceChoice"/>).</summary>
	private (bool Use, string? WhyNot) ChooseControls()
	{
		if (FluentFinControlsChoice is { } choice)
		{
			return choice();
		}

		var use = FluentFin.Mpv.MpvVideoSurfaceChoice.UsesFluentFinControls(settings, LibmpvUnavailable, out var whyNot);
		return (use, whyNot);
	}

	/// <summary>This page's play was taken by the backend: item, track and position events are its own from here.</summary>
	private bool _mpvPlayAccepted;

	/// <summary>Shown in the video area while the video window is hidden.</summary>
	[ObservableProperty]
	public partial string MpvStatusText { get; set; } = "";

	/// <summary>
	/// fluentfin-mpv: the start is under way (queue, backend, connect, play): the page shows its loading screen with
	/// <see cref="MpvStatusText"/> as the step and a Cancel (docs/research/ux-review.md V10). False once the backend took the
	/// play (mpv's window covers the area from then on), when the start fails, and when the page is left.
	/// </summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(MpvLeaveText))]
	public partial bool IsMpvStarting { get; set; }

	/// <summary>What is being started, as the loading screen names it (empty when not known, e.g. a remote play).</summary>
	[ObservableProperty]
	public partial string MpvStartingTitle { get; set; } = "";

	/// <summary>Under the title: "S1:E4 - Episode" for an episode, the year for a movie.</summary>
	[ObservableProperty]
	public partial string MpvStartingSubtitle { get; set; } = "";

	/// <summary>The item the loading screen takes its backdrop from (null: none).</summary>
	[ObservableProperty]
	public partial BaseItemDto? MpvStartingItem { get; set; }

	/// <summary>The loading screen's button: Cancel while starting, Go back once it has stopped (an error, a crash).</summary>
	public string MpvLeaveText => IsMpvStarting ? "Cancel" : "Go back";

	/// <summary>Set by the page: full screen on/off the way the other players do it (presenter, title bar; the pane is PlayerChrome's).</summary>
	public Action<bool>? SetFullScreen { get; set; }

	/// <summary>Set by the page: whether the window is full screen now (its presenter).</summary>
	public Func<bool>? IsFullScreen { get; set; }

	/// <summary>Set by the page: hands the keyboard to mpv once it plays.</summary>
	public Action? FocusVideo { get; set; }

	/// <summary>
	/// Set by the page: goes back with the navigation service of the frame the page is on (the self-test's page sits on the
	/// window's root frame, not the shell's). False when there is nothing to go back to. Unset: the shell's service.
	/// </summary>
	public Func<bool>? GoBack { get; set; }

	internal long MpvGeneration => _mpvGeneration;

	/// <summary>Takes over OnNavigatedTo when the mpv player is selected (or for the self-test). False = not mpv.</summary>
	private async Task<bool> OnNavigatedToMpv(object parameter)
	{
		if (parameter is not MpvSelfTestRequest && MediaPlayerType != MediaPlayerType.Mpv)
		{
			return false;
		}

		MediaPlayerType = MediaPlayerType.Mpv;
		IsMpv = true;
		ShowStarting(parameter);
		// until this page's play is known, every active state belongs to the page before (null = any: the self-test only)
		_mpvItems = parameter is MpvSelfTestRequest ? null : new HashSet<string>(StringComparer.Ordinal);
		var generation = _mpvGeneration = mpvPlayback.Begin("a player page opened");
		// fluentfin-mpv player controls: the same answer the video surface gives the backend at connect
		var (useFluentFinControls, whyNot) = ChooseControls();
		UsesFluentFinControls = useFluentFinControls;
		MpvControls = useFluentFinControls ? CreateMpvControls(generation) : null;
		if (whyNot is not null)
		{
			ShowMpvNotice($"FluentFin's player controls are not available ({whyNot}); mpv's controls are used.");
		}
		logger.LogInformation("Player page opened with the mpv player (generation {Generation}): {Request}", generation, DescribeMpvParameter(parameter));
		SubscribeMpv(generation);
		var opened = Stopwatch.StartNew();
		MpvStartStep? step = null;
		void OnStep(MpvStartStep next)
		{
			step = next;
			if (!_mpvLeaving)
			{
				MpvStatusText = StepText(next);
			}
		}

		try
		{
			switch (parameter)
			{
				case MpvSelfTestRequest selfTest:
					if (await mpvPlayback.PlayFileAsync(generation, selfTest, IsFullScreen?.Invoke() ?? false, onStep: OnStep, controls: ControlsCommand()))
					{
						OnMpvPlayAccepted();
					}
					return true;

				case PlayQueueUpdate:
					ShowMpvMessage("SyncPlay is not available with the MPV player.", isError: false);
					return true;
			}

			BridgePlayRequest? request = null;
			if (parameter is BridgePlayRequest remote)
			{
				request = remote;
			}
			else if (parameter is BaseItemDto { Id: not null } or PlaybackRequest { Dto.Id: not null })
			{
				OnStep(MpvStartStep.LoadingQueue);
				request = parameter is PlaybackRequest picked
					? await LoadMpvQueue(generation, picked.Dto, picked.Selection, picked.FromBeginning)
					: await LoadMpvQueue(generation, (BaseItemDto)parameter, null);
			}

			if (request is null)
			{
				ShowMpvMessage("Nothing to play.", isError: false);
				return true;
			}

			if (MpvIdentity.FromSession(settings) is not { } identity)
			{
				ShowMpvMessage("Not logged in to a server.", isError: true);
				return true;
			}

			_mpvItems = new HashSet<string>(request.ItemIds.Select(NormalizeId), StringComparer.Ordinal);
			if (await mpvPlayback.PlayAsync(generation, identity, request, IsFullScreen?.Invoke() ?? false, onStep: OnStep, controls: ControlsCommand()))
			{
				logger.LogInformation("Sent play to the mpv backend {Ms} ms after the player page opened (generation {Generation})", opened.ElapsedMilliseconds, generation);
				OnMpvPlayAccepted();
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// as text, redacted: a failure's message may quote a request (docs/BRIDGE.md §1: no token in any log)
			logger.LogError("MPV playback could not start (step: {Step}, generation {Generation}): {Error}", step?.ToString() ?? "play", generation, BridgeRedaction.Redact(ex.ToString()));
			ShowMpvMessage(StartFailureMessage(step, ex), isError: true);
		}

		return true;
	}

	/// <summary>
	/// fluentfin-mpv: the queue for an item page's play button, from one request to the server (<see cref="MpvQueue"/>), not
	/// FluentFin's playlist, which asks for each season in turn. Playlist stays empty: the mpv page shows none of it (mpv's
	/// OSC is the player UI).
	/// </summary>
	private async Task<BridgePlayRequest?> LoadMpvQueue(long generation, BaseItemDto item, PlaybackSelection? selection, bool fromBeginning = false)
	{
		var watch = Stopwatch.StartNew();
		var request = await MpvQueue.BuildAsync(JellyfinClient, item, selection, fromBeginning);
		logger.LogInformation("Queue for {Type} {ItemId} (generation {Generation}): {Count} item(s) from index {Index}, loaded in {Ms} ms",
			item.Type, item.Id, generation, request?.ItemIds.Count ?? 0, request?.StartIndex, watch.ElapsedMilliseconds);
		return request;
	}

	/// <summary>Shown in the video area while the page waits (plain words: docs/research/ux-review.md V10).</summary>
	internal static string StepText(MpvStartStep step) => step switch
	{
		MpvStartStep.LoadingQueue => "Loading the queue…",
		MpvStartStep.StartingBackend => "Starting the player…",
		MpvStartStep.Connecting => "Connecting to the server…",
		_ => "Starting playback…",
	};

	/// <summary>fluentfin-mpv: the loading screen names what the page was opened for, from what it already has (no request).</summary>
	private void ShowStarting(object? parameter)
	{
		var item = parameter switch
		{
			PlaybackRequest picked => picked.Dto,
			BaseItemDto dto => dto,
			_ => null,
		};

		MpvStartingItem = item;
		(MpvStartingTitle, MpvStartingSubtitle) = parameter switch
		{
			MpvSelfTestRequest selfTest => (selfTest.Title ?? "", ""),
			_ when item is null => ("", ""),
			_ when item.Type is BaseItemDto_Type.Season => (item.SeriesName ?? "", item.Name ?? ""),
			_ when item.Type is BaseItemDto_Type.Series => (item.Name ?? "", ""),
			_ => (item.GetCardTitle(), item.GetCardSubtitle()),
		};
		IsMpvStarting = true;
	}

	/// <summary>
	/// fluentfin-mpv: the loading screen's Cancel (Go back once the start has stopped): leaves the page the way Back does, so
	/// the page's leave stops whatever the start got to (a start whose page is gone goes no further: MpvPlaybackService).
	/// With nothing to go back to, the start is stopped here and the page says so.
	/// </summary>
	[CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(IsMpvStartScreenVisible))]
	private async Task LeaveMpv()
	{
		logger.LogInformation("Player page (generation {Generation}): {Button} pressed", _mpvGeneration, IsMpvStarting ? "Cancel" : "Go back");
		if (GoBack?.Invoke() ?? navigationService.GoBack())
		{
			return;
		}

		await OnNavigatedFromMpv("the start was cancelled with nothing to go back to");
		IsMpvStarting = false;
		MpvStatusText = "Playback cancelled";
	}

	/// <summary>What the page shows when a start fails: which step, then why (never a token: redacted).</summary>
	private static string StartFailureMessage(MpvStartStep? step, Exception ex)
	{
		if (MpvNotFoundMessage.For(ex) is { } plain)
		{
			return plain; // fluentfin-mpv (release 1.0): no mpv for mpv's controls, said plainly
		}

		var why = BridgeRedaction.Redact(ex.Message);
		return step switch
		{
			MpvStartStep.LoadingQueue => $"Loading the queue failed: {why}",
			MpvStartStep.StartingBackend => $"Starting mpv failed: {why}",
			MpvStartStep.Connecting => $"Connecting failed: {why}",
			_ => ex is BridgeException ? why : $"MPV playback could not start: {why}",
		};
	}

	/// <summary>For the log: what the page was opened for, by type and id (no names, no URLs).</summary>
	private static string DescribeMpvParameter(object? parameter) => parameter switch
	{
		MpvSelfTestRequest => "the self-test",
		PlayQueueUpdate => "a SyncPlay queue (not played with mpv)",
		BridgePlayRequest remote => $"a remote play of {remote.ItemIds.Count} item(s) from index {remote.StartIndex}",
		PlaybackRequest picked => $"{picked.Dto.Type} {picked.Dto.Id:N}{(picked.Selection is null ? "" : " with streams picked on its page")}{(picked.FromBeginning ? " from the beginning" : "")}",
		BaseItemDto item => $"{item.Type} {item.Id:N}",
		null => "nothing",
		_ => parameter.GetType().Name,
	};

	private void SubscribeMpv(long generation)
	{
		var backend = mpvPlayback.Backend;

		backend.Events
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(e => OnMpvEvent(generation, e))
			.DisposeWith(_mpvDisposables);

		backend.StatusChanged
			.Skip(1)
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(status => OnMpvStatus(generation, status))
			.DisposeWith(_mpvDisposables);

		// FluentFin owns the session websocket, remote control goes to the backend
		webSocketMessages
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(async message => await OnMpvRemoteControl(generation, message))
			.DisposeWith(_mpvDisposables);
	}

	private void OnMpvPlayAccepted()
	{
		_mpvPlayAccepted = true;

		// fluentfin-mpv player controls: from here the controls tell the backend where their bar is (set_controls went
		// before the play already, so mpv's own controls never showed)
		_ = MpvControls?.SendControls();

		// shown before mpv creates its window, so mpv's window gets the final size from the start
		IsMpvVideoVisible = true;
		IsMpvStarting = false;
		MpvStatusText = "";
	}

	internal void OnMpvEvent(long generation, BridgeEvent e)
	{
		if (_mpvLeaving || !mpvPlayback.IsCurrent(generation))
		{
			return;
		}

		try
		{
			// fluentfin-mpv player controls: what they show (the state is passed on by OnMpvState, for this page's playback only);
			// segments and next: the Skip button and the Next button's state (docs/BRIDGE.md §5)
			if (MpvControls is { } controls && _mpvPlayAccepted
				&& e.Name is Bridge.Events.Position or Bridge.Events.Item or Bridge.Events.Tracks or Bridge.Events.Segments or Bridge.Events.Next)
			{
				controls.OnEvent(e);
			}

			switch (e.Name)
			{
				case Bridge.Events.State when e.DataAs<BridgeState>() is { } state:
					OnMpvState(state);
					break;

				case Bridge.Events.Ended when e.DataAs<BridgeEnded>() is { } ended:
					_mpvLastEndReason = ended.Reason;
					break;

				case Bridge.Events.Error when e.DataAs<BridgeError>() is { } error:
					_mpvLastError = BridgeRedaction.Redact(error.Message);
					if (error.Fatal || !_mpvSawPlayback)
					{
						ShowMpvMessage($"MPV: {_mpvLastError}", isError: true);
					}
					else
					{
						// the item plays on (a track or quality change that could not be applied): said briefly, nothing stops
						ShowMpvNotice(string.IsNullOrEmpty(_mpvLastError) ? "MPV: something could not be done; playback goes on." : $"MPV: {_mpvLastError}");
					}
					break;

				case Bridge.Events.FullscreenRequest when e.DataAs<BridgeFullscreenRequest>() is { } request:
					_ = OnMpvFullscreenRequest(generation, request.Fullscreen);
					break;
			}
		}
		catch (System.Text.Json.JsonException ex)
		{
			logger.LogWarning("Unreadable {Event} event from the mpv backend: {Message}", e.Name, ex.Message);
		}
	}

	private void OnMpvState(BridgeState state)
	{
		if (state.IsActive)
		{
			if (_mpvItems is not null && (state.ItemId is null || !_mpvItems.Contains(NormalizeId(state.ItemId))))
			{
				// still the playback of the page before this one, until the backend has taken this page's play
				return;
			}

			_mpvSawPlayback = true;
			IsMpvVideoVisible = true;
			IsMpvStarting = false;
			MpvControls?.OnState(state);
			MpvStatusText = "";

			// once the item has started: a pause queued while opening goes from opening straight to paused
			if (!_mpvFocused && state.State is Bridge.States.Playing or Bridge.States.Paused)
			{
				_mpvFocused = true;
				FocusVideo?.Invoke();
			}
			return;
		}

		if (state.State == Bridge.States.Idle && _mpvSawPlayback)
		{
			_mpvSawPlayback = false;
			OnMpvPlaybackFinished();
		}
	}

	/// <summary>The last item ended or playback was stopped (mpv's stop key, a remote Stop): back to where play was pressed.</summary>
	private void OnMpvPlaybackFinished()
	{
		IsMpvVideoVisible = false;

		if (_mpvLastEndReason == "error")
		{
			ShowMpvMessage(string.IsNullOrEmpty(_mpvLastError) ? "Playback failed." : $"Playback failed: {_mpvLastError}", isError: true);
			return;
		}

		if (!(GoBack?.Invoke() ?? navigationService.GoBack()))
		{
			MpvStatusText = "Playback ended";
		}
	}

	internal void OnMpvStatus(long generation, MpvBackendStatus status)
	{
		if (_mpvLeaving || !mpvPlayback.IsCurrent(generation))
		{
			return;
		}

		if (status.State is MpvBackendState.Crashed)
		{
			_mpvSawPlayback = false;
			IsMpvVideoVisible = false;
			ShowMpvMessage($"{status.LastError}. It starts again with the next playback.", isError: true);
		}
	}

	private async Task OnMpvFullscreenRequest(long generation, bool fullScreen)
	{
		SetFullScreen?.Invoke(fullScreen);
		await SendMpv(generation, Bridge.Commands.SetFullscreen, BridgeCommands.Fullscreen(fullScreen));
	}

	/// <summary>The page left or entered full screen on its own (Escape): tell the backend for its OSC button.</summary>
	public Task OnFullScreenChanged(bool fullScreen)
	{
		if (!IsMpv)
		{
			return Task.CompletedTask;
		}

		if (MpvControls is { } controls)
		{
			controls.IsFullScreen = fullScreen;
		}

		return SendMpv(_mpvGeneration, Bridge.Commands.SetFullscreen, BridgeCommands.Fullscreen(fullScreen));
	}

	/// <summary>
	/// The page is being left (called before <see cref="OnNavigatedFrom"/>, while this page is still the current one): a
	/// full-screen window is restored and the backend told, so neither carries full screen into what comes next.
	/// </summary>
	public Task LeaveFullScreen()
	{
		if (!IsMpv || IsFullScreen?.Invoke() != true)
		{
			return Task.CompletedTask;
		}

		// tools\ui-harness\selftest-gate.ps1 reads this line
		logger.LogInformation("Leaving the player page in full screen: restoring the window, sending set_fullscreen false to the mpv backend");
		SetFullScreen?.Invoke(false);
		if (MpvControls is { } controls)
		{
			controls.IsFullScreen = false;
		}

		return SendMpv(_mpvGeneration, Bridge.Commands.SetFullscreen, BridgeCommands.Fullscreen(false));
	}

	internal async Task OnMpvRemoteControl(long generation, IOutboundWebSocketMessage message)
	{
		BridgeCommand? command;
		try
		{
			command = _mpvLeaving ? null : MpvRemoteControl.Translate(message, mpvPlayback.Backend.LastState);
		}
		catch (Exception ex)
		{
			// called from an async void subscriber: nothing may escape
			logger.LogWarning(ex, "Unreadable remote control message {Message}", message.GetType().Name);
			return;
		}

		if (command is null)
		{
			return;
		}

		logger.LogInformation("Remote control: {Command}", command.Name);
		await SendMpv(generation, command.Name, command.Args);
	}

	/// <summary>Never throws: its callers are event handlers and async void subscribers.</summary>
	private async Task SendMpv(long generation, string command, System.Text.Json.Nodes.JsonObject? args)
	{
		try
		{
			await mpvPlayback.SendIfCurrentAsync(generation, command, args);
		}
		catch (BridgeException ex)
		{
			logger.LogWarning("'{Command}' was not taken by the mpv backend: {Message}", command, ex.Message);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "'{Command}' could not be sent to the mpv backend", command);
		}
	}

	/// <summary>
	/// Takes over OnNavigatedFrom for the mpv player: stop (the backend reports it). Once: a page that was already left (its
	/// removal without a navigation, <see cref="OnPageRemoved"/>, or a sign-out, which stopped its playback first) sends
	/// nothing more. <paramref name="reason"/> goes to the log with the generation. False = not mpv.
	/// </summary>
	private async Task<bool> OnNavigatedFromMpv(string reason = "the player page was left by a navigation")
	{
		if (!IsMpv)
		{
			return false;
		}

		if (_mpvLeaving)
		{
			logger.LogInformation("Player page (generation {Generation}): {Reason}, but it was already left; nothing more to stop", _mpvGeneration, reason);
			return true;
		}

		logger.LogInformation("Player page (generation {Generation}): {Reason}", _mpvGeneration, reason);
		_mpvLeaving = true;
		_mpvDisposables.Dispose();
		IsMpvVideoVisible = false;
		IsMpvStarting = false;
		await mpvPlayback.StopAsync(_mpvGeneration, reason);
		return true;
	}

	/// <summary>
	/// fluentfin-mpv: the page was taken out of the window (its Unloaded, looked at again once layout settled). Only its own
	/// frame's navigations tell it it was left (<paramref name="navigatedFrom"/>); a navigation of the window's root frame
	/// (Log out, Switch user) takes the shell away with every page on it and tells only the shell. Removed without being
	/// navigated from and not back in the window (<paramref name="loadedAgain"/>: a move within the window unloads and loads
	/// again) is a leave like any other: the playback stops, once (docs/BRIDGE.md §6). True when it was taken as a leave.
	/// </summary>
	public async Task<bool> OnPageRemoved(bool navigatedFrom, bool loadedAgain)
	{
		if (navigatedFrom || loadedAgain || !IsMpv || _mpvLeaving)
		{
			return false;
		}

		logger.LogInformation("The player page (generation {Generation}) was removed from the window without being navigated from; leaving it (its mpv playback stops)", _mpvGeneration);
		homeCache.MarkStale(); // fluentfin-mpv: as any leave (OnNavigatedFrom)
		await LeaveFullScreen();
		await OnNavigatedFromMpv("the player page was removed from the window without a navigation");
		return true;
	}

	/// <summary>
	/// fluentfin-mpv player controls: the controls for this page's playback. They send through the page's generation (a
	/// newer page's backend is never driven by an old page's controls); full screen, Escape and back are the page's.
	/// </summary>
	private MpvControlsViewModel CreateMpvControls(long generation) => new((command, args) => SendMpv(generation, command, args))
	{
		// Settings > Playback: the same wheel, step and Skip button as FluentFin's own players
		WheelChangesVolume = settings.MouseWheelVolume,
		Step = settings.VolumeStep,
		SubtitleSize = settings.SubtitleSize,
		SubtitleSizeChanged = size => settings.SubtitleSize = size,
		ShowSkipPrompt = settings.ShowSkipPrompt,
		NameItem = NameMpvItem,
		IsFullScreen = IsFullScreen?.Invoke() ?? false,
		SetFullScreen = fullScreen =>
		{
			SetFullScreen?.Invoke(fullScreen);
			_ = OnFullScreenChanged(fullScreen);
		},
		GoBack = () =>
		{
			if (!(GoBack?.Invoke() ?? navigationService.GoBack()))
			{
				logger.LogInformation("Back from the player controls: nothing to go back to");
			}
		},
	};

	/// <summary>
	/// fluentfin-mpv player controls: an item's title for the Next button's tooltip, from the server (the <c>next</c> event
	/// carries ids only). Null when it cannot be had; never throws.
	/// </summary>
	private async Task<string?> NameMpvItem(string itemId)
	{
		if (!Guid.TryParse(itemId, out var id))
		{
			return null;
		}

		try
		{
			return await JellyfinClient.GetItem(id) is { } dto
				? MpvControlsViewModel.NextTitle(new BridgeItem(itemId, dto.Name, dto.Type?.ToString(), dto.SeriesName, dto.ParentIndexNumber, dto.IndexNumber))
				: null;
		}
		catch (Exception ex)
		{
			logger.LogInformation("The next item {ItemId} could not be named for the Next button: {Error}", itemId, BridgeRedaction.Redact(ex.Message));
			return null;
		}
	}

	/// <summary><c>set_controls</c> for the start (before <c>play</c>): FluentFin's controls, nothing under them yet. Null: mpv's.</summary>
	private System.Text.Json.Nodes.JsonObject? ControlsCommand() => MpvControls is null ? null : BridgeCommands.Controls(ui: true, 0);

	/// <summary>fluentfin-mpv player controls: the in-process mpv ended by itself (the page heard it from the video).</summary>
	public void OnMpvCoreLost()
	{
		if (_mpvLeaving)
		{
			return;
		}

		_mpvSawPlayback = false;
		IsMpvVideoVisible = false;
		ShowMpvMessage("mpv ended. It starts again with the next playback.", isError: true);
	}

	private static string NormalizeId(string id) => id.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

	private void ShowMpvMessage(string message, bool isError)
	{
		IsMpvStarting = false;
		MpvStatusText = "";
		MpvMessageIsError = isError;
		MpvMessage = message;

		// the message says what happened since: a notice about the playback before it is stale
		DismissMpvNotice();
	}

	/// <summary>Shows <see cref="MpvNotice"/> and hides it again after <see cref="MpvNoticeDuration"/> unless a newer one took its place.</summary>
	private void ShowMpvNotice(string notice)
	{
		var shown = Interlocked.Increment(ref _mpvNotices);
		MpvNotice = notice;
		_ = HideMpvNoticeLater(shown);
	}

	private async Task HideMpvNoticeLater(int shown)
	{
		// back on the page's thread (the events arrive on the UI thread)
		await MpvNoticeDelay(MpvNoticeDuration);
		if (Volatile.Read(ref _mpvNotices) == shown)
		{
			MpvNotice = null;
		}
	}

	/// <summary>The message bar's close button: gone now, and the next message opens the bar again, even with the same text.</summary>
	public void DismissMpvMessage() => MpvMessage = null;

	/// <summary>The notice's close button (or a message that replaces it): gone now, and the next notice shows even with the same text.</summary>
	public void DismissMpvNotice()
	{
		Interlocked.Increment(ref _mpvNotices);
		MpvNotice = null;
	}
}
