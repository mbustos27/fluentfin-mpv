"""Round-7 and round-8 mutations on a scratch copy of backend/, tests run
off-screen.

mut6.py's 171 (read from it, not imported: mut6 runs at import) -- none of
their patterns moved in round 7 or 8 -- the round-7 fixes removed one at a
time (L*), and the round-8 fixes removed one at a time (P*: M is mut3's),
187 in all: each must be caught, or is listed in EQUIVALENT with the reason
it cannot be. Round 8 reversed one round-7 fix (the shutdown's held end now
runs after its stop, not before): L2c is now round 7's order put back, and
L2b's and L2d's patterns follow the lines round 8 changed under them.

    MUT_WORK=<a folder outside the repo>  (copies, logs, sandboxed APPDATA)
    MUT_SRC=<a backend copy to mutate>     (default: the repo's backend/)
    MUT_SHARD=<i>/<n>                      (optional: every n-th mutation from
                                            the i-th, 0-based; run n of these
                                            at once, each its own MUT_WORK)
    mut7.py [--check] [--round7 | --round8] [prefix ...]

``--check`` only verifies that every pattern still matches exactly once in
MUT_SRC. ``--round7`` runs only round 7's set: the L* mutations and the
earlier ones whose target lines round 7 changed (``TOUCHED7``). ``--round8``
runs only round 8's: the P* mutations, the L* round 8 changed
(``CHANGED8``) and the earlier ones whose target lines round 8 changed
(``TOUCHED8``). The tests
always run with the repo's venv python. Scoring as mut6: a run that never
finished counts as caught only when hidden_run killed it at its limit (exit
124) twice; any other run with no "Ran" line is a harness error, reported
and not scored.
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

text = open(os.path.join(HERE, "mut6.py"), encoding="utf-8").read()
ns = {"os": os, "HERE": HERE}
exec(text[text.index('text = open(os.path.join(HERE, "mut5.py")'):
          text.index("\n#: Survivors that cannot be caught")], ns)
exec(text[text.index("EQUIVALENT = dict("):text.index("\n\ndef run(tag)")],
     ns)
B = "bridge.py"
MUTS = list(ns["MUTS"])
assert len(MUTS) == 171, len(MUTS)

#: mut6 mutations whose target lines round 7 changed: run again with it.
TOUCHED7 = ("N29", "F5c", "G4", "K1e", "K1i", "K2b")
#: mut6 mutations whose target lines round 8 changed: none (only bridge.py's
#: held end changed, which mut6 does not reach); the L* it changed are
#: ``CHANGED8``.
TOUCHED8 = ()

MUTS += [
    # --- round 7: each fix removed ---
    # item 1: a held end does not spin the action thread
    ("L1a a held end leaves the progress reports on", B, [
        ("                        pm.should_send_timeline = False\n"
         "                        return None\n",
         "                        return None\n")]),
    ("L1b every hold logged", B, [
        ("        if not again:\n            log.info(\"Holding the end",
         "        if True:\n            log.info(\"Holding the end")]),
    # item 2: the end of the last item held at a stop or the shutdown
    ("L2a the shutdown's hook not installed", B, [
        ("        runtime.before_final_stop = (\n"
         "            lambda pm: self._end_held(pm, unload=False))\n", "")]),
    ("L2b the orderly stop does not run the hook", B, [
        ("                if held is not None:\n"
         "                    # After stop()",
         "                if False:\n"
         "                    # After stop()")]),
    # Round 8 reversed round 7's order: this puts round 7's back.
    ("L2c the hook runs before the stop (round 7's order)", B, [
        ("                    steps.append((\"held end\", lambda: held(pm)))\n",
         "                    steps.insert(0, (\"held end\", lambda: held(pm)))\n")]),
    ("L2d a play done during the shutdown drops the held end", B, [
        ("            if (held is None or self.pending is not None\n"
         "                    or self._shutdown.is_set() or self._superseded_job()):\n"
         "                return\n"
         "            self._deferred_finish = None\n",
         "            if (held is None or self.pending is not None\n"
         "                    or self._superseded_job()):\n"
         "                return\n"
         "            self._deferred_finish = None\n"
         "            if self._shutdown.is_set():\n"
         "                return\n")]),
    ("L2e the stop task does not end a held end", B, [
        ("                        self._end_held(p, target)\n",
         "                        pass\n")]),
    ("L2f a stop runs the held callback itself (the advance)", B, [
        ("        self.tracker.mark_ended(video, \"eof\")\n"
         "        pm.send_timeline_stopped(True)\n"
         "        if unload:\n"
         "            pm._unload_ended(video)\n"
         "        return True\n",
         "        pm.finished_callback(_has_lock, epoch)\n"
         "        return True\n")]),
    ("L2g ended whether or not it is still current", B, [
        ("        if pm._video is not video or pm._play_epoch != epoch:\n"
         "            return False\n"
         "        log.info(\"Ending %s: it reached its end",
         "        log.info(\"Ending %s: it reached its end")]),
    ("L2h a held end a stop ends is not marked eof", B, [
        ("        self.tracker.mark_ended(video, \"eof\")\n"
         "        pm.send_timeline_stopped(True)\n",
         "        pm.send_timeline_stopped(True)\n")]),
    ("L2i the shutdown's held end unloads too", B, [
        ("            lambda pm: self._end_held(pm, unload=False))\n",
         "            lambda pm: self._end_held(pm, unload=True))\n")]),
    ("L2j a held end at the shutdown not reported", B, [
        ("        pm.send_timeline_stopped(True)\n"
         "        if unload:\n",
         "        if unload:\n")]),
    # item 3: a failed quality change keeps the quality that played
    ("L3a the quality of a start that took not recorded", B, [
        ("                        self._quality[video] = getattr(video, \"trs_ovr\",\n"
         "                                                       None)\n",
         "                        pass\n")]),
    ("L3b the quality not put back", B, [
        ("            if video in self._quality:\n"
         "                video.trs_ovr = self._quality[video]\n",
         "            pass\n")]),
    # --- round 8: each fix removed ---
    # item 1 is L2c above (the order). Item 2: a play job superseded while
    # its item fetch finished leaves the held end to what superseded it.
    ("P2a a superseded play job still runs the held end", B, [
        ("                    or self._shutdown.is_set() or self._superseded_job()):\n",
         "                    or self._shutdown.is_set()):\n")]),
    ("P2b a superseded play job drops the held end", B, [
        ("                    or self._shutdown.is_set() or self._superseded_job()):\n"
         "                return\n"
         "            self._deferred_finish = None\n",
         "                    or self._shutdown.is_set()):\n"
         "                return\n"
         "            self._deferred_finish = None\n"
         "            if self._superseded_job():\n"
         "                return\n")]),
]

#: Round-7 mutations whose target round 8 changed, their patterns updated
#: to follow it: L2c (the order reversed: now round 7's order put back), L2b
#: (the comment it matches moved), L2d (the line it splits gained the
#: superseded check).
CHANGED8 = ("L2b", "L2c", "L2d")

#: Survivors that cannot be caught, with the reason (reported, not scored).
EQUIVALENT = dict(ns["EQUIVALENT"])


def run(tag):
    """One test run of the copy as it stands: ``(status, summary,
    failing)``; status is ``done`` (a "Ran" line), ``hung`` (hidden_run
    killed it at its limit: exit 124) or ``harness`` (neither)."""
    log = os.path.join(OUT, "mut7-%s.log" % tag)
    if os.path.exists(log):
        os.remove(log)      # hidden_run appends
    cmd = [PY, HR, "--log", log, "--timeout", str(RUN_LIMIT), "--desktop",
           "mut7-" + os.path.basename(os.path.normpath(W)),
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
    summary = [line for line in out.splitlines()
               if line.startswith(("Ran ", "OK", "FAILED"))]
    failing = sorted(set(re.findall(r"^(?:FAIL|ERROR): (\S+) \((\S+)\)",
                                    out, re.M)))
    if any(line.startswith("Ran ") for line in summary):
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
    round7 = "--round7" in args
    round8 = "--round8" in args
    only = [a for a in args if a not in ("--check", "--round7", "--round8")]
    chosen = [m for m in MUTS
              if not only or any(m[0].startswith(o + " ") for o in only)]
    if round7:
        chosen = [m for m in chosen if m[0].startswith("L")
                  or m[0].split()[0] in TOUCHED7]
    if round8:
        chosen = [m for m in chosen if m[0].startswith("P")
                  or m[0].split()[0] in CHANGED8 + TOUCHED8]
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
