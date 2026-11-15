"""Mutation checks for the UI fixes, in a scratch copy of ui/ (the repo is never edited).

Each mutation removes or weakens one fix by editing one or two lines (checked by an expected substring), builds the
scratch copy, runs the tests that should pin it, and restores the file. KILLED = the tests failed (the fix is pinned).
The line actually edited is printed, so a drifted anchor shows.

Usage: python mutate_ui.py --work <scratch dir> [name-substring ...]
  --work   where the copy of ui/ lives (kept between runs for incremental builds); never inside the repo
The repo is the folder two levels above this script.
"""
import argparse, os, shutil, subprocess, sys, time

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

HOST = "FluentFin.Core/Mpv/MpvBackendHost.cs"
CLIENT = "FluentFin.Core/Mpv/BridgeClient.cs"
SERVICE = "FluentFin.Core/Mpv/MpvPlaybackService.cs"
CLOSE = "FluentFin.Core/Mpv/CloseSequence.cs"
VM = "FluentFin/ViewModels/VideoPlayerViewModel.Mpv.cs"
JOBPROC = "FluentFin/Mpv/JobProcess.cs"
TITLEBAR = "FluentFin/ViewModels/TitleBarViewModel.cs"
IMPV = "FluentFin.Core/Mpv/IMpvBackend.cs"
LAUNCHER = "FluentFin/Mpv/WindowsMpvBackendLauncher.cs"

# name, [(file, line, must contain, replacement line)], test project, filter
MUTATIONS = [
    ("1 host.Kill guard", [(HOST, 536, "KillProcess(session);", "\t\tsession.Process.Kill();")], "app", "FullyQualifiedName~Kill_ThatThrowsAggregateException"),
    ("1 shutdown-timeout kill guard", [(HOST, 715, "KillProcess(session);", "\t\t\tsession.Process.Kill();")], "app", "FullyQualifiedName~Shutdown_WhenTheBackendIgnoresIt"),
    ("1 Dispose kill guard", [(HOST, 996, "KillProcess(session);", "\t\t\tsession.Process.Kill();")], "app", "FullyQualifiedName~Dispose_WhenTheKillThrows"),
    ("1 Unresponsive handler guard", [("FluentFin/Mpv/MpvVideoHost.cs", 410, "foreach (var handler in handlers", "\t\thandlers(reason); foreach (var handler in Array.Empty<Action<string>>())")], "app", "FullyQualifiedName~Unresponsive_AHandlerThatThrows"),
    ("2 no JOB_LIST", [(JOBPROC, 233, "startedWithJobList = useJobList &&", "\t\t\t\t\t\tstartedWithJobList = false;")], "app", "FullyQualifiedName~TheProcess_IsInTheJob_BeforeItsFirstInstruction"),
    ("2 no JOB_LIST, no assign while suspended", [(JOBPROC, 233, "startedWithJobList = useJobList &&", "\t\t\t\t\t\tstartedWithJobList = false;"), (JOBPROC, 143, "job.Assign(process);", "\t\t\t\t\t;")], "app", "FullyQualifiedName~TheProcess_IsInTheJob_BeforeItsFirstInstruction"),
    ("3 back stack entry kept", [("FluentFin/Services/NavigationService.cs", 148, "backStack.RemoveAt", "\t\t;")], "app", "FullyQualifiedName~NavigationServiceTests"),
    ("4 GlobalCommands mpv filter", [("FluentFin.Core/GlobalCommands.cs", 45, "selection = Mpv.MpvTrackPick.ForPlay", "\t\t\t\t;")], "core", "FullyQualifiedName~ItemPageTrackPickTests"),
    ("4 movie preselect mark", [("FluentFin.Core/ViewModels/MovieViewModel.cs", 105, "_streamsPreselected = true;", "\t\t;")], "core", "FullyQualifiedName~ItemPageTrackPickTests"),
    ("4 episode preselect mark", [("FluentFin.Core/ViewModels/EpisodeViewModel.cs", 109, "_streamsPreselected = true;", "\t\t;")], "core", "FullyQualifiedName~ItemPageTrackPickTests"),
    ("4 ForPlay ignores userPicked", [("FluentFin.Core/Mpv/BridgeCommands.cs", 68, "if (!userPicked || selection is null)", "\t\tif (selection is null)")], "core", "FullyQualifiedName~ItemPageTrackPickTests"),
    ("5 no fullscreen sync at begin", [(SERVICE, 114, "await SyncFullScreenAsync", "\t\t;")], "app", "FullyQualifiedName~APageThatBegins"),
    ("5 leaving does not tell backend", [(VM, 319, "return SendMpv(_mpvGeneration, Bridge.Commands.SetFullscreen", "\t\treturn Task.CompletedTask;")], "app", "FullyQualifiedName~LeavingInFullScreen"),
    ("6 _mpvItems null during first await", [(VM, 80, "_mpvItems = parameter is MpvSelfTestRequest", "\t\t;")], "app", "FullyQualifiedName~ForeignPlayback_WhileThisPageIsStillLoadingItsItem"),
    ("survivor: _mpvItems filter", [(VM, 236, "if (_mpvItems is not null &&", "\t\t\tif (false)")], "app", "FullyQualifiedName~ForeignPlayback"),
    ("survivor: OnMpvEvent generation", [(VM, 196, "if (_mpvLeaving || !mpvPlayback.IsCurrent(generation))", "\t\tif (_mpvLeaving)")], "app", "FullyQualifiedName~EventsForAPageThatWasReplaced"),
    ("survivor: OnMpvStatus generation", [(VM, 281, "if (_mpvLeaving || !mpvPlayback.IsCurrent(generation))", "\t\tif (_mpvLeaving)")], "app", "FullyQualifiedName~ACrashReportedToAPageThatWasReplaced"),
    ("survivor: error detail redaction", [(CLIENT, 282, "BridgeRedaction.Redact(detail)", "\t\tpending.Response.TrySetException(new BridgeCommandException(pending.Command, code, detail));")], "app", "FullyQualifiedName~ErrorResponse_DetailsAreRedacted"),
    ("survivor: malformed line redaction", [(CLIENT, 327, "var redacted = BridgeRedaction.Redact(line);", "\t\tvar redacted = line;")], "app", "FullyQualifiedName~MalformedLines_AreLoggedWithoutTokens"),
    ("survivor: error event log redaction", [(HOST, 795, "BridgeRedaction.Redact(error.Message));", "\t\t\t\tlogger.LogWarning(\"mpv backend error {Code} (fatal: {Fatal}): {Message}\", error.Code, error.Fatal, error.Message);")], "app", "FullyQualifiedName~ErrorEvents_AreLoggedAndShownWithoutTokens"),
    ("survivor: error event status redaction", [(HOST, 798, "LastError = $\"{error.Code}: {BridgeRedaction.Redact(error.Message)}\"", "\t\t\t\t\tSetStatus(StatusOf(session) with { LastError = $\"{error.Code}: {error.Message}\" });")], "app", "FullyQualifiedName~ErrorEvents_AreLoggedAndShownWithoutTokens"),
    ("7 unbounded lifecycle wait", [(HOST, 614, "if (!await _lifecycle.WaitAsync(LifecycleTimeout))", "\t\tawait _lifecycle.WaitAsync(); if (false)")], "app", "FullyQualifiedName~Shutdown_DoesNotWaitFor"),
    ("7 unbounded write", [(CLIENT, 118, "await write.WaitAsync(Left(), cancellationToken);", "\t\t\t\tawait write.WaitAsync(cancellationToken);")], "app", "FullyQualifiedName~AWriteTheBackendNeverTakes"),
    ("7 unbounded write-lock wait", [(CLIENT, 93, "if (!await _writeLock.WaitAsync(Left(), cancellationToken).ConfigureAwait(false))", "\t\t\tawait _writeLock.WaitAsync(cancellationToken); if (false)")], "app", "FullyQualifiedName~ARequestQueuedBehindAStuckWrite|FullyQualifiedName~Shutdown_WhenTheBackendStopsReadingItsInput"),
    ("7 stalled write does not kill", [(HOST, 348, "KillSession(session, \"it stopped reading its input\");", "\t\t\t\t;")], "app", "FullyQualifiedName~AWriteTheBackendNeverTakes_ClosesTheConnection_AndKillsTheBackend"),
    ("8 no interruption emitted", [(HOST, 926, "_interrupted.OnNext(interrupted);", "\t\t\t\t;")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("8 stale state position", [(HOST, 949, "var position = session.LatestPosition", "\t\tvar position = session.LastState?.PositionTicks;")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("8 reports after shutdown", [(HOST, 875, "if (session.ExitIsTheStops())", "\t\t\tif (false)")], "app", "FullyQualifiedName~NormalShutdown_WhilePlaying|FullyQualifiedName~BackendExitsAfterARestartsShutdown|FullyQualifiedName~Shutdown_WithAnUnreportedSession"),
    ("8 reports any state", [(HOST, 826, "if (!state.IsActive || state.ItemId is not", "\t\tif (state.ItemId is not { Length: > 0 } itemId || state.PlaySessionId is not { Length: > 0 } playSessionId)")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("8 service does not call Stop", [(SERVICE, 194, "await _jellyfinClient.Stop(new PlaybackStopInfo", "\t\t\tawait Task.Yield(); _ = (new PlaybackStopInfo")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("9 close: 5 s shutdown budget", [(HOST, 42, "public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);", "\tpublic TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);")], "app", "FullyQualifiedName~Shutdown_WaitsForASlowFinalReport"),
    ("focus once started (paused)", [(VM, 247, "state.State is Bridge.States.Playing or Bridge.States.Paused", "\t\t\tif (!_mpvFocused && state.State == Bridge.States.Playing)")], "app", "FullyQualifiedName~TheKeyboardGoesToMpv"),
    ("audit: remote control guard", [(VM, 329, "catch (Exception ex)", "\t\tcatch (Exception ex) when (ex is null)")], "app", "FullyQualifiedName~ARemoteControlMessageThatCannotBeRead"),
    ("X JobProcess no terminate after failure while suspended", [(JOBPROC, 156, "TerminateProcess(process, 1);", "\t\t\t\t;")], "app", "FullyQualifiedName~JobProcessTests|FullyQualifiedName~JobObjectTests"),
    ("X Kill fallback no TerminateOnly", [("FluentFin/Mpv/WindowsMpvBackendLauncher.cs", 103, "_process.TerminateOnly(KilledExitCode);", "\t\t\t\t;")], "app", "FullyQualifiedName~JobProcessTests|FullyQualifiedName~JobObjectTests|FullyQualifiedName~MpvBackendHost"),
    ("X movie flag reset removed", [("FluentFin.Core/ViewModels/MovieViewModel.cs", 70, "_streamsPreselected = _streamsPicked = false;", "\t\t;")], "core", "FullyQualifiedName~ItemPageTrackPickTests"),
    ("X episode flag reset removed", [("FluentFin.Core/ViewModels/EpisodeViewModel.cs", 69, "_streamsPreselected = _streamsPicked = false;", "		;")], "core", "FullyQualifiedName~ItemPageTrackPickTests"),
    ("X KillSession ignores ShuttingDown", [(HOST, 524, "if (!session.TryMarkKilled(reason))", "\t\t\tif (!session.TryMarkKilled(reason) && false)")], "app", "FullyQualifiedName~Mpv"),

    # round 2 (2026-11-14): crash report without a position, the kill/close race, bounded close, the shutdown deadline
    ("R2-1 opening: the snapshot's position is used", [(HOST, 949, "var position = session.LatestPosition", "\t\tvar position = session.LatestPosition is { } mark && string.Equals(mark.PlaySessionId, open.PlaySessionId, StringComparison.Ordinal) ? mark.Ticks : session.LastState?.PositionTicks;")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("R2-1 service never sends Failed", [(SERVICE, 200, "Failed = playback.PositionKnown ? null : true,", "\t\t\t\tFailed = null,")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("R2-1 service sends 0 for no position", [(SERVICE, 199, "PositionTicks = playback.PositionTicks,", "\t\t\t\tPositionTicks = playback.PositionTicks ?? 0,")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("R2-1 service always sends Failed", [(SERVICE, 200, "Failed = playback.PositionKnown ? null : true,", "\t\t\t\tFailed = true,")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("R2-2 OnExited ignores a kill once closing", [(HOST, 217, "return _shuttingDown && _killReason is null && !_goneWhenStopped;", "\t\t\t\treturn _shuttingDown && !_goneWhenStopped;")], "app", "FullyQualifiedName~AKillThatLands|FullyQualifiedName~WatchdogKill_ThenClose"),
    ("R2-2 shutdown does not wait for the exit handling", [(HOST, 565, "await WaitForExitHandlingAsync(Volatile.Read(ref _lastSession));", "\t\t;")], "app", "FullyQualifiedName~Shutdown_RightAfterAKill|FullyQualifiedName~WatchdogKill_ThenClose"),
    ("R2-2 exit handling not awaited", [(HOST, 588, "await session.ExitHandled.WaitAsync(ExitHandlingTimeout);", "\t\t\tawait Task.CompletedTask;")], "app", "FullyQualifiedName~Shutdown_RightAfterAKill"),
    ("R2-2 close does not wait for crash reports", [(CLOSE, 54, "await mpvPlayback.WaitForReportsAsync(interruptedReportTimeout);", "\t\t;")], "app", "FullyQualifiedName~Close_AfterAWatchdogKill"),
    ("R2-2 crash reports not tracked", [(SERVICE, 49, "Track(ReportInterruptedAsync(playback))", "\t\tbackend.PlaybackInterrupted.Subscribe(playback => _ = ReportInterruptedAsync(playback));")], "app", "FullyQualifiedName~Close_AfterAWatchdogKill"),
    ("R2-2 unbounded crash-report wait", [(SERVICE, 71, "await Task.WhenAll(pending).WaitAsync(timeout);", "\t\t\tawait Task.WhenAll(pending);")], "app", "FullyQualifiedName~Close_WaitsAtMostTwoSeconds"),
    ("R2-2 crash-report bound 5 s", [(CLOSE, 17, "InterruptedReportTimeout = TimeSpan.FromSeconds(2);", "\tpublic static readonly TimeSpan InterruptedReportTimeout = TimeSpan.FromSeconds(5);")], "app", "FullyQualifiedName~Close_WaitsAtMostTwoSeconds"),
    ("R2-3 unbounded upstream Stop", [(CLOSE, 29, "await jellyfinClient.Stop().WaitAsync(timeout);", "\t\t\tawait jellyfinClient.Stop();")], "app", "FullyQualifiedName~CloseSequenceTests"),
    ("R2-3 upstream Stop bound 10 s", [(CLOSE, 16, "UpstreamStopTimeout = TimeSpan.FromSeconds(5);", "\tpublic static readonly TimeSpan UpstreamStopTimeout = TimeSpan.FromSeconds(10);")], "app", "FullyQualifiedName~CloseSequenceTests"),
    ("R2-3 upstream Stop exception escapes", [(CLOSE, 35, "catch (Exception ex)", "\t\tcatch (Exception ex) when (ex is null)")], "app", "FullyQualifiedName~CloseSequenceTests"),
    ("R2-3 upstream stop, then mpv (in turn)", [(CLOSE, 22, "StopUpstreamPlaybackAsync(jellyfinClient, logger, upstreamStopTimeout ?? UpstreamStopTimeout),", "\t\t\tStopUpstreamPlaybackAsync(jellyfinClient, logger, upstreamStopTimeout ?? UpstreamStopTimeout).ContinueWith(_ => StopMpvAsync(mpvPlayback, logger, interruptedReportTimeout ?? InterruptedReportTimeout)).Unwrap());"), (CLOSE, 23, "StopMpvAsync(mpvPlayback, logger, interruptedReportTimeout ?? InterruptedReportTimeout));", "")], "app", "FullyQualifiedName~RunSideBySide"),
    ("R2-4 grace after an unanswered shutdown", [(HOST, 710, "Exited.WaitAsync(Left(graceful ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500)))", "\t\t\tawait session.Process.Exited.WaitAsync(Left(TimeSpan.FromMilliseconds(500)));")], "app", "FullyQualifiedName~Shutdown_ThatIsNeverAnswered"),
    ("R2-4 shutdown budget 8 s", [(HOST, 42, "public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);", "\tpublic TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(8);")], "app", "FullyQualifiedName~Shutdown_WaitsForASlowFinalReport"),
    ("R2-4 shutdown budget 12 s", [(HOST, 42, "public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);", "\tpublic TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(12);")], "app", "FullyQualifiedName~Shutdown_ThatIsNeverAnswered"),
    ("R2-x torn-down write leaks its own exception", [(CLIENT, 127, "ex is IOException or ObjectDisposedException ||", "		catch (Exception ex) when (ex is IOException or ObjectDisposedException)")], "app", "FullyQualifiedName~BridgeClientTests"),
    ("R2-x write waits for the caller's context", [(CLIENT, 202, "await output.WriteAsync(line + \"\\n\").ConfigureAwait(false);", "		await output.WriteAsync(line + \"\\n\");")], "app", "FullyQualifiedName~AWrite_FinishesWithoutTheCallersContext"),
    ("R2-x flush waits for the caller's context", [(CLIENT, 203, "await output.FlushAsync().ConfigureAwait(false);", "		await output.FlushAsync();")], "app", "FullyQualifiedName~ASmallWrite_LetsGoOfTheWriteLock"),
    ("R2-x lock wait resumes on the caller's context", [(CLIENT, 93, "if (!await _writeLock.WaitAsync(Left(), cancellationToken).ConfigureAwait(false))", "			if (!await _writeLock.WaitAsync(Left(), cancellationToken))")], "app", "FullyQualifiedName~ARequestThatWaitedForTheWriteLock"),
    ("R2-x close lock wait resumes on the caller's context", [(CLIENT, 173, "if (!await _writeLock.WaitAsync(timeout).ConfigureAwait(false))", "		if (!await _writeLock.WaitAsync(timeout))")], "app", "FullyQualifiedName~CloseOutput_ThatWaitedForTheWriteLock"),
    ("R2-x close releases on the caller's context", [(CLIENT, 181, "await Task.Run(output.Close).WaitAsync(timeout).ConfigureAwait(false);", "			await Task.Run(output.Close).WaitAsync(timeout);")], "app", "FullyQualifiedName~CloseOutput_LetsGoOfTheWriteLock"),
    ("R2-7 the page's GoBack ignored", [(VM, 273, "if (!(GoBack?.Invoke() ?? navigationService.GoBack()))", "\t\tif (!navigationService.GoBack())")], "app", "FullyQualifiedName~IdleAfterPlayback"),

    # round 4 (2026-11-14): crash vs stop decided by what came first; the `reported` rule; exit 1 after the stop; the 10 s cap
    ("R4-1 kill check and mark not atomic (old order)", [(HOST, 524, "if (!session.TryMarkKilled(reason))", "\t\t\tif (session.ShuttingDown)"), (HOST, 529, "logger.LogError(\"Killing the mpv backend (pid {Pid}): {Reason}\", session.Process.Id, reason);", "\t\t\tlogger.LogError(\"Killing the mpv backend (pid {Pid}): {Reason}\", session.Process.Id, reason); session.TryMarkKilled(reason);")], "app", "FullyQualifiedName~AKillDecidedJustBeforeTheClose"),
    ("R4-1 a killed backend is asked to shut down", [(HOST, 674, "var ask = gone is null;", "\t\tvar ask = true;")], "app", "FullyQualifiedName~AKillDecidedJustBeforeTheClose|FullyQualifiedName~AKillThatLandsAfterTheClosePathBegan"),
    ("R4-1 a killed backend gets stdin EOF", [(HOST, 701, "if (ask && !asked)", "\t\tif (!asked)")], "app", "FullyQualifiedName~AKillDecidedJustBeforeTheClose"),
    ("R4-1 gone ignores the end of the output", [(HOST, 195, "_goneWhenStopped = ProcessHasExited() || Client.IsClosed;", "\t\t\t\t_goneWhenStopped = ProcessHasExited();")], "app", "FullyQualifiedName~ACrashSeenOnlyAfterTheCloseBegan"),
    ("R4-1 gone ignores an exit already seen", [(HOST, 195, "_goneWhenStopped = ProcessHasExited() || Client.IsClosed;", "\t\t\t\t_goneWhenStopped = Client.IsClosed;")], "app", "FullyQualifiedName~AnExitAlreadySeen_ButNotYetHandled"),
    ("R4-1 OnExited ignores a death before the stop", [(HOST, 217, "return _shuttingDown && _killReason is null && !_goneWhenStopped;", "\t\t\t\treturn _shuttingDown && _killReason is null;")], "app", "FullyQualifiedName~ACrashSeenOnlyAfterTheCloseBegan|FullyQualifiedName~AnExitAlreadySeen_ButNotYetHandled"),
    ("R4-1 the stop's mark is taken again", [(HOST, 189, "if (_shuttingDown)", "\t\t\t\tif (false)")], "app", "FullyQualifiedName~AConnectThatHangsAtTheClose"),
    ("R4-1 the stop releases before a crash is handled", [(HOST, 729, "await WaitForExitHandlingAsync(session);", "\t\t\tawait Task.CompletedTask;")], "app", "FullyQualifiedName~AReportedEventStillInThePipe"),
    ("R4-2 idle closes the session", [(HOST, 826, "if (!state.IsActive || state.ItemId is not", "\t\tif (state.State == Bridge.States.Idle) { session.OpenPlay = null; } if (!state.IsActive || state.ItemId is not { Length: > 0 } itemId || state.PlaySessionId is not { Length: > 0 } playSessionId)")], "app", "FullyQualifiedName~Crash_AfterAStopWentIdle_BeforeItsReport"),
    ("R4-2 ended closes the session", [(HOST, 775, "case Bridge.Events.Reported when", "\t\t\tcase Bridge.Events.Ended: session.OpenPlay = null; break; case Bridge.Events.Reported when ReportedPlaySession(e) is { } reported:")], "app", "FullyQualifiedName~Crash_AfterAStopWentIdle_BeforeItsReport"),
    ("R4-2 reported ignored", [(HOST, 781, "session.OpenPlay = null;", "\t\t\t\t\t;")], "app", "FullyQualifiedName~Crash_AfterTheStopWasReported|FullyQualifiedName~Crash_AfterTheReportedEvent_BeforeIdle"),
    ("R4-2 reported closes any session", [(HOST, 779, "string.Equals(open.PlaySessionId, reported", "\t\t\t\tif (session.OpenPlay is { } open)")], "app", "FullyQualifiedName~AReportForAnotherSession"),
    ("R4-2 a failed report keeps the session open", [(HOST, 779, "string.Equals(open.PlaySessionId, reported", "\t\t\t\tif (session.OpenPlay is { } open && string.Equals(open.PlaySessionId, reported, StringComparison.Ordinal) && !((e.Data as JsonObject)?[\"outcome\"] is JsonValue o && o.ToString() == \"failed\"))")], "app", "FullyQualifiedName~AnyReportOutcome_ClosesTheSession"),
    ("R4-2 only known outcomes close the session", [(HOST, 779, "string.Equals(open.PlaySessionId, reported", "\t\t\t\tif (session.OpenPlay is { } open && string.Equals(open.PlaySessionId, reported, StringComparison.Ordinal) && (e.Data as JsonObject)?[\"outcome\"] is JsonValue o && o.ToString() is \"sent\" or \"failed\")")], "app", "FullyQualifiedName~AnyReportOutcome_ClosesTheSession"),
    ("R4-2 the outcome is read as a string", [(HOST, 775, "case Bridge.Events.Reported when ReportedPlaySession(e) is { } reported:", "\t\t\tcase Bridge.Events.Reported when ReportedPlaySession(e) is { } reported && e.DataAs<Dictionary<string, string?>>() is not null:")], "app", "FullyQualifiedName~AnyReportOutcome_ClosesTheSession"),
    ("R4-2 an early report does not mark its session seen", [(HOST, 785, "session.SeenPlaySessions.Add(reported);", "				;")], "app", "FullyQualifiedName~AReportThatComesBeforeAnyStateOfItsSession"),
    ("R4-2 a newer session does not replace", [(HOST, 831, "if (session.SeenPlaySessions.Add(playSessionId))", "\t\tif (session.SeenPlaySessions.Add(playSessionId) && session.OpenPlay is null)")], "app", "FullyQualifiedName~ANewerSession_ReplacesTheOpenOne"),
    ("R4-2 a reported session reopens", [(HOST, 831, "if (session.SeenPlaySessions.Add(playSessionId))", "\t\tif (session.SeenPlaySessions.Add(playSessionId) | session.OpenPlay is null)")], "app", "FullyQualifiedName~AStateAfterTheReport"),
    ("R4-2 a state without a play session opens one", [(HOST, 826, "if (!state.IsActive || state.ItemId is not", "\t\tif (!state.IsActive || state.ItemId is not { Length: > 0 } itemId) { return; } var playSessionId = state.PlaySessionId ?? \"\"; if (false)")], "app", "FullyQualifiedName~Crash_WhileTheNextItemOpensWithoutAPlaySessionYet"),
    ("R4-2 media source never filled in", [(HOST, 839, "open with { MediaSourceId = mediaSourceId }", "\t\t\t;")], "app", "FullyQualifiedName~TheReportCarriesTheMediaSource"),
    ("R4-2 positions with no item recorded", [(HOST, 772, "case Bridge.Events.Position when PositionTicks(e) is { } position", "\t\t\tcase Bridge.Events.Position when PositionTicks(e) is { } position && (session.LastState?.ItemId ?? \"\") is var itemId && (session.LastState?.PlaySessionId ?? \"\") is var playSessionId:")], "app", "FullyQualifiedName~APositionEventWithNoItemPlaying"),
    ("R4-2 a position for any item", [(HOST, 949, "var position = session.LatestPosition", "\t\tvar position = session.LatestPosition is { } mark ? mark.Ticks : (long?)null;")], "app", "FullyQualifiedName~Crash_WhilePlaying_BeforeItsFirstPositionEvent|FullyQualifiedName~Crash_WhileOpening"),
    ("R4-3 a non-zero exit after the stop is a crash", [(HOST, 875, "if (session.ExitIsTheStops())", "\t\t\tif (session.ExitIsTheStops() && exitCode == 0)")], "app", "FullyQualifiedName~Exit1_AfterAnsweringShutdown|FullyQualifiedName~Exit1_AfterStdinEof|FullyQualifiedName~Exit1_WithoutAnsweringShutdown"),
    ("R4-3 exit 1 after shutdown is not a crash (EOF only)", [(HOST, 875, "if (session.ExitIsTheStops())", "\t\t\tif (session.ExitIsTheStops() && exitCode == 0)")], "app", "FullyQualifiedName~Exit1_AfterStdinEof"),
    ("R4-3 exit 1 after shutdown logged as information", [(HOST, 883, "exited with code {Code} after FluentFin stopped it", "\t\t\t\t\tlogger.LogInformation(\"The mpv backend exited with code {Code} after FluentFin stopped it (shutdown, stdin EOF or a kill to close): the server may not have its final playback report; FluentFin does not send one\", exitCode);")], "app", "FullyQualifiedName~Exit1_AfterAnsweringShutdown"),
    ("R4-3 exit 0 after shutdown warned", [(HOST, 877, "if (exitCode == 0 || !session.Started)", "\t\t\t\tif (false)")], "app", "FullyQualifiedName~Exit0_AfterShutdown_IsLoggedAsInformation"),
    ("R4-x test pipes: concurrent writes not serialized", [("FluentFin.Tests/Mpv/PipePair.cs", 55, "await _sendLock.WaitAsync();", "\t\t;"), ("FluentFin.Tests/Mpv/PipePair.cs", 62, "_sendLock.Release();", "\t\t\t;")], "app", "FullyQualifiedName~TheTestPipes_LinesSentFromManyThreadsAtOnce"),
    ("R4-4 a shutdown answered late gets half a second more", [(HOST, 710, "Exited.WaitAsync(Left(graceful ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500)))", "\t\t\tawait session.Process.Exited.WaitAsync(Left(asked ? TimeSpan.FromMilliseconds(500) : graceful ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500)));")], "app", "FullyQualifiedName~Shutdown_AnsweredJustBeforeTheDeadline"),
    # round 5 (2026-11-14): the close waits for the exit of the kill it makes; the position is the play session's; the report
    # goes only to the session's server; the crash message names only what was written just before the exit
    ("R5-1 the exit handling is skipped while the exit is unseen (old check)", [(HOST, 580, "if (session is null || session.ExitHandled.IsCompleted || (session.ShuttingDown && session.ExitIsTheStops()) || session.ExitWaited)", "\t\t\tif (session is null || !session.Process.Exited.IsCompleted || session.ExitHandled.IsCompleted)")], "app", "FullyQualifiedName~WhoseExitIsSeenAfterTheKill"),
    ("R5-1 the exit handling is skipped while the exit is unseen (kept skip)", [(HOST, 580, "if (session is null || session.ExitHandled.IsCompleted || (session.ShuttingDown && session.ExitIsTheStops()) || session.ExitWaited)", "\t\t\tif (session is null || !session.Process.Exited.IsCompleted || session.ExitHandled.IsCompleted || (session.ShuttingDown && session.ExitIsTheStops()) || session.ExitWaited)")], "app", "FullyQualifiedName~WhoseExitIsSeenAfterTheKill"),
    ("R5-1 a kill the close decides is waited for", [(HOST, 580, "if (session is null || session.ExitHandled.IsCompleted || (session.ShuttingDown && session.ExitIsTheStops()) || session.ExitWaited)", "\t\t\tif (session is null || session.ExitHandled.IsCompleted || session.ExitWaited)")], "app", "FullyQualifiedName~AKillTheCloseDecides_IsNotWaitedFor"),
    ("R5-1 the exit handling is not bounded", [(HOST, 588, "await session.ExitHandled.WaitAsync(ExitHandlingTimeout);", "\t\t\tawait session.ExitHandled.WaitAsync(TimeSpan.FromSeconds(30));")], "app", "FullyQualifiedName~AKillThatDoesNotTake_DelaysTheCloseByAtMostTheExitHandlingBound"),
    ("R5-2 the position of any session of the item", [(HOST, 949, "var position = session.LatestPosition is { } mark && string.Equals(mark.PlaySessionId, open.PlaySessionId", "\t\tvar position = session.LatestPosition is { } mark && mark.ItemId == open.ItemId ? mark.Ticks : (long?)null;")], "app", "FullyQualifiedName~Crash_WhileAReplayOfTheSameItemOpens|FullyQualifiedName~Crash_AfterARestartOfTheSameItem"),
    ("R5-2 a position with no play session is recorded", [(HOST, 772, "case Bridge.Events.Position when PositionTicks(e) is { } position && session.LastState is", "\t\t\tcase Bridge.Events.Position when PositionTicks(e) is { } position && session.LastState is { ItemId: { Length: > 0 } itemId } && (session.LastState.PlaySessionId ?? \"\") is var playSessionId:")], "app", "FullyQualifiedName~APositionWhileTheNextItemOpensWithoutAPlaySession"),
    ("R5-3 the session's server is not recorded", [(HOST, 834, "session.OpenPlay = new OpenPlaySession(itemId, state.MediaSourceId, playSessionId, playedAs?.ServerUrl, playedAs?.UserId);", "\t\t\tsession.OpenPlay = new OpenPlaySession(itemId, state.MediaSourceId, playSessionId, null, playedAs?.UserId);")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("R5-3 the server is the connection's when the crash is handled", [(HOST, 950, "return new MpvInterruptedPlayback(open.ItemId, open.MediaSourceId, open.PlaySessionId, position, reason, open.ServerUrl, open.UserId);", "\t\treturn new MpvInterruptedPlayback(open.ItemId, open.MediaSourceId, open.PlaySessionId, position, reason, session.Identity?.ServerUrl, open.UserId);")], "app", "FullyQualifiedName~Crash_AfterFluentFinSwitchedServers"),
    ("R5-3 the service ignores the session's server", [(SERVICE, 169, "if (!SameServer(playback.ServerUrl, current))", "\t\t\tif (false)")], "app", "FullyQualifiedName~Crash_AfterFluentFinSwitchedServers"),
    ("R5-3 the server is compared when the session opens, not when the crash is handled", [(SERVICE, 161, "var current = _jellyfinClient.BaseUrl;", "\t\t\tvar current = playback.ServerUrl;")], "app", "FullyQualifiedName~Crash_AfterFluentFinSwitchedServers"),
    ("R5-3 the skip log shows the full URL", [(SERVICE, 173, "itemId, BridgeRedaction.ServerForLog(playback.ServerUrl), BridgeRedaction.ServerForLog(current));", "\t\t\t\t\titemId, playback.ServerUrl, current);")], "app", "FullyQualifiedName~Crash_AfterFluentFinSwitchedServers"),
    ("R5-3 SameServer ignores the port", [(SERVICE, 222, "a.Port == b.Port &&", "\t\t\t   true &&")], "app", "FullyQualifiedName~SameServer_ComparesSchemeHostPortAndPath"),
    ("R5-3 SameServer ignores the path", [(SERVICE, 223, "string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);", "\t\t\t   true;")], "app", "FullyQualifiedName~SameServer_ComparesSchemeHostPortAndPath"),
    ("R5-3 SameServer compares the raw strings", [(SERVICE, 215, "if (!Uri.TryCreate(sessionServer, UriKind.Absolute, out var a) || !Uri.TryCreate(currentServer, UriKind.Absolute, out var b))", "\t\tif (sessionServer is not null) { return sessionServer == currentServer; } if (!Uri.TryCreate(sessionServer, UriKind.Absolute, out var a) || !Uri.TryCreate(currentServer, UriKind.Absolute, out var b))")], "app", "FullyQualifiedName~SameServer_ComparesSchemeHostPortAndPath|FullyQualifiedName~Crash_WhenFluentFinIsOnTheSameServerUnderAnotherSpelling"),
    ("R5-3 ServerForLog keeps the user info", [("FluentFin.Core/Mpv/BridgeRedaction.cs", 40, ": Uri.TryCreate(url, UriKind.Absolute, out var uri)", "\t\t: Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.GetLeftPart(UriPartial.Authority)")], "app", "FullyQualifiedName~ServerForLog|FullyQualifiedName~Identity_ToStringNeverShowsCredentials"),
    ("R5-3 the connect log shows the full server URL", [("FluentFin.Core/Mpv/BridgeCommands.cs", 20, "public override string ToString() =>", "\tpublic override string ToString() => $\"{UserName} ({UserId}) on {ServerName} {ServerUrl} as {ClientName} {ClientVersion} / {DeviceName}\";")], "app", "FullyQualifiedName~Identity_ToStringNeverShowsCredentials|FullyQualifiedName~Crash_AfterFluentFinSwitchedServers"),
    ("R5-4 the old 3 s stderr window", [(HOST, 911, ": session.LastStderr(since: exitedAt - CrashStderrWindowMs, afterOrder: session.LastEventOrder);", "\t\t\t\t: session.LastStderr(since: Environment.TickCount64 - 3000);")], "app", "FullyQualifiedName~CrashMessage_"),
    ("R5-4 the last event does not bound the window", [(HOST, 911, ": session.LastStderr(since: exitedAt - CrashStderrWindowMs, afterOrder: session.LastEventOrder);", "\t\t\t\t: session.LastStderr(since: exitedAt - CrashStderrWindowMs);")], "app", "FullyQualifiedName~CrashMessage_DoesNotNameAnErrorWrittenBeforeTheLastEvent"),
    ("R5-4 the 1 s before the exit does not bound the window", [(HOST, 911, ": session.LastStderr(since: exitedAt - CrashStderrWindowMs, afterOrder: session.LastEventOrder);", "\t\t\t\t: session.LastStderr(afterOrder: session.LastEventOrder);")], "app", "FullyQualifiedName~CrashMessage_DoesNotNameAnErrorWrittenMoreThanASecondBeforeTheExit"),
    ("R5-4 a 3 s window before the exit", [(HOST, 21, "private const long CrashStderrWindowMs = 1000;", "\tprivate const long CrashStderrWindowMs = 3000;")], "app", "FullyQualifiedName~CrashMessage_DoesNotNameAnErrorWrittenMoreThanASecondBeforeTheExit"),
    ("R5-4 events do not mark their order", [(HOST, 764, "session.LastEventOrder = session.NextReadOrder();", "\t\t;")], "app", "FullyQualifiedName~CrashMessage_DoesNotNameAnErrorWrittenBeforeTheLastEvent"),
    ("R5-4 the libmpv traceback is not harmless", [(HOST, 225, "var harmless = IsLibmpvMissing(line);", "\t\t\t\tvar harmless = false;")], "app", "FullyQualifiedName~CrashMessage_NeverNamesTheHarmlessLibmpvTraceback|FullyQualifiedName~StartFailure_DoesNotNameTheLibmpvTraceback"),
    ("R5-4 only the libmpv lines, not their traceback", [(HOST, 226, "if (harmless)", "\t\t\t\tif (false)")], "app", "FullyQualifiedName~CrashMessage_NeverNamesTheHarmlessLibmpvTraceback"),
    ("R5-4 the walk back crosses into another traceback", [(HOST, 233, "if (!head && !earlier.Harmless && (earlier.Line.Length == 0 || !char.IsWhiteSpace(earlier.Line[0])))", "\t\t\t\t\t\tif (false)")], "app", "FullyQualifiedName~CrashMessage_NamesARealError"),
    ("R5-4 harmless lines are named", [(HOST, 264, "return _stderrTail.LastOrDefault(x => x.At >= since && x.Order > afterOrder && !x.Harmless && LooksLikeAnError(x.Line))", "\t\t\t\treturn _stderrTail.LastOrDefault(x => x.At >= since && x.Order > afterOrder && LooksLikeAnError(x.Line))?.Line.Trim() ?? \"\";")], "app", "FullyQualifiedName~CrashMessage_NeverNamesTheHarmlessLibmpvTraceback|FullyQualifiedName~StartFailure_DoesNotNameTheLibmpvTraceback"),
    ("R5-4 the exit is when it was seen", [(HOST, 908, "var exitedAt = Math.Min(exitSeenAt, session.Process.ExitedAt ?? exitSeenAt);", "			var exitedAt = exitSeenAt;")], "app", "FullyQualifiedName~CrashMessage_NamesTheErrorWrittenJustBeforeTheExit_WhenTheExitIsSeenLate"),
    ("R5-4 JobProcess records no exit time", [(JOBPROC, 398, "exitedAt(ExitTickCount(duplicate));", "			;")], "app", "FullyQualifiedName~ExitedAt_IsWhenTheProcessEnded"),
    ("R5-4 the launcher does not pass the exit time on", [("FluentFin/Mpv/WindowsMpvBackendLauncher.cs", 79, "public long? ExitedAt => _process.ExitedAt;", "		public long? ExitedAtNotPassedOn => _process.ExitedAt;")], "app", "FullyQualifiedName~ExitedAt_IsWhenTheProcessEnded"),
    ("R5-4 the exit time is when the wait saw it", [(JOBPROC, 438, "return ago <= TimeSpan.Zero ? nowTicks : nowTicks - (long)ago.TotalMilliseconds;", "		return nowTicks;")], "app", "FullyQualifiedName~TheExitTime_IsAsLongAgoAsWindowsRecorded"),
    # round 5 follow-up (2026-11-14): the crash report goes only as the user the session was played as (docs/BRIDGE.md §6)
    ("R5-5 the session's user is not recorded", [(HOST, 834, "session.OpenPlay = new OpenPlaySession(itemId, state.MediaSourceId, playSessionId, playedAs?.ServerUrl, playedAs?.UserId);", "\t\t\tsession.OpenPlay = new OpenPlaySession(itemId, state.MediaSourceId, playSessionId, playedAs?.ServerUrl, null);")], "app", "FullyQualifiedName~InterruptedPlaybackTests"),
    ("R5-5 the user is the connection's when the crash is handled", [(HOST, 950, "return new MpvInterruptedPlayback(open.ItemId, open.MediaSourceId, open.PlaySessionId, position, reason, open.ServerUrl, open.UserId);", "\t\treturn new MpvInterruptedPlayback(open.ItemId, open.MediaSourceId, open.PlaySessionId, position, reason, open.ServerUrl, session.Identity?.UserId);")], "app", "FullyQualifiedName~Crash_AfterAUserSwitchOnTheSameServer"),
    ("R5-5 the service ignores the session's user", [(SERVICE, 177, "if (!SameUser(playback.UserId, currentUser))", "\t\t\tif (false)")], "app", "FullyQualifiedName~Crash_AfterAUserSwitchOnTheSameServer"),
    ("R5-5 the user is compared with the session's own", [(SERVICE, 162, "var currentUser = _jellyfinClient.UserId;", "\t\t\tvar currentUser = Guid.TryParse(playback.UserId, out var sessionUser) ? sessionUser : Guid.Empty;")], "app", "FullyQualifiedName~Crash_AfterAUserSwitchOnTheSameServer"),
    ("R5-5 SameUser compares the raw strings", [(SERVICE, 231, "Guid.TryParse(sessionUser, out var user) && user != Guid.Empty && user == currentUser;", "\t\tcurrentUser != Guid.Empty && sessionUser == currentUser.ToString(\"N\");")], "app", "FullyQualifiedName~SameUser_"),
    ("R5-5 nobody logged in counts as a user", [(SERVICE, 231, "Guid.TryParse(sessionUser, out var user) && user != Guid.Empty && user == currentUser;", "\t\tGuid.TryParse(sessionUser, out var user) && user == currentUser;")], "app", "FullyQualifiedName~SameUser_"),
    # round 6 (2026-11-14): Log out / Switch user stop the mpv playback and wait for its report before the token goes; a page
    # removed without a navigation is a leave; a crash before the close counts even unseen; the backend's fatal error is the
    # crash's cause; one wait for an exit; a session is played as its play's connection; no server URL in ToString
    ("R6-1 Log out does not stop the mpv playback", [(TITLEBAR, 73, "await _mpvPlayback.StopForSignOutAsync();", "\t\t;")], "app", "FullyQualifiedName~SigningOut_"),
    ("R6-1 Switch user does not stop the mpv playback", [(TITLEBAR, 83, "await _mpvPlayback.StopForSignOutAsync();", "\t\t;")], "app", "FullyQualifiedName~SigningOut_"),
    ("R6-1 Log out stops only after revoking the token", [(TITLEBAR, 73, "await _mpvPlayback.StopForSignOutAsync();", "\t\t;"), (TITLEBAR, 76, "await _jellyfinClient.Logout();", "\t\tawait _jellyfinClient.Logout(); await _mpvPlayback.StopForSignOutAsync();")], "app", "FullyQualifiedName~SigningOut_"),
    ("R6-1 Switch user stops only after leaving", [(TITLEBAR, 83, "await _mpvPlayback.StopForSignOutAsync();", "\t\t;"), (TITLEBAR, 85, "_setupNavigationService.NavigateTo<LoginViewModel>(CurrentServer);", "\t\t_setupNavigationService.NavigateTo<LoginViewModel>(CurrentServer); await _mpvPlayback.StopForSignOutAsync();")], "app", "FullyQualifiedName~SigningOut_"),
    ("R6-1 the sign-out sends no stop", [(SERVICE, 377, "await StopAsync(generation, \"signing out\").WaitAsync(Left());", "\t\t\t\t;")], "app", "FullyQualifiedName~SigningOut_"),
    ("R6-1 the sign-out's stop is not bounded", [(SERVICE, 377, "await StopAsync(generation, \"signing out\").WaitAsync(Left());", "\t\t\t\tawait StopAsync(generation, \"signing out\");")], "app", "FullyQualifiedName~SigningOut_IsBoundedInAll_WhenTheBackendNeverAnswersTheStop"),
    ("R6-1 the sign-out does not wait for the stop report", [(SERVICE, 309, "if (!await _backend.WaitForStopReportsAsync(Left()))", "\t\t\tif (false)")], "app", "FullyQualifiedName~SigningOut_"),
    ("R6-1 the sign-out's report wait is not bounded", [(SERVICE, 309, "if (!await _backend.WaitForStopReportsAsync(Left()))", "\t\t\tif (!await _backend.WaitForStopReportsAsync(TimeSpan.FromSeconds(30)))")], "app", "FullyQualifiedName~SigningOut_"),
    ("R6-1 the sign-out does not wait for FluentFin's own Stopped", [(SERVICE, 314, "await WaitForReportsAsync(Left());", "\t\t\t;")], "app", "FullyQualifiedName~SigningOut_WhenTheBackendDiesBeforeItsReport"),
    ("R6-1 a page that was left still counts as playing", [(SERVICE, 268, "Interlocked.Exchange(ref _leftGeneration, generation + 1);", "\t\t\t;")], "app", "FullyQualifiedName~SigningOut_AfterThePageWasLeft"),
    ("R6-1 the sign-out stops with no page", [(SERVICE, 303, "if (generation != Interlocked.Read(ref _leftGeneration))", "\t\t\tif (true)")], "app", "FullyQualifiedName~SigningOut_AfterThePageWasLeft"),
    ("R6-1 the report wait ends at idle", [(HOST, 471, "bool Owed() => !session.ExitHandled.IsCompleted && (session.OpenPlay is not null || session.LastState is { IsActive: true });", "\t\t\tbool Owed() => !session.ExitHandled.IsCompleted && session.LastState is { IsActive: true };")], "app", "FullyQualifiedName~SignOutStopReportTests"),
    ("R6-1 the report wait ends at the reported", [(HOST, 471, "bool Owed() => !session.ExitHandled.IsCompleted && (session.OpenPlay is not null || session.LastState is { IsActive: true });", "\t\t\tbool Owed() => !session.ExitHandled.IsCompleted && session.OpenPlay is not null;")], "app", "FullyQualifiedName~SignOutStopReportTests"),
    ("R6-1 the report wait ignores the backend's death", [(HOST, 492, "await Task.WhenAny(done.Task, session.ExitHandled).WaitAsync(timeout);", "\t\t\t\tawait done.Task.WaitAsync(timeout);")], "app", "FullyQualifiedName~SignOutStopReportTests"),
    ("R6-1 a removed page ignores its own navigation", [(VM, 395, "if (navigatedFrom || loadedAgain || !IsMpv || _mpvLeaving)", "\t\tif (loadedAgain || !IsMpv || _mpvLeaving)")], "app", "FullyQualifiedName~APageNavigatedFrom_IsLeftByItsNavigation"),
    ("R6-1 a page moved within the window is left", [(VM, 395, "if (navigatedFrom || loadedAgain || !IsMpv || _mpvLeaving)", "\t\tif (navigatedFrom || !IsMpv || _mpvLeaving)")], "app", "FullyQualifiedName~APageMovedWithinTheWindow"),
    ("R6-1 a removed page is left twice", [(VM, 395, "if (navigatedFrom || loadedAgain || !IsMpv || _mpvLeaving)", "\t\tif (navigatedFrom || loadedAgain || !IsMpv)")], "app", "FullyQualifiedName~APageRemovedFromTheWindow"),
    ("R6-1 another player's removed page is taken", [(VM, 395, "if (navigatedFrom || loadedAgain || !IsMpv || _mpvLeaving)", "\t\tif (navigatedFrom || loadedAgain || _mpvLeaving)")], "app", "FullyQualifiedName~APageOfAnotherPlayer"),
    ("R6-1 a removed page does not stop", [(VM, 438, "await OnNavigatedFromMpv(\"the player page was removed", "\t\t;")], "app", "FullyQualifiedName~APageRemovedFromTheWindow|FullyQualifiedName~APageRemovedInFullScreen"),
    ("R6-1 a removed page stays full screen", [(VM, 401, "await LeaveFullScreen();", "\t\t;")], "app", "FullyQualifiedName~APageRemovedInFullScreen"),
    ("R6-2 dead is when the exit was seen (old)", [(HOST, 195, "_goneWhenStopped = ProcessHasExited() || Client.IsClosed;", "\t\t\t\t_goneWhenStopped = Process.Exited.IsCompleted || Client.IsClosed;")], "app", "FullyQualifiedName~ACrashBeforeTheClose_NeitherSeenNorItsOutputEnded"),
    ("R6-2 JobProcess asks its wait, not Windows", [(JOBPROC, 75, "return IsSignalled(_process) ?? Exited.IsCompleted;", "\t\t\t\treturn Exited.IsCompleted;")], "app", "FullyQualifiedName~HasExited_IsTrueBeforeTheExitIsSeen"),
    ("R6-2 a signalled process reads as running", [(JOBPROC, 96, "WAIT_OBJECT_0 => true,", "\t\t\t\tWAIT_OBJECT_0 => false,")], "app", "FullyQualifiedName~IsSignalled_ReadsTheProcessHandle|FullyQualifiedName~HasExited_"),
    ("R6-2 the launcher does not pass HasExited on", [(LAUNCHER, 82, "public bool HasExited => _process.HasExited;", "\t\tpublic bool HasExitedNotPassedOn => _process.HasExited;")], "app", "FullyQualifiedName~HasExited_IsTrueBeforeTheExitIsSeen"),
    ("R6-3 the fatal error is not the cause", [(HOST, 909, "var last = session.FatalError is { Length: > 0 } fatal", "\t\t\tvar last = session.FatalError is { Length: > 1000000 } fatal")], "app", "FullyQualifiedName~CrashMessage_NamesTheBackendsOwnFatalError"),
    ("R6-3 a non-fatal error is the cause", [(HOST, 789, "if (error.Fatal && !string.IsNullOrWhiteSpace(error.Message))", "\t\t\t\tif (!string.IsNullOrWhiteSpace(error.Message))")], "app", "FullyQualifiedName~CrashMessage_ANonFatalErrorEvent"),
    ("R6-3 the fatal error is not redacted", [(HOST, 792, "session.FatalError = BridgeRedaction.Redact(error.Message).Trim();", "\t\t\t\t\tsession.FatalError = error.Message.Trim();")], "app", "FullyQualifiedName~CrashMessage_NamesTheBackendsOwnFatalError"),
    ("R6-4 the close waits for the same exit twice", [(HOST, 580, "if (session is null || session.ExitHandled.IsCompleted || (session.ShuttingDown && session.ExitIsTheStops()) || session.ExitWaited)", "\t\t\tif (session is null || session.ExitHandled.IsCompleted || (session.ShuttingDown && session.ExitIsTheStops()))")], "app", "FullyQualifiedName~AKillThatNeverTakes_IsWaitedForOnce"),
    ("R6-4 a finished wait is not marked", [(HOST, 592, "session.ExitWaited = true;", "\t\t\t\t;")], "app", "FullyQualifiedName~AKillThatNeverTakes_IsWaitedForOnce"),
    ("R6-5 the play's connection is not recorded", [(HOST, 452, "session.PlayIdentity = session.Identity;", "\t\t\t;")], "app", "FullyQualifiedName~FirstShownAfterTheSwitch"),
    ("R6-5 a session is the connection's at its first state (old)", [(HOST, 833, "var playedAs = session.PlayIdentity ?? session.Identity;", "\t\t\tvar playedAs = session.Identity;")], "app", "FullyQualifiedName~FirstShownAfterTheSwitch"),
    ("R6-5 any command is taken for a play", [(HOST, 449, "if (command == Bridge.Commands.Play)", "\t\tif (true)")], "app", "FullyQualifiedName~FirstShownAfterTheSwitch"),
    ("R6-7 the interrupted playback prints its server URL", [(IMPV, 117, "public override string ToString() =>", "\tpublic string ToStringNotOverridden() =>")], "app", "FullyQualifiedName~InterruptedPlayback_ToString"),
]


def run(args, work, timeout=900):
    # the copy has no backend above it: JobProcessTests find the repo's venv python through this variable
    env = dict(os.environ, FLUENTFIN_MPV_TEST_PYTHON=os.path.join(REPO, "backend", ".venv", "Scripts", "python.exe"))
    return subprocess.run(args, capture_output=True, text=True, timeout=timeout, cwd=work, env=env)


def copy(work, ui):
    if os.path.isdir(ui):
        # refresh the sources, keep bin/obj for incremental builds
        for root, dirs, files in os.walk(os.path.join(REPO, "ui")):
            dirs[:] = [d for d in dirs if d not in ("bin", "obj", ".vs")]
            rel = os.path.relpath(root, os.path.join(REPO, "ui"))
            os.makedirs(os.path.join(ui, rel), exist_ok=True)
            for f in files:
                # only changed files, with a fresh mtime: copy2's old mtimes would look older than the last build
                src, dst = os.path.join(root, f), os.path.join(ui, rel, f)
                data = open(src, "rb").read()
                if not os.path.exists(dst) or open(dst, "rb").read() != data:
                    open(dst, "wb").write(data)
    else:
        shutil.copytree(os.path.join(REPO, "ui"), ui, ignore=shutil.ignore_patterns("bin", "obj", ".vs"))
    shutil.copy2(os.path.join(REPO, "nuget.config"), os.path.join(work, "nuget.config"))


# SHOW=1: also the names of the tests that failed (a quiet run prints only the counts)
SHOW_LOGGER = ["--logger", "console;verbosity=normal"] if os.environ.get("SHOW") else []


def test(work, ui, project, flt):
    if project == "app":
        r = run(["dotnet", "test", os.path.join(ui, "FluentFin.Tests", "FluentFin.Tests.csproj"), "-p:Platform=x64", "-nologo", "-v:quiet", "--filter", flt] + SHOW_LOGGER, work)
    else:
        r = run(["dotnet", "test", os.path.join(ui, "FluentFin.Core.Tests", "FluentFin.Core.Tests.csproj"), "-nologo", "-v:quiet", "--filter", flt] + SHOW_LOGGER, work)
    out = r.stdout + r.stderr
    if "error CS" in out or "Build FAILED" in out:
        return "BUILD-FAIL", out
    if "No test matches" in out:
        return "NO-TESTS", out
    return ("KILLED" if r.returncode else "SURVIVED"), out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work", required=True)
    parser.add_argument("only", nargs="*")
    args = parser.parse_args()
    work = os.path.abspath(args.work)
    if os.path.commonpath([work, REPO]) == REPO:
        sys.exit("--work must be outside the repo")
    ui = os.path.join(work, "ui")
    os.makedirs(work, exist_ok=True)
    copy(work, ui)
    selected = [m for m in MUTATIONS if not args.only or any(o in m[0] for o in args.only)]

    # sanity: everything passes unmutated
    for project in sorted({m[2] for m in selected}):
        flt = "|".join(sorted({m[3] for m in selected if m[2] == project}))
        result, out = test(work, ui, project, flt)
        if result != "SURVIVED":
            print("BASELINE NOT GREEN for", project, result)
            print(out[-3000:])
            return 1
    print("baseline green for all", len(selected), "filters", flush=True)

    counts = {}
    for name, edits, project, flt in selected:
        originals = {}
        where = []
        try:
            for rel, line, must, new in edits:
                path = os.path.join(ui, rel)
                if path not in originals:
                    originals[path] = open(path, "rb").read()
                text = open(path, "rb").read().decode("utf-8-sig")
                lines = text.split("\n")
                # the line given, or the nearest one with the expected text (earlier edits shift lines)
                hits = [i + 1 for i, l in enumerate(lines) if must in l]
                assert hits, (name, rel, must)
                line = min(hits, key=lambda i: abs(i - line))
                where.append("%s:%d" % (rel.split("/")[-1], line))
                cr = "\r" if lines[line - 1].endswith("\r") else ""
                lines[line - 1] = new + cr
                bom = originals[path].startswith(b"\xef\xbb\xbf")
                open(path, "wb").write((b"\xef\xbb\xbf" if bom else b"") + "\n".join(lines).encode("utf-8"))
            started = time.time()
            result, out = test(work, ui, project, flt)
            counts[result] = counts.get(result, 0) + 1
            print("%-52s %-10s (%.0f s) %s" % (name, result, time.time() - started, ", ".join(where)), flush=True)
            if os.environ.get("SHOW"):
                print("\n".join(l for l in out.splitlines() if l.lstrip().startswith("Failed ") or "Failed!" in l or "Passed!" in l or " error " in l)[:2500], flush=True)
            if result == "BUILD-FAIL":
                print("\n".join(l for l in out.splitlines() if "error" in l)[:1500])
        finally:
            for path, data in originals.items():
                open(path, "wb").write(data)
    print("summary:", ", ".join("%s %d" % kv for kv in sorted(counts.items())))
    return 0 if set(counts) <= {"KILLED"} else 1


if __name__ == "__main__":
    sys.exit(main())
