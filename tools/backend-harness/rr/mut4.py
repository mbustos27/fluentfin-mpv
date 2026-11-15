"""Round-4 mutations on a scratch copy of backend/, tests run off-screen.

mut3.py's 73 (read from it, not imported: mut3 runs at import) with the
patterns the round-4 code moved brought up to date, the round-4 reviewer's
G1-G12, and the round-4 fixes removed one at a time (H*): each must be
caught -- or is listed in EQUIVALENT with the reason it cannot be.

    MUT_WORK=<a folder outside the repo>  (copies, logs, sandboxed APPDATA)
    mut4.py [--check] [prefix ...]

``--check`` only verifies that every pattern still matches exactly once.
"""
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
W = os.environ.get("MUT_WORK") or sys.exit(
    "set MUT_WORK to a work folder outside the repo (copies, logs, "
    "sandboxed APPDATA go there)")
SRC = r"C:\Users\moise\Dev\fluentfin-mpv\backend"
S = os.path.join(W, "mut")
PY = os.path.join(SRC, ".venv", "Scripts", "python.exe")
HR = r"C:\Users\moise\Dev\fluentfin-mpv\tools\backend-bench\hidden_run.py"
MPVD = os.path.expandvars(
    r"%LOCALAPPDATA%\Microsoft\WinGet\Packages"
    r"\mpv-player.mpv-CI.MSVC_Microsoft.Winget.Source_8wekyb3d8bbwe")
P = os.path.join(S, "jellyfin_mpv_shim")
ENV = os.path.join(W, "env-mut")
OUT = os.path.join(W, "out")

text = open(os.path.join(HERE, "mut3.py"), encoding="utf-8").read()
ns = {"os": os}
exec(text[text.index("B = \"bridge.py\""):text.index("\ndef run(")], ns)
B, H = "bridge.py", "bridge_http.py"
ST, PL = "bridge_state.py", "player.py"

#: mut3 entries whose code moved in round 4: same breakage, new pattern.
MOVED = {
    "R14": [("                    if newer and p._video is not target:\n",
             "                    if False:\n")],
    "M6": [("        return video, _play_session(video, options)\n",
            "        return video, _play_session(video)\n")],
    "F4c": [("                    self._reported.forget(video, session)\n",
             "                    pass\n")],
    "N19": [("        response = self._send(session, method, **kwargs)\n",
             "        response = self._send(session, action, **kwargs)\n")],
}
MUTS = []
for name, f, edits in ns["MUTS"]:
    tag = name.split()[0]
    MUTS.append((name, f, MOVED.get(tag, edits)))
assert len(MUTS) == 73, len(MUTS)

MUTS += [
    # --- round 4: the re-reviewer's additions (patterns at the round-4 code)
    ("G1 Stopped wrapper not under the report lock", B, [
        ("            with pm._tl_lock:\n                video, session = self._stop_subject(pm, options, stopped)\n",
         "            if True:\n                video, session = self._stop_subject(pm, options, stopped)\n")]),
    ("G2 bookkeeping report not matched by session", B, [
        ("                return session in list(self._sessions.values())\n", "                return False\n")]),
    ("G3 release never recorded", B, [
        ("            self._released.add(video, session)\n", "            pass\n")]),
    ("G4 restart keeps the release record", B, [
        ("                    self._released.forget(video, session)\n", "                    pass\n")]),
    ("G5 terminate replies not re-bounded", B, [
        ("            self._bound_replies(bound_ipc_replies, deadline)\n            self._run_steps([(\"player\", pm.terminate)])\n",
         "            self._run_steps([(\"player\", pm.terminate)])\n")]),
    ("G6 IPC floor zero", B, [
        ("IPC_FLOOR = 0.25\n", "IPC_FLOOR = 0.0\n")]),
    ("G7 end_mpv_process ignores poll", B, [
        ("            if process is None or process.poll() is not None:\n", "            if process is None:\n")]),
    ("G8 Stopped recorded before the report", B, [
        ("                result = original_stopped(finished, options, client, stopped)\n                self._reported.add(video, session)\n",
         "                self._reported.add(video, session)\n                result = original_stopped(finished, options, client, stopped)\n")]),
    ("G9 epoch read after the start", B, [
        ("            epoch = pm._play_epoch\n            # A restart",
         "            epoch = None\n            # A restart")]),
    ("G10 stop command not deferred, dropped", B, [
        ("        if not blocking:\n            pm.put_task(stop, pm)\n            return True\n",
         "        if not blocking:\n            return True\n")]),
    ("G11 forget on any epoch change", B, [
        ("                if pm._video is video and pm._play_epoch != epoch:\n",
         "                if pm._play_epoch != epoch:\n")]),
    ("G12 hook stale check off (play-loop only)", B, [
        ("                    stale = self._superseded_job()\n                if pm._video is video",
         "                    stale = False\n                if pm._video is video")]),
    # --- round 4: each fix removed ---
    # item 1: an answered stop's item is not playing for transport
    ("H1a next/previous read _video again", B, [
        ("        if self._playing(pm) is None:\n            raise BridgeError(NO_PLAYBACK, \"nothing is playing\")\n        self._queue_play(\"next\"",
         "        if pm._video is None:\n            raise BridgeError(NO_PLAYBACK, \"nothing is playing\")\n        self._queue_play(\"next\"")]),
    ("H1b track changes read _video again", B, [
        ("        if self._playing(pm) is None:\n            raise BridgeError(NO_PLAYBACK, \"nothing is playing\")\n        # set_streams may",
         "        if pm._video is None:\n            raise BridgeError(NO_PLAYBACK, \"nothing is playing\")\n        # set_streams may")]),
    ("H1c pause/seek read _video again", B, [
        ("        if self._playing(pm) is not None or self.dev_item is not None:\n",
         "        if pm._video is not None or self.dev_item is not None:\n")]),
    ("H1d stop never marks its item", B, [
        ("            self._stop_for = target\n", "            pass\n")]),
    ("H1e a cancelled start still counts as opening", B, [
        ("        return opening is None or opening not in self._cancelled\n",
         "        return True\n")]),
    ("H1f the mark outlives its stop", B, [
        ("                    if self._stop_for is target:\n                        self._stop_for = None\n",
         "                    if self._stop_for is target:\n                        pass\n")]),
    ("H1g volume after a stop goes to the stopped item", B, [
        ("        if self._playing(pm) is not None:\n            pm.run_action(lambda p: p.set_volume(volume))\n",
         "        if pm._video is not None:\n            pm.run_action(lambda p: p.set_volume(volume))\n")]),
    # item 2: the deferred stop's check under the player's lock
    ("H2 deferred stop checks outside the lock", B, [
        ("                with p._lock:\n                    with self._state_lock:\n                        newer = self._gen != gen\n",
         "                if True:\n                    with self._state_lock:\n                        newer = self._gen != gen\n")]),
    # item 3: a bookkeeping report keyed on its session
    ("H3a bookkeeping report matched against _video", B, [
        ("        if wanted is not None and _play_session(video) != wanted:\n            video = None\n",
         "        if wanted is not None and _play_session(video) != wanted:\n            pass\n")]),
    ("H3b bookkeeping report not recorded", B, [
        ("                    self._orphans.append(session)\n", "                    pass\n")]),
    ("H3c checks ignore the bookkeeping record", B, [
        ("            if session in self._orphans:\n                return True\n",
         "            if False:\n                return True\n")]),
    ("H3d a new session keeps the bookkeeping record", B, [
        ("            if session is not None and session in self._orphans:\n                self._orphans.remove(session)\n",
         "            if False:\n                self._orphans.remove(session)\n")]),
    # item 6: the nested exception tuples
    ("H6a volume save catches a nested tuple", PL, [
        ("        except (*_mpv_errors, TypeError):\n", "        except (_mpv_errors, TypeError):\n")]),
    ("H6b photo duration catches a nested tuple", PL, [
        ("            except (*_mpv_errors, ValueError, TypeError):\n",
         "            except (_mpv_errors, ValueError, TypeError):\n")]),
    # item 7: reported
    ("H7a outcome wrapper not installed", B, [
        ("        bridge_http.watch_stops(client, self._stop_outcome)\n", "        pass\n")]),
    ("H7b no exception counts as sent", H, [
        ("            if status is not None and 200 <= status < 300:\n", "            if True:\n")]),
    ("H7c status shared between threads", H, [
        ("        self._caller = threading.local()\n", "        self._caller = type(\"Shared\", (), {})()\n")]),
    ("H7d outcome written by the reporting thread", B, [
        ("            log.info(\"Play session of %s done (%s): %s\",\n",
         "            self._flush_reported()\n            log.info(\"Play session of %s done (%s): %s\",\n")]),
    ("H7e no sweep at shutdown", B, [
        ("        swept = self.tracker.sweep_sessions()\n", "        swept = 0\n")]),
    ("H7f last reported after the answer", B, [
        ("        self._flush_reported()\n        if self.shutdown_request_id is not None:\n            self.writer.respond(self.shutdown_request_id, {})\n",
         "        if self.shutdown_request_id is not None:\n            self.writer.respond(self.shutdown_request_id, {})\n        self._flush_reported()\n")]),
    ("H7g more than one outcome per session", ST, [
        ("        with self._lock:\n            if session in self._done:\n                return None\n",
         "        with self._lock:\n            if False:\n                return None\n")]),
    ("H7h never-opened sessions get nothing", B, [
        ("            self._session_done(item, now, bridge_state.NONE,\n",
         "            (lambda *a: None)(item, now, bridge_state.NONE,\n")]),
    ("H7i a restart leaves the old session open", B, [
        ("            self._session_done(item, before, bridge_state.FAILED,\n",
         "            (lambda *a: None)(item, before, bridge_state.FAILED,\n")]),
    ("H7j Playing not recorded when it is sent", B, [
        ("            self._session_phase(pm._video, bridge_state.PlaySessions.OPENED)\n            return original_initial(*args, **kwargs)\n",
         "            return original_initial(*args, **kwargs)\n")]),
    ("H7k a pending Stopped not recorded", B, [
        ("                if reporter is not None:\n                    # Its Stopped is on the report worker now",
         "                if False:\n                    # Its Stopped is on the report worker now")]),
    ("H7l reported before the tick's state", B, [
        ("        with self._emit_lock:\n            try:\n                events = [] if obs is None else self.tracker.update(obs)\n",
         "        with self._emit_lock:\n            self._send_reported()\n            try:\n                events = [] if obs is None else self.tracker.update(obs)\n")]),
    ("H7m sweep forgets the opening session", ST, [
        ("            outcome = NONE if phase == PlaySessions.SEEN else FAILED\n",
         "            if phase == PlaySessions.SEEN:\n                continue\n            outcome = FAILED\n")]),
    ("H7n states do not record what they showed", ST, [
        ("                self.sessions.note(snap[\"playSessionId\"], snap[\"itemId\"],\n                                   PlaySessions.SEEN)\n",
         "                pass\n")]),
]

#: Survivors that cannot be caught, with the reason (reported, not scored).
EQUIVALENT = {}


def run(tag):
    log = os.path.join(OUT, "mut4-%s.log" % tag)
    if os.path.exists(log):
        os.remove(log)      # hidden_run appends
    cmd = [PY, HR, "--log", log, "--timeout", "600", "--desktop", "bk4-mut",
           "--cwd", S,
           "--env", "APPDATA=" + os.path.join(ENV, "a"),
           "--env", "LOCALAPPDATA=" + os.path.join(ENV, "l"),
           "--env", "PATH=" + MPVD + r";C:\Windows\System32;C:\Windows",
           "--env", "PYTHONUTF8=1", "--",
           PY, "-m", "unittest", "discover", "-s", "tests",
           "-k", "test_bridge", "-k", "test_window_embed",
           "-k", "test_mpv_error_catch"]
    subprocess.run(cmd, capture_output=True, text=True, timeout=700)
    out = open(log, encoding="utf-8", errors="replace").read()
    summary = [l for l in out.splitlines()
               if l.startswith(("Ran ", "OK", "FAILED"))]
    failing = sorted(set(re.findall(r"^(?:FAIL|ERROR): (\S+) \((\S+)\)",
                                    out, re.M)))
    return summary, failing


def apply(src, edits):
    CR, LF = chr(13), chr(10)
    mutated = src
    for old, new in edits:
        if CR + LF in src:
            old = old.replace(LF, CR + LF)
            new = new.replace(LF, CR + LF)
        if mutated.count(old) != 1:
            return None, "pattern count %d %r" % (mutated.count(old),
                                                  old[:60])
        mutated = mutated.replace(old, new)
    if mutated == src:
        return None, "no-op"
    return mutated, None


def main():
    args = sys.argv[1:]
    check = "--check" in args
    only = [a for a in args if a != "--check"]
    chosen = [m for m in MUTS
              if not only or any(m[0].startswith(o + " ") for o in only)]
    if check:
        bad = 0
        for name, f, edits in chosen:
            path = os.path.join(SRC, "jellyfin_mpv_shim", f)
            _m, why = apply(open(path, encoding="utf-8", newline="").read(),
                            edits)
            if why:
                bad += 1
                print("%-52s %s" % (name, why))
        print("patterns: %d checked, %d stale" % (len(chosen), bad))
        return
    for d in (OUT, os.path.join(ENV, "a"), os.path.join(ENV, "l")):
        os.makedirs(d, exist_ok=True)
    if os.path.exists(S):
        shutil.rmtree(S)
    for d in ("jellyfin_mpv_shim", "tests", "tools"):
        shutil.copytree(os.path.join(SRC, d), os.path.join(S, d),
                        ignore=shutil.ignore_patterns("__pycache__"))
    base = run("base")
    print("BASELINE", base[0], base[1][:3], flush=True)
    caught = survived = skipped = 0
    for name, f, edits in chosen:
        path = os.path.join(P, f)
        src = open(path, encoding="utf-8", newline="").read()
        mutated, why = apply(src, edits)
        if why:
            print(name, "SKIP:", why, flush=True)
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
            print("%-52s %s %s %s%s" % (
                name, "CAUGHT" if new_fails else "SURVIVED", summ,
                [t for t, _ in new_fails][:3],
                "" if new_fails or name.split()[0] not in EQUIVALENT
                else "  equivalent: " + EQUIVALENT[name.split()[0]]),
                flush=True)
        finally:
            open(path, "w", encoding="utf-8", newline="").write(src)
    print("SCORE caught %d, survived %d, skipped %d" % (caught, survived,
                                                         skipped), flush=True)


main()
