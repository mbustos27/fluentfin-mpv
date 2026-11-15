"""Round-5 mutations on a scratch copy of backend/, tests run off-screen.

mut4.py's 113 (read from it, not imported: mut4 runs at import), with the
patterns round 5 moved brought up to date (MOVED5); the round-5 reviewer's
R5*; and the round-5 fixes removed one at a time (J*): each must be caught
-- or is listed in EQUIVALENT with the reason it cannot be.

    MUT_WORK=<a folder outside the repo>  (copies, logs, sandboxed APPDATA)
    MUT_SRC=<a backend copy to mutate>     (default: the repo's backend/)
    MUT_SHARD=<i>/<n>                      (optional: every n-th mutation from
                                            the i-th, 0-based; run n of these
                                            at once, each its own MUT_WORK)
    mut5.py [--check] [prefix ...]

``--check`` only verifies that every pattern still matches exactly once in
MUT_SRC. The tests always run with the repo's venv python.

Scored as mut6.py scores (round 6): a run with no "Ran" line is caught only
when hidden_run killed it at its limit (exit 124) twice -- it is re-run once
-- and is otherwise a harness error, reported and not scored. Until round 6
any run without a "Ran" line counted as caught.
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
REPO_BACKEND = r"C:\Users\moise\Dev\fluentfin-mpv\backend"
SRC = os.environ.get("MUT_SRC") or REPO_BACKEND
S = os.path.join(W, "mut")
PY = os.path.join(REPO_BACKEND, ".venv", "Scripts", "python.exe")
HR = r"C:\Users\moise\Dev\fluentfin-mpv\tools\backend-bench\hidden_run.py"
MPVD = os.path.expandvars(
    r"%LOCALAPPDATA%\Microsoft\WinGet\Packages"
    r"\mpv-player.mpv-CI.MSVC_Microsoft.Winget.Source_8wekyb3d8bbwe")
P = os.path.join(S, "jellyfin_mpv_shim")
ENV = os.path.join(W, "env-mut")
OUT = os.path.join(W, "out")
#: hidden_run's limit on one test run (mut6.py: scoring).
RUN_LIMIT = 300

text = open(os.path.join(HERE, "mut4.py"), encoding="utf-8").read()
ns = {"os": os, "HERE": HERE}
exec(text[text.index('text = open(os.path.join(HERE, "mut3.py")'):
          text.index("\n#: Survivors that cannot be caught")], ns)
B, H = "bridge.py", "bridge_http.py"
ST, PL = "bridge_state.py", "player.py"

#: mut4 entries whose code moved in round 5: same breakage, new pattern.
MOVED5 = {
    # the shutdown's flush is now the final one
    "H7f": [("        self._flush_reported(final=True)\n        if self.shutdown_request_id is not None:\n            self.writer.respond(self.shutdown_request_id, {})\n",
             "        if self.shutdown_request_id is not None:\n            self.writer.respond(self.shutdown_request_id, {})\n        self._flush_reported(final=True)\n")],
}
MUTS = []
for name, f, edits in ns["MUTS"]:
    tag = name.split()[0]
    MUTS.append((name, f, MOVED5.get(tag, edits)))
assert len(MUTS) == 113, len(MUTS)

#: The hook's error block, moved by J2a back to after `opening` is cleared.
_ERROR_BLOCK = (
    "                if (pm._video is not video and not cancelled and not failed\n"
    "                        and video is not None):\n"
    "                    # A start that did not end up playing and said nothing\n"
    "                    # on the way out: no URL, or the negotiation raised.\n"
    "                    # Before `opening` goes: with nothing left playing, a\n"
    "                    # sample in between read \"idle\" -- and a UI leaves the\n"
    "                    # player page on that -- before the \"error\".\n"
    "                    if raised is not None:\n"
    "                        code = classify_exception(raised)\n"
    "                        message = describe_exception(raised)\n"
    "                        raised._bridge_reported = True\n"
    "                    else:\n"
    "                        code, message = MPV_FAILED, \"could not start playback\"\n"
    "                    if stale:\n"
    "                        # Superseded since: a stop, a newer play or a switch\n"
    "                        # (whose retirement is what made this request fail)\n"
    "                        # has cleared the error, and this one is not the\n"
    "                        # UI's any more.\n"
    "                        log.info(\"Not reporting %s failing: superseded\",\n"
    "                                 getattr(video, \"item_id\", \"a start\"))\n"
    "                    else:\n"
    "                        self.tracker.report_error(\n"
    "                            code, message,\n"
    "                            item_id=getattr(video, \"item_id\", None))\n")
_SETTLE = ("                self._settle_sessions(pm, video, before)\n"
           "                self.poke()\n\n"
           "        def finished_callback(")

MUTS += [
    # --- round 5: the re-reviewer's (rr5/probe/mut5.py), patterns at the
    # round-5 code
    ("R5a sweep before the final report", B, [
        ("        self.clean_shutdown = self.runtime.shutdown(deadline) is not False\n",
         "        self.tracker.sweep_sessions()\n        self.clean_shutdown = self.runtime.shutdown(deadline) is not False\n")]),
    ("R5b deferred stop clears any stop mark", B, [
        ("                    if self._stop_for is target:\n                        self._stop_for = None\n",
         "                    if True:\n                        self._stop_for = None\n")]),
    ("R5d restart check without before!=now", B, [
        ("        if (before is not None and before != now\n",
         "        if (before is not None\n")]),
    ("R5j pending no longer counts as opening", B, [
        ("            if self.pending is not None:\n                return True\n            opening = self.opening\n",
         "            opening = self.opening\n")]),
    ("R5r cancelled starts not settled", B, [
        ("                self._settle_sessions(pm, video, before)\n",
         "                if not cancelled:\n                    self._settle_sessions(pm, video, before)\n")]),
    ("R5p no flush at shutdown", B, [
        ("        self._flush_reported(final=True)\n        if self.shutdown_request_id is not None:\n",
         "        if self.shutdown_request_id is not None:\n")]),
    ("R5x settle only for the opening video's failed start", B, [
        ("        if (now is not None and pm._video is not video\n",
         "        if (now is not None and pm._video is not video and pm._video is None\n")]),
    # --- round 5: each fix removed ---
    # item 1: the item before a next/previous/advance that did not take
    ("J1a item before a failed next stays current", B, [
        ("                elif (not owed and current is not None and current is not video\n",
         "                elif (False and current is not None and current is not video\n")]),
    ("J1b only mpv failures end it (the reviewer's narrower fix)", B, [
        ("                elif (not owed and current is not None and current is not video\n",
         "                elif ((cancelled or failed) and not owed and current is not None and current is not video\n")]),
    ("J1c the item ended whether or not it was reported", B, [
        ("                        and pm._video is current\n                        and self._was_reported(current)):\n",
         "                        and pm._video is current):\n")]),
    ("J1d a play command's start takes this path too", B, [
        ("                elif (not owed and current is not None and current is not video\n",
         "                elif (current is not None and current is not video\n")]),
    ("J1e the hook ends the item during the shutdown too", B, [
        ("        if self._shutdown.is_set():\n            return\n        with pm._lock:\n            if pm._video is not outgoing:\n",
         "        with pm._lock:\n            if pm._video is not outgoing:\n")]),
    ("J2a the error set after opening is cleared", B, [
        (_ERROR_BLOCK, ""),
        (_SETTLE, "                self._settle_sessions(pm, video, before)\n"
                  + _ERROR_BLOCK + "                self.poke()\n\n"
                  "        def finished_callback(")]),
    # item 2 (and the restart that reached mpv)
    ("J3a restart: the failed request's None stays", B, [
        ("            video.playback_info = info\n", "            pass\n")]),
    ("J3b restart: progress reports stay off", B, [
        ("            pm.should_send_timeline = True\n\n    def _on_load_start",
         "            pass\n\n    def _on_load_start")]),
    ("J3c restart: progress back on though interrupted", B, [
        ("        if not interrupted and sending and _play_session(video) == before:\n",
         "        if sending and _play_session(video) == before:\n")]),
    ("J3d restart: progress back on for a new session", B, [
        ("        if not interrupted and sending and _play_session(video) == before:\n",
         "        if not interrupted and sending:\n")]),
    ("J3e restart that reached mpv stays current", B, [
        ("        if reached:\n            self._end_reported(pm, video,",
         "        if False:\n            self._end_reported(pm, video,")]),
    ("J3f on_load_start not installed", B, [
        ("        pm.on_load_start = self._on_load_start\n", "")]),
    ("J3g a previous load of the video still counts", B, [
        ("            self._loaded.discard(video)\n            try:\n                return original_play(",
         "            try:\n                return original_play(")]),
    ("J3i set_streams reads the video its restart ended", PL, [
        ("        current = self._video\n        if current is not None:\n            self._capture_track_memory(current)\n",
         "        self._capture_track_memory(self._video)\n")]),
    ("J3h same-id restart that reached mpv gets no outcome", B, [
        ("            if (pm._video is not video and session is not None\n                    and session == before):\n",
         "            if False:\n")]),
    # item 3: reported without the monitor's tick
    ("J4a outcome queued, writer not woken", B, [
        ("            self.poke()\n            self._reported_due.set()\n",
         "            self.poke()\n")]),
    ("J4b writer never writes", B, [
        ("            try:\n                with self._emit_lock:\n                    self._send_reported()\n            except Exception:\n                log.exception(\"could not write the reported events\")\n",
         "            pass\n")]),
    ("J4c writer does not wait for the monitor", B, [
        ("                    timeout=REPORTED_WAIT)\n", "                    timeout=0)\n")]),
    ("J4d writes after the last flush", B, [
        ("        if self._reported_closed:\n            return\n        for data in self.tracker.drain_reported():\n",
         "        for data in self.tracker.drain_reported():\n")]),
    ("J4e writer outside the emit lock", B, [
        ("            try:\n                with self._emit_lock:\n                    self._send_reported()\n            except Exception:\n                log.exception(\"could not write the reported events\")\n",
         "            try:\n                self._send_reported()\n            except Exception:\n                log.exception(\"could not write the reported events\")\n")]),
    ("J4f the shutdown's flush is not the last", B, [
        ("        self._flush_reported(final=True)\n        if self.shutdown_request_id is not None:\n",
         "        self._flush_reported()\n        if self.shutdown_request_id is not None:\n")]),
    ("J4g writer thread not started", B, [
        ("        self._spawn(\"bridge-reported\", self._reported_loop)\n", "")]),
    ("J4h REPORTED_WAIT 30 s", B, [
        ("REPORTED_WAIT = 1.0\n", "REPORTED_WAIT = 30.0\n")]),
]

#: Survivors that cannot be caught, with the reason (reported, not scored).
EQUIVALENT = {
    "J1d": "a play command's start reaches that branch only when "
           "`_report_outgoing` returned None: nothing was playing (current "
           "is None) or its report raised (current not reported, which "
           "`_was_reported` excludes) -- `not owed` adds nothing reachable",
}


def run(tag):
    """One test run of the copy as it stands: ``(status, summary,
    failing)``; status is ``done`` (a "Ran" line), ``hung`` (hidden_run
    killed it at its limit: exit 124) or ``harness`` (neither)."""
    log = os.path.join(OUT, "mut5-%s.log" % tag)
    if os.path.exists(log):
        os.remove(log)      # hidden_run appends
    cmd = [PY, HR, "--log", log, "--timeout", str(RUN_LIMIT), "--desktop",
           "mut5-" + os.path.basename(os.path.normpath(W)),
           "--cwd", S,
           "--env", "APPDATA=" + os.path.join(ENV, "a"),
           "--env", "LOCALAPPDATA=" + os.path.join(ENV, "l"),
           "--env", "PATH=" + MPVD + r";C:\Windows\System32;C:\Windows",
           "--env", "PYTHONUTF8=1", "--",
           PY, "-m", "unittest", "discover", "-s", "tests",
           "-k", "test_bridge", "-k", "test_window_embed",
           "-k", "test_mpv_error_catch"]
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True,
                              timeout=RUN_LIMIT + 120)
        said = proc.stdout or ""
    except subprocess.TimeoutExpired:
        said = ""
    try:
        out = open(log, encoding="utf-8", errors="replace").read()
    except OSError:
        out = ""
    summary = [l for l in out.splitlines()
               if l.startswith(("Ran ", "OK", "FAILED"))]
    failing = sorted(set(re.findall(r"^(?:FAIL|ERROR): (\S+) \((\S+)\)",
                                    out, re.M)))
    if any(l.startswith("Ran ") for l in summary):
        return "done", summary, failing
    exit_ = re.search(r"hidden_run: exit (-?\d+)", said)
    if exit_ and exit_.group(1) == "124":
        return "hung", ["killed at %d s (exit 124)" % RUN_LIMIT], failing
    return "harness", ["no Ran line; hidden_run said %r" % said.strip()[-200:]
                       ], failing


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
    shard = os.environ.get("MUT_SHARD")
    if shard:
        i, n = (int(x) for x in shard.split("/"))
        chosen = chosen[i::n]
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
    status, summary, base_failing = run("base")
    print("BASELINE", status, summary, base_failing[:3], flush=True)
    if status != "done":
        sys.exit("the unmutated copy did not run to the end: nothing to "
                 "score against")
    caught = survived = skipped = equivalent = harness = 0
    errors = []
    for name, f, edits in chosen:
        path = os.path.join(P, f)
        src = open(path, encoding="utf-8", newline="").read()
        mutated, why = apply(src, edits)
        if why:
            print(name, "SKIP:", why, flush=True)
            skipped += 1
            continue
        open(path, "w", encoding="utf-8", newline="").write(mutated)
        tag = name.split()[0]
        try:
            status, summary, failing = run(tag)
            if status == "hung":
                # Once more before it counts: a run killed at the limit on a
                # loaded machine is not yet a hang.
                print("%-52s hung once, re-running" % name, flush=True)
                status, summary, failing = run(tag + "-again")
                if status == "hung":
                    summary = ["hung twice (exit 124 both times)"]
            if status == "harness":
                harness += 1
                errors.append((name, summary))
                print("%-52s HARNESS ERROR %s" % (name, summary), flush=True)
                continue
            new_fails = sorted(set(failing) - set(base_failing))
            hit = status == "hung" or bool(new_fails)
            if hit:
                caught += 1
            elif tag in EQUIVALENT:
                equivalent += 1
            else:
                survived += 1
            print("%-52s %s %s %s%s" % (
                name, "CAUGHT" if hit else "SURVIVED", summary,
                [t for t, _ in new_fails][:3],
                "" if hit or tag not in EQUIVALENT
                else "  equivalent: " + EQUIVALENT[tag]),
                flush=True)
        finally:
            open(path, "w", encoding="utf-8", newline="").write(src)
    print("SCORE caught %d, survived %d (not equivalent), equivalent %d, "
          "skipped %d; harness errors %d (not scored)" % (
              caught, survived, equivalent, skipped, harness), flush=True)
    for name, summary in errors:
        print("HARNESS ERROR", name, summary, flush=True)


main()
