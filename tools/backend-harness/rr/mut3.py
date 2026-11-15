"""Mutations on a scratch copy of backend/, tests run off-screen via hidden_run.

mut2.py's set (R*, Q*, N*) with patterns brought up to the round-3 code, the
round-3 reviewer's additions (M*), and the round-3 fixes removed one at a
time (F*): each must be caught. mut3.py [prefix ...]

    MUT_WORK=<a folder outside the repo>  (copies, logs, sandboxed APPDATA)"""
import os, re, shutil, subprocess, sys
R2 = os.environ.get("MUT_WORK") or sys.exit("set MUT_WORK to a work folder outside the repo (copies, logs, sandboxed APPDATA go there)")
SRC = r"C:\Users\moise\Dev\fluentfin-mpv\backend"
S = os.path.join(R2, "mut")
PY = os.path.join(SRC, ".venv", "Scripts", "python.exe")
HR = r"C:\Users\moise\Dev\fluentfin-mpv\tools\backend-bench\hidden_run.py"
MPVD = os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WinGet\Packages\mpv-player.mpv-CI.MSVC_Microsoft.Winget.Source_8wekyb3d8bbwe")
P = os.path.join(S, "jellyfin_mpv_shim")
ENV = os.path.join(R2, "env-mut")

B = "bridge.py"
H = "bridge_http.py"
MUTS = [
    ("R1 report before the item fetch", B, [
        ("                outgoing = self._report_outgoing(pm, video)\n", "                outgoing = None\n"),
        ("            try:\n                started = self.runtime.start_playback(client, user_id,\n",
         "            try:\n                self._report_outgoing(self.runtime.pm, None)\n                started = self.runtime.start_playback(client, user_id,\n")]),
    ("R2 outgoing stream not released", B, [
        ("                pm.release_stream(outgoing)\n", "                pass\n")]),
    ("R3 degraded shutdown leaves mpv", B, [
        ("        self._run_steps([(\"mpv\", lambda: self._end_mpv_unlocked(pm))])\n", "        pass\n")]),
    ("R4 unclean shutdown exits 0", B, [
        ("        if not bridge.clean_shutdown:\n            status = status or 1\n",
         "        if not bridge.clean_shutdown:\n            status = status\n")]),
    ("R5 clean flag always true", B, [
        ("        self.clean_shutdown = self.runtime.shutdown(deadline) is not False\n",
         "        self.runtime.shutdown(deadline)\n        self.clean_shutdown = True\n")]),
    ("R6 switch stop not waited", B, [
        ("        self._stop_playback(blocking=True)\n", "        self._stop_playback(blocking=False)\n")]),
    ("R7 no main excepthook", B, [
        ("    sys.excepthook = on_exception\n", "    pass\n")]),
    ("R8 verify retries", B, [
        ("\"timeout\": VERIFY_TIMEOUT, \"retry\": 0}", "\"timeout\": VERIFY_TIMEOUT, \"retry\": 5}")]),
    ("R9 replacing flag not reset in finally", B, [
        ("                self._local.replacing = None\n                with self._state_lock:\n",
         "                with self._state_lock:\n")]),
    ("R10 put-back unlocked", "player_window.py", [
        ("        # One lock with set_embedded_fullscreen (window_embed.fullscreen_lock)\n        with window_embed.fullscreen_lock:",
         "        # One lock with set_embedded_fullscreen (window_embed.fullscreen_lock)\n        if True:")]),
    ("R11 hold never released", B, [
        ("                    self._held.discard(token)\n", "                    pass\n")]),
    ("R12 window-only connect clears error", B, [
        ("        cfg = proto.validate_connect(args, dev=self.dev)\n",
         "        cfg = proto.validate_connect(args, dev=self.dev)\n        self.tracker.clear_error()\n")]),
    ("R13 STOP_WAIT loop gone", B, [
        ("        while pm._video is not None or pm._start_in_progress:\n", "        while False:\n")]),
    ("R14 deferred stop stops anything", B, [
        ("            if newer and p._video is not target:\n", "            if False:\n")]),
    ("R15 shutdown never waits for lock", B, [
        ("        if not pm._lock.acquire(timeout=left()):\n", "        if not pm._lock.acquire(blocking=False):\n")]),
    ("R16 report outside the player lock", B, [
        ("        with pm._lock:\n            outgoing = pm._video\n", "        if True:\n            outgoing = pm._video\n")]),
    ("Q1 play_file keeps the header", B, [("                pm._revoke_auth_header()\n", "                pass\n")]),
    ("Q2 no outgoing report", B, [("                outgoing = self._report_outgoing(pm, video)\n", "                outgoing = None\n")]),
    ("Q3 no surrogate fallback", "bridge_protocol.py", [('        data = text.encode("utf-16", "surrogatepass").decode(\n            "utf-16", "replace").encode("utf-8")', '        raise')]),
    ("Q4 publish never ends a late mpv", B, [('        if closing:\n            log.info("The backend is shutting down; ending', '        if False:\n            log.info("The backend is shutting down; ending')]),
    ("Q5 no thread excepthook", B, [("    threading.excepthook = on_thread_exception\n", "    pass\n")]),
    # --- round 2 ---
    ("N1 no supersede check after the report", B, [
        ("                abandoned = self._superseded_job()\n", "                abandoned = False\n")]),
    ("N2 committed start not waited for", B, [
        ("        if committed is not None:\n            deadline = time.monotonic() + COMMIT_WAIT\n",
         "        if False:\n            deadline = time.monotonic() + COMMIT_WAIT\n")]),
    ("N3 report lock wait unbounded", B, [
        ("            if not pm._tl_lock.acquire(timeout=left()):\n", "            if not pm._tl_lock.acquire():\n")]),
    ("N5 control join on its own clock", B, [
        ("            control.join(max(0.0, deadline - time.monotonic()))\n", "            control.join(5.0)\n")]),
    ("N6 report drain on its own clock", B, [
        ("        if reporter is not None and not reporter.drain(left()):\n",
         "        if reporter is not None and not reporter.drain(5.0):\n")]),
    ("N7 thread joins on their own clock", B, [
        ("                thread.join(max(0.0, deadline - time.monotonic()))\n", "                thread.join(15.0)\n")]),
    ("N8 blocking stop inline when free", B, [
        ("        pm.put_task(stop, pm)\n        deadline = time.monotonic() + STOP_WAIT\n",
         "        pm.run_action(stop)\n        deadline = time.monotonic() + STOP_WAIT\n")]),
    ("N9 old client not retired", B, [
        ("            self.runtime.retire_client(old)\n", "            pass\n")]),
    ("N10 second Stopped not filtered", B, [
        ("                if self._reported.has(video, session):\n", "                if False:\n")]),
    ("N11 second release not filtered", B, [
        ("            if self._released.has(video, session):\n", "            if False:\n")]),
    ("N12 cancelled replacement leaves the old item", B, [
        ("                    if cancelled or failed:\n", "                    if False:\n")]),
    ("N13 abandoned start leaves the old item", B, [
        ("                if outgoing is not None:\n                    self._end_reported(pm, outgoing)\n",
         "                if False:\n                    self._end_reported(pm, outgoing)\n")]),
    ("N14 finished callback mark outside the lock", B, [
        ("            with pm._lock:\n                if epoch is None or epoch == pm._play_epoch:\n",
         "            if True:\n                if epoch is None or epoch == pm._play_epoch:\n")]),
    ("N15 eof marked on a replaced item", B, [
        ("                    if video is not None and video not in self._replaced:\n",
         "                    if video is not None:\n")]),
    ("N16 survivor stays replaced", B, [
        ("                        self._replaced.discard(outgoing)\n", "                        pass\n")]),
    ("N17 transport lands on any item", B, [
        ("                    if expect() is None or p._video is not expect():\n", "                    if False:\n")]),
    ("N18 opening item not recorded", B, [
        ("                        owed[\"video\"] = video   # what its transport is for\n", "                        pass\n")]),
    ("N19 retry goes out as GET", H, [
        ("        return self._send(session, method, **kwargs)\n", "        return self._send(session, action, **kwargs)\n")]),
    ("N20 guard not installed", B, [
        ("        bridge_http.guard(client)\n", "        pass\n")]),
    ("N21 retired caller never let go", H, [
        ("            if time.monotonic() >= limit and not done.is_set():\n", "            if False:\n")]),
    ("N22 retired retries allowed", H, [
        ("        if attempt and self._retired.is_set():\n", "        if False:\n")]),
    ("N23 retired request unbounded", H, [
        ("                data[\"retry\"] = 0\n", "                pass\n")]),
    ("N24 exit grace not cut", B, [
        ("    exit_watchdog.GRACE_SECONDS = exit_grace(\n        bridge.shutdown_deadline, exit_watchdog.GRACE_SECONDS)\n", "    pass\n")]),
    ("N25 watchdog at the app's 20 s", B, [
        ("    exit_watchdog.arm(SHUTDOWN_BUDGET + EXIT_SLACK + 0.5)\n", "    exit_watchdog.arm()\n")]),
    ("N26 verifying client kept at shutdown", B, [
        ("            self.runtime.retire_client(verifying, 0)\n", "            pass\n")]),
    ("N27 STOP_WAIT ignores the shutdown", B, [
        ("            if time.monotonic() >= deadline or self._shutdown.is_set():\n",
         "            if time.monotonic() >= deadline:\n")]),
    ("N28 reported session not recorded", B, [
        ("                self._reported.add(video, session)\n", "                pass\n")]),
    ("N29 progress left on at shutdown", B, [
        ("                pm.should_send_timeline = False\n                if bound_ipc_replies is not None:\n",
         "                if bound_ipc_replies is not None:\n")]),
    ("N30 job generation leaks", B, [
        ("                self._local.job_gen = None\n", "                pass\n")]),
    # --- round 3: the re-reviewer's additions ---
    ("M1 deadline counted from stop() not the request", B, [
        ("            deadline = self._shutdown_at + SHUTDOWN_BUDGET\n",
         "            deadline = time.monotonic() + SHUTDOWN_BUDGET\n")]),
    ("M2 retired grace from the retire moment only", H, [
        ("            limit = max(started, self._retired_at) + self._grace\n",
         "            limit = self._retired_at + self._grace\n")]),
    ("M3 reported session recorded before the report", B, [
        ("                log.info(\"Replacing %s: reporting it stopped\",\n",
         "                self._reported.add(outgoing, _play_session(outgoing))\n                log.info(\"Replacing %s: reporting it stopped\",\n")]),
    ("M4 report lock not held across the final stop", B, [
        ("            if not pm._tl_lock.acquire(timeout=left()):\n",
         "            if not pm._tl_lock.acquire(timeout=left()) or pm._tl_lock.release() or not pm._tl_lock.acquire(timeout=left()):\n")]),
    ("M5 committed never cleared", B, [
        ("                    if self._committed is video:\n                        self._committed = None\n",
         "                    if self._committed is video:\n                        pass\n")]),
    ("M6 Stopped wrapper ignores options", B, [
        ("                session = _play_session(video, options)\n", "                session = _play_session(video)\n")]),
    ("M7 switch retires after the stop", B, [
        ("            self.runtime.retire_client(old)\n        # BRIDGE.md section 3: a new connect stops any playback first.\n        self._stop_playback(blocking=True)\n",
         "            pass\n        # BRIDGE.md section 3: a new connect stops any playback first.\n        self._stop_playback(blocking=True)\n        if old is not None:\n            self.runtime.retire_client(old)\n")]),
    ("M8 degraded path joins threads", B, [
        ("            try:\n                thread.halt = True\n                thread.trigger.set()\n            except Exception:\n                log.debug(\"could not halt %s\", name, exc_info=True)\n",
         "            try:\n                thread.halt = True\n                thread.trigger.set()\n                thread.join(15)\n            except Exception:\n                log.debug(\"could not halt %s\", name, exc_info=True)\n")]),
    ("M9 helper not a daemon", H, [
        ("        threading.Thread(target=work, name=\"bridge-http\", daemon=True).start()\n",
         "        threading.Thread(target=work, name=\"bridge-http\", daemon=False).start()\n")]),
    ("M10 retired timeout not capped", H, [
        ("                data[\"timeout\"] = min(data.get(\"timeout\") or RETIRED_TIMEOUT,\n                                      RETIRED_TIMEOUT)\n",
         "                pass\n")]),
    # --- round 3: each fix removed ---
    ("F1 stop command inline again", B, [
        ("        if not blocking:\n            pm.put_task(stop, pm)\n            return True\n",
         "        if not blocking:\n            pm.run_action(stop)\n            return True\n")]),
    ("F2a release checked against the Stopped record", B, [
        ("            if self._released.has(video, session):\n", "            if self._reported.has(video, session):\n")]),
    ("F2b already-reported outgoing not released", B, [
        ("            if not self._was_reported(outgoing):\n",
         "            reported_before = self._was_reported(outgoing)\n            if not reported_before:\n"),
        ("                pm.release_stream(outgoing)\n",
         "                if not reported_before:\n                    pm.release_stream(outgoing)\n")]),
    ("F3a play-loop error for a superseded job", B, [
        ("                    if stale:\n                        # A stop, a newer play or a switch came since, and\n",
         "                    if False:\n                        # A stop, a newer play or a switch came since, and\n")]),
    ("F3b play job error for a superseded job", B, [
        ("                if (not self._job_superseded()\n                        and not getattr(error, \"_bridge_reported\", False)):\n",
         "                if (True\n                        and not getattr(error, \"_bridge_reported\", False)):\n")]),
    ("F3c play job 'cannot be played' for a superseded job", B, [
        ("                if not started and not self._job_superseded():\n", "                if not started:\n")]),
    ("F3d hook error for a superseded start", B, [
        ("                    if stale:\n                        # Superseded since: a stop, a newer play or a switch\n",
         "                    if False:\n                        # Superseded since: a stop, a newer play or a switch\n")]),
    ("F4a ledger keyed on the session alone", B, [
        ("                return self._sessions.get(video) == session\n",
         "                return session in list(self._sessions.values())\n")]),
    ("F4b ledger held strongly", B, [
        ("        self._sessions = weakref.WeakKeyDictionary()\n", "        self._sessions = {}\n")]),
    ("F4c new play session keeps the old record", B, [
        ("                    self._reported.forget(video)\n", "                    pass\n")]),
    ("F5a final action not registered", B, [
        ("    exit_watchdog.set_final_action(runtime.end_mpv_process)\n", "    pass\n")]),
    ("F5b final action kills nothing", B, [
        ("            process.kill()\n", "            pass\n")]),
    ("F5c replies not bounded to what is left", B, [
        ("                if bound_ipc_replies is not None:\n                    self._bound_replies(bound_ipc_replies, deadline)\n",
         "                if bound_ipc_replies is not None:\n                    pass\n")]),
]


def run(tag):
    log = os.path.join(R2, "out", "mut3-%s.log" % tag)
    if os.path.exists(log):
        os.remove(log)      # hidden_run appends
    cmd = [PY, HR, "--log", log, "--timeout", "600", "--desktop", "bk3-mut", "--cwd", S,
           "--env", "APPDATA=" + os.path.join(ENV, "a"), "--env", "LOCALAPPDATA=" + os.path.join(ENV, "l"),
           "--env", "PATH=" + MPVD + r";C:\Windows\System32;C:\Windows",
           "--env", "PYTHONUTF8=1", "--",
           PY, "-m", "unittest", "discover", "-s", "tests", "-k", "test_bridge", "-k", "test_window_embed"]
    subprocess.run(cmd, capture_output=True, text=True, timeout=700)
    text = open(log, encoding="utf-8", errors="replace").read()
    summary = [l for l in text.splitlines() if l.startswith(("Ran ", "OK", "FAILED"))]
    failing = sorted(set(re.findall(r"^(?:FAIL|ERROR): (\S+) \((\S+)\)", text, re.M)))
    return summary, failing


os.makedirs(os.path.join(R2, "out"), exist_ok=True); os.makedirs(os.path.join(ENV, "a"), exist_ok=True); os.makedirs(os.path.join(ENV, "l"), exist_ok=True)
if os.path.exists(S):
    shutil.rmtree(S)
for d in ("jellyfin_mpv_shim", "tests", "tools"):
    shutil.copytree(os.path.join(SRC, d), os.path.join(S, d), ignore=shutil.ignore_patterns("__pycache__"))
only = sys.argv[1:]
base = run("base")
print("BASELINE", base[0], base[1][:3], flush=True)
caught = survived = skipped = 0
for name, f, edits in MUTS:
    if only and not any(name.startswith(o + " ") for o in only):
        continue
    path = os.path.join(P, f)
    src = open(path, encoding="utf-8", newline="").read()
    CR, LF = chr(13), chr(10)
    mutated = src
    okp = True
    for old, new in edits:
        if CR + LF in src:
            old = old.replace(LF, CR + LF); new = new.replace(LF, CR + LF)
        if mutated.count(old) != 1:
            print(name, "SKIP: pattern count", mutated.count(old), repr(old[:50]), flush=True)
            okp = False
            break
        mutated = mutated.replace(old, new)
    if not okp:
        skipped += 1
        continue
    if mutated == src:
        print(name, "SKIP: no-op", flush=True)
        skipped += 1
        continue
    open(path, "w", encoding="utf-8", newline="").write(mutated)
    try:
        summ, failing = run(name.split()[0])
        new_fails = sorted(set(failing) - set(base[1]))
        if new_fails:
            caught += 1
        else:
            survived += 1
        print("%-52s %s %s %s" % (name, "CAUGHT" if new_fails else "SURVIVED", summ,
                                  [t for t, _ in new_fails][:3]), flush=True)
    finally:
        open(path, "w", encoding="utf-8", newline="").write(src)
print("SCORE caught %d, survived %d, skipped %d" % (caught, survived, skipped), flush=True)
