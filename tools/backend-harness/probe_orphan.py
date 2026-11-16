import os, sys, time, json
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import os as _os  # noqa: E402
_REPO = _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", ".."))  # the repository
sys.path.insert(0, _os.path.join(_REPO, "backend", "tools"))
from bridge_smoke import BridgeProcess
from probe import PY, OUT, mpv_procs, kill_pids

delay = float(sys.argv[1]) if len(sys.argv) > 1 else 0.3
mode = sys.argv[2] if len(sys.argv) > 2 else "eof"
b = BridgeProcess(PY, OUT, "orphan-%s-%s" % (mode, delay), dev=True)
try:
    b.wait_event("hello", timeout=30)
    msg = {"id": 1, "cmd": "connect", "args": {"protocol": 1, "window": {"mode": "standalone"}}}
    b.send_raw((json.dumps(msg) + "\n").encode())
    time.sleep(delay)
    if mode == "eof":
        b.proc.stdin.close()
    else:
        b.send_raw(b'{"id": 2, "cmd": "shutdown"}\n')
    t0 = time.time(); code = b.finish(60)
    print("exit", code, round(time.time() - t0, 2))
    print("responses", [m for m in b.messages if "id" in m])
    time.sleep(4)
    left = mpv_procs(b.config_dir)
    print("mpv left after bridge exit:", left)
finally:
    kill_pids(mpv_procs(b.config_dir)); b.kill()
