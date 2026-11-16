"""Round-6 mutations on a scratch copy of backend/, tests run off-screen.

mut5.py's 143 (read from it, not imported: mut5 runs at import), with the
patterns round 6 moved brought up to date (MOVED6); the round-6 reviewer's
mutation (R6m1); and the round-6 fixes removed one at a time (K*): each must
be caught -- or is listed in EQUIVALENT with the reason it cannot be.

    MUT_WORK=<a folder outside the repo>  (copies, logs, sandboxed APPDATA)
    MUT_SRC=<a backend copy to mutate>     (default: the repo's backend/)
    MUT_SHARD=<i>/<n>                      (optional: every n-th mutation from
                                            the i-th, 0-based; run n of these
                                            at once, each its own MUT_WORK)
    mut6.py [--check] [prefix ...]

``--check`` only verifies that every pattern still matches exactly once in
MUT_SRC. The tests always run with the repo's venv python.

Scoring (round-6 item 6; mut5 counted any run without a "Ran" line as
caught): a run that never finished counts as **caught only when hidden_run
says it killed it** (``exit 124``, its timeout: the mutation hung a test),
and only after one re-run of that mutation hangs again; any other run with
no "Ran" line -- the harness itself failing -- is a **harness error**,
reported and not scored.
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
import os as _os  # noqa: E402
_REPO = _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..", ".."))  # the repository
REPO_BACKEND = _os.path.join(_REPO, "backend")
SRC = os.environ.get("MUT_SRC") or REPO_BACKEND
S = os.path.join(W, "mut")
PY = os.path.join(REPO_BACKEND, ".venv", "Scripts", "python.exe")
HR = _os.path.join(_REPO, "tools", "backend-bench", "hidden_run.py")
MPVD = os.path.expandvars(
    r"%LOCALAPPDATA%\Microsoft\WinGet\Packages"
    r"\mpv-player.mpv-CI.MSVC_Microsoft.Winget.Source_8wekyb3d8bbwe")
P = os.path.join(S, "jellyfin_mpv_shim")
ENV = os.path.join(W, "env-mut")
OUT = os.path.join(W, "out")
#: hidden_run's limit on one test run; a mutation that hangs a test is
#: killed there with exit 124.
RUN_LIMIT = 300

text = open(os.path.join(HERE, "mut5.py"), encoding="utf-8").read()
ns = {"os": os, "HERE": HERE}
exec(text[text.index('text = open(os.path.join(HERE, "mut4.py")'):
          text.index("\n#: Survivors that cannot be caught")], ns)
B, H = "bridge.py", "bridge_http.py"
ST, PR = "bridge_state.py", "player_reporting.py"
EQUIVALENT5 = {}
exec(text[text.index("EQUIVALENT = {"):text.index("\n\ndef run(tag)")],
     {}, EQUIVALENT5)

#: mut5 entries whose code moved in round 6: same breakage, new pattern.
MOVED6 = {
    # the restart's put-back is the whole snapshot now (`_put_back`)
    "J3a": [("        vars(video).update(restart)\n", "        pass\n")],
    "J3b": [("            pm.should_send_timeline = True\n        if interrupted or stale:\n",
             "            pass\n        if interrupted or stale:\n")],
    "J3c": [("        if not interrupted and sending and session == before:\n",
             "        if sending and session == before:\n")],
    "J3d": [("        if not interrupted and sending and session == before:\n",
             "        if not interrupted and sending:\n")],
    # the load record is per start and per thread now: a previous start's
    # load counting for this one is a record kept across starts
    "J3g": [("            start = {\"video\": video, \"loaded\": False}\n            self._local.start = start\n",
             "            start = getattr(self._local, \"kept\", None) or {\"video\": video, \"loaded\": False}\n            self._local.kept = start\n            self._local.start = start\n")],
}
MUTS = []
for name, f, edits in ns["MUTS"]:
    tag = name.split()[0]
    MUTS.append((name, f, MOVED6.get(tag, edits)))
assert len(MUTS) == 143, len(MUTS)

MUTS += [
    # --- round 6: the re-reviewer's (rr6/mut/m1) ---
    ("R6m1 reported writer never clears its wake-up", B, [
        ("            self._reported_due.clear()\n",
         "            pass  # MUT: never cleared\n")]),
    # --- round 6: each fix removed ---
    # item 1: a play owns what comes next
    ("K1a the end is never held", B, [
        ("                    if video is not None and self._defer_finish(\n",
         "                    if False and self._defer_finish(\n")]),
    ("K1b held only while opening, not while pending", B, [
        ("            if self.pending is None and (opening is None or opening is video):\n",
         "            if opening is None or opening is video:\n")]),
    ("K1c held only while pending, not while opening", B, [
        ("            if self.pending is None and (opening is None or opening is video):\n",
         "            if self.pending is None:\n")]),
    ("K1e the held end never runs", B, [
        ("        pm.put_task(pm.finished_callback, has_lock, epoch)\n",
         "        pass\n")]),
    ("K1f the play command does not run it", B, [
        ("                self._finish_deferred()\n", "")]),
    ("K1i a second end without the lock overwrites the first", B, [
        ("                has_lock = has_lock or held[1]\n",
         "                pass\n")]),
    # item 2: a restart that never got a usable stream
    ("K2a nothing put back", B, [
        ("        self._put_back(pm, video, restart)\n", "")]),
    ("K2b the tracks asked for are kept", B, [
        ("            video.aid, video.sid, video.explicit_tracks = tracks[1:]\n",
         "            pass\n")]),
    ("K2c set_streams not wrapped", B, [
        ("        pm.set_streams = set_streams\n", "")]),
    ("K2d mpv's auth header left as the failed start left it", B, [
        ("        if had == has:\n            return\n",
         "        if True:\n            return\n")]),
    ("K2e the abandoned session never settled", B, [
        ("        if (abandoned is not None and abandoned != session\n",
         "        if (False and abandoned is not None and abandoned != session\n")]),
    ("K2f no error event for the change", B, [
        ("        if interrupted or stale:\n            return\n        if raised is not None:\n",
         "        if True:\n            return\n        if raised is not None:\n")]),
    ("K2h a cancelled restart with a stream is put back too", B, [
        ("        if raised is None and interrupted and _negotiated_stream(video):\n            return\n",
         "")]),
    ("K2i a cancelled restart is always taken as having a stream", B, [
        ("    return (getattr(video, \"media_source\", None) is not None\n            and getattr(video, \"play_method\", None) is not None)\n",
         "    return True\n")]),
    ("K2m no snapshot taken", B, [
        ("            restart = (_video_state(video) if current is video and\n                       current is not None else None)\n",
         "            restart = None\n")]),
    ("K2n a restart's new session shown while it negotiates", ST, [
        ("                snap[\"playSessionId\"] = restart[1]\n",
         "                pass\n")]),
    ("K2o the restart negotiating is never recorded", B, [
        ("                    self._restart_shown = (video, before)\n",
         "                    pass\n")]),
    ("K2p the old session still shown once mpv has the load", B, [
        ("                if (self._restart_shown is not None\n                        and self._restart_shown[0] is start[\"video\"]):\n                    self._restart_shown = None\n",
         "                pass\n")]),
    ("K2l a report with no media source raises (stop a no-op)", PR, [
        ("            \"MediaSourceId\": (video.media_source or {}).get(\"Id\"),\n",
         "            \"MediaSourceId\": video.media_source[\"Id\"],\n")]),
    # item 3: a restart a play cancels ends replaced
    ("K3a the play does not mark what it replaces", B, [
        ("            self.tracker.mark_ended(pm._video, \"replaced\")\n        client, user_id",
         "            pass\n        client, user_id")]),
    # item 4: the load hooks are their own start's
    ("K4a a load start marks nothing", B, [
        ("            start[\"loaded\"] = True\n", "            pass\n")]),
    ("K4b a load start is opening's (the old attribution)", B, [
        ("            start = {\"video\": video, \"loaded\": False}\n            self._local.start = start\n",
         "            start = {\"video\": video, \"loaded\": False}\n            self._local.start = start\n            self.__dict__.setdefault(\"_by_video\", {})[id(video)] = start\n"),
        ("        start = getattr(self._local, \"start\", None)\n        if start is not None:\n            start[\"loaded\"] = True\n",
         "        start = self.__dict__.get(\"_by_video\", {}).get(id(self.opening))\n        if start is not None:\n            start[\"loaded\"] = True\n")]),
    ("K4c a load error is opening's (the old attribution)", B, [
        ("        start = getattr(self._local, \"start\", None)\n        if start is not None:\n            video = start[\"video\"]\n",
         "        start = self.opening if pm._start_in_progress else None\n        if start is not None:\n            video = start\n")]),
    # item 7: a PlaybackInfo that is not one
    ("K7a check_play_info not installed", B, [
        ("        bridge_http.check_play_info(client)\n", "")]),
    ("K7b a None PlaybackInfo passes", H, [
        ("        if isinstance(info, dict) and isinstance(info.get(\"MediaSources\"),\n                                                 list):\n            return info\n",
         "        if not isinstance(info, dict) or isinstance(\n                info.get(\"MediaSources\"), list):\n            return info\n")]),
    ("K7c the message is not shown", B, [
        ("        if isinstance(message, str) and message:\n            return proto.redact(\"%s: %s\" % (status, message))\n",
         "")]),
    ("K7d no media sources is refused too", H, [
        ("        if isinstance(info, dict) and isinstance(info.get(\"MediaSources\"),\n                                                 list):\n",
         "        if isinstance(info, dict) and info.get(\"MediaSources\"):\n")]),
]

#: Survivors that cannot be caught, with the reason (reported, not scored).
EQUIVALENT = dict(EQUIVALENT5["EQUIVALENT"])
EQUIVALENT["J3d"] = (
    "round 6: a restart that found no stream has its session put back "
    "(`_put_back`) from the copy taken where `before` is read, from the "
    "same video, so `session == before` always holds on that line; the "
    "restart that did get a new session returns before it (`reached`, or "
    "cancelled with a stream in hand)")


def run(tag):
    """One test run of the copy as it stands: ``(status, summary,
    failing)``; status is ``done`` (a "Ran" line), ``hung`` (hidden_run
    killed it at its limit: exit 124) or ``harness`` (neither)."""
    log = os.path.join(OUT, "mut6-%s.log" % tag)
    if os.path.exists(log):
        os.remove(log)      # hidden_run appends
    cmd = [PY, HR, "--log", log, "--timeout", str(RUN_LIMIT), "--desktop",
           "mut6-" + os.path.basename(os.path.normpath(W)),
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
