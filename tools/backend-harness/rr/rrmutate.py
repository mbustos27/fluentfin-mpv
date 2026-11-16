"""Re-review mutations on a scratch copy of backend/, tests run off-screen via hidden_run."""
import os, re, shutil, subprocess, sys
RR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
import os as _os  # noqa: E402
_REPO = _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..", ".."))  # the repository
SRC = _os.path.join(_REPO, "backend")
S = os.path.join(RR, "mut")
PY = os.path.join(SRC, ".venv", "Scripts", "python.exe")
HR = _os.path.join(_REPO, "tools", "backend-bench", "hidden_run.py")
MPVD = _os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WinGet\Packages\mpv-player.mpv-CI.MSVC_Microsoft.Winget.Source_8wekyb3d8bbwe")
P = os.path.join(S, "jellyfin_mpv_shim")
ENV = os.path.join(RR, "env-mut")

MUTS = [
    ("R1 report before the item fetch", "bridge.py", [
        ("                self._report_outgoing(pm, video)\n", "                pass\n"),
        ("            try:\n                started = self.runtime.start_playback(client, user_id,\n",
         "            try:\n                self._report_outgoing(self.runtime.pm, None)\n                started = self.runtime.start_playback(client, user_id,\n")]),
    ("R2 outgoing stream not released", "bridge.py", [
        ("                pm.release_stream(outgoing)\n", "                pass\n")]),
    ("R3 degraded shutdown leaves mpv", "bridge.py", [
        ("        self._run_steps([(\"mpv\", lambda: self._end_mpv_unlocked(pm))])\n", "        pass\n")]),
    ("R4 unclean shutdown exits 0", "bridge.py", [
        ("        if not bridge.clean_shutdown:\n            status = status or 1\n",
         "        if not bridge.clean_shutdown:\n            status = status\n")]),
    ("R5 clean flag always true", "bridge.py", [
        ("        self.clean_shutdown = self.runtime.shutdown() is not False\n",
         "        self.runtime.shutdown()\n        self.clean_shutdown = True\n")]),
    ("R6 switch stop not waited", "bridge.py", [
        ("        self._stop_playback(blocking=True)\n", "        self._stop_playback(blocking=False)\n")]),
    ("R7 no main excepthook", "bridge.py", [
        ("    sys.excepthook = on_exception\n", "    pass\n")]),
    ("R8 verify retries", "bridge.py", [
        ("\"timeout\": VERIFY_TIMEOUT, \"retry\": 0}", "\"timeout\": VERIFY_TIMEOUT, \"retry\": 5}")]),
    ("R9 replacing flag not reset in finally", "bridge.py", [
        ("            finally:\n                self._local.replacing = False\n",
         "            finally:\n                pass\n")]),
    ("R10 put-back unlocked", "player_window.py", [
        ("        # One lock with set_embedded_fullscreen (window_embed.fullscreen_lock)\n        with window_embed.fullscreen_lock:",
         "        # One lock with set_embedded_fullscreen (window_embed.fullscreen_lock)\n        if True:")]),
    ("R11 hold never released", "bridge.py", [
        ("                    self._held.discard(token)\n", "                    pass\n")]),
    ("R12 window-only connect clears error", "bridge.py", [
        ("        cfg = proto.validate_connect(args, dev=self.dev)\n",
         "        cfg = proto.validate_connect(args, dev=self.dev)\n        self.tracker.clear_error()\n")]),
    ("R13 STOP_WAIT loop gone", "bridge.py", [
        ("        while pm._video is not None or pm._start_in_progress:\n",
         "        while False:\n")]),
    ("R14 deferred stop stops anything", "bridge.py", [
        ("            if newer and p._video is not target:\n", "            if False:\n")]),
    ("R15 shutdown never waits for lock", "bridge.py", [
        ("        if pm._lock.acquire(timeout=wait):\n", "        if pm._lock.acquire(blocking=False):\n")]),
    ("R16 report outside the player lock", "bridge.py", [
        ("        with pm._lock:\n            outgoing = pm._video\n", "        if True:\n            outgoing = pm._video\n")]),
]
MUTS += [('Q1 play_file keeps the header', 'bridge.py', [('                pm._revoke_auth_header()\n', '                pass\n')]), ('Q2 no outgoing report', 'bridge.py', [('                self._report_outgoing(pm, video)\n', '                pass\n')]), ('Q3 no surrogate fallback', 'bridge_protocol.py', [('        data = text.encode("utf-16", "surrogatepass").decode(\n            "utf-16", "replace").encode("utf-8")', '        raise')]), ('Q4 publish never ends a late mpv', 'bridge.py', [('        if closing:\n            log.info("The backend is shutting down; ending', '        if False:\n            log.info("The backend is shutting down; ending')]), ('Q5 no thread excepthook', 'bridge.py', [('    threading.excepthook = on_thread_exception\n', '    pass\n')])]


def run(tag):
    log = os.path.join(RR, "out", "mut-%s.log" % tag)
    cmd = [PY, HR, "--log", log, "--timeout", "600", "--desktop", "rr-mut", "--cwd", S,
           "--env", "APPDATA=" + os.path.join(ENV, "a"), "--env", "LOCALAPPDATA=" + os.path.join(ENV, "l"),
           "--env", "PATH=" + MPVD + r";C:\Windows\System32;C:\Windows",
           "--env", "PYTHONUTF8=1", "--",
           PY, "-m", "unittest", "discover", "-s", "tests", "-k", "test_bridge", "-k", "test_window_embed"]
    subprocess.run(cmd, capture_output=True, text=True, timeout=700)
    text = open(log, encoding="utf-8", errors="replace").read()
    summary = [l for l in text.splitlines() if l.startswith(("Ran ", "OK", "FAILED"))]
    failing = sorted(set(re.findall(r"^(?:FAIL|ERROR): (\S+) \((\S+)\)", text, re.M)))
    return summary, failing


os.makedirs(os.path.join(ENV, "a"), exist_ok=True); os.makedirs(os.path.join(ENV, "l"), exist_ok=True)
if os.path.exists(S):
    shutil.rmtree(S)
for d in ("jellyfin_mpv_shim", "tests", "tools"):
    shutil.copytree(os.path.join(SRC, d), os.path.join(S, d), ignore=shutil.ignore_patterns("__pycache__"))
base = run("base")
print("BASELINE", base[0], base[1][:3], flush=True)
only = sys.argv[1:]
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
        continue
    open(path, "w", encoding="utf-8", newline="").write(mutated)
    try:
        summ, failing = run(name.split()[0])
        new_fails = sorted(set(failing) - set(base[1]))
        print("%-42s %s %s %s" % (name, "CAUGHT" if new_fails else "SURVIVED", summ,
                                  [t for t, _ in new_fails][:3]), flush=True)
    finally:
        open(path, "w", encoding="utf-8", newline="").write(src)
