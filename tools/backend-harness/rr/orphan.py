import json, os, sys, time
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from rrprobe import BridgeProcess, PY, OUT, mpv_procs, kill_pids, log
for mode in ("eof", "shutdown"):
    for delay in (0.05, 0.15, 0.3, 0.6, 1.0):
        b = BridgeProcess(PY, OUT, "rr-orphan-%s-%s" % (mode, delay), dev=True)
        try:
            b.wait_event("hello", timeout=30)
            b.send_raw((json.dumps({"id": 1, "cmd": "connect", "args": {"protocol": 1, "window": {"mode": "standalone"}}}) + "\n").encode())
            time.sleep(delay)
            if mode == "eof":
                b.proc.stdin.close()
            else:
                b.send_raw(b'{"id": 2, "cmd": "shutdown"}\n')
            t0 = time.time(); code = b.finish(60)
            resp = [(m.get("id"), m.get("ok"), (m.get("error") or {}).get("code")) for m in b.messages if "id" in m]
            time.sleep(3)
            log(mode, delay, "exit", code, round(time.time() - t0 - 3, 2), "responses", resp, "mpv left", mpv_procs(b.config_dir), "not_json", b.not_json[:1])
        finally:
            kill_pids(mpv_procs(b.config_dir)); b.kill()
