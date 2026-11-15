"""mpv (external backend) startup latency and IPC round-trip cost.

Run it off-screen (it opens real mpv windows):

    python hidden_run.py --log out.txt --env PATH=<mpv dir>;%PATH% -- ^
        python bench_mpv_start.py --shim-root <shim checkout> --runs 7 --json out.json

What it measures, per option variant (interleaved, median of --runs):

* spawn -> pipe: Popen until the named pipe answers WaitNamedPipe (polled 1 ms).
* spawn -> ready: until the first IPC reply (get_property mpv-version).
* loadfile -> file-loaded / playback-restart: a lavfi test source
  (av://lavfi:testsrc=size=1280x720:rate=30), i.e. no disk or network.

Variants are built from the shim's OWN option builder
(jellyfin_mpv_shim.mpv_options.build_mpv_options + mpv_scripts, which import
neither player.py nor a backend -- STARTHERE), converted to argv exactly the
way python-mpv-jsonipc's MPVProcess does it.

Also: raw IPC round trip (get_property volume, 300x) on a bare pipe vs the same
read through python_mpv_jsonipc.MPV attached to the running mpv -- the
per-property-read cost every `self._player.<prop>` pays on this backend.
"""

import argparse
import json
import os
import random
import statistics
import subprocess
import sys
import tempfile
import time
import ctypes

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.WaitNamedPipeW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]

TESTSRC = "av://lavfi:testsrc=size=1280x720:rate=30"


def mpv_fmt(v):
    if isinstance(v, bool):
        return "yes" if v else "no"
    return str(v)


JSONIPC_ONLY = {"start_mpv", "ipc_socket", "mpv_location", "start_retries",
                "start_retry_delay_ms", "discard_output", "log_handler",
                "loglevel", "quit_callback", "diagnose_start_failures"}


def to_argv(opts):
    out = []
    for k, v in opts.items():
        if k in JSONIPC_ONLY:
            continue
        vals = v if isinstance(v, list) else [v]
        for one in vals:
            out.append("--%s=%s" % (k.replace("_", "-"), mpv_fmt(one)))
    return out


def shim_options(shim_root, cfg, osc_style, scripts=True, force_window=None):
    """The shim's own construction options, from its own builder, in a child
    process so its argv/config import side effects stay there."""
    code = r"""
import json, sys
sys.argv = [sys.argv[0], "--config", %r]
sys.path.insert(0, %r)
from jellyfin_mpv_shim.conf import settings
from jellyfin_mpv_shim import mpv_options as mo
style = %r
opts = mo.build_mpv_options(style, mo.mpv_scripts(style, True), True, True)
# player._init_mpv adds these as constructor kwargs
opts["input_default_bindings"] = True
opts["input_vo_keyboard"] = True
opts["input_media_keys"] = settings.media_keys
print(json.dumps(opts))
""" % (cfg, shim_root, osc_style)
    raw = subprocess.check_output([sys.executable, "-c", code], text=True)
    opts = json.loads(raw.strip().splitlines()[-1])
    if not scripts:
        opts.pop("scripts", None)
        opts.pop("script", None)
    if force_window is not None:
        if force_window:
            opts["force_window"] = True
        else:
            opts.pop("force_window", None)
    return opts


class Pipe:
    """Single-threaded sync client: write, then read until the reply. Never
    read and write concurrently (a sync Windows handle serialises them)."""

    def __init__(self, name):
        self.f = open(r"\\.\pipe" + "\\" + name, "r+b", buffering=0)
        self.buf = b""
        self.rid = 0
        self.events = []  # (t, name)

    def _line(self):
        while b"\n" not in self.buf:
            chunk = self.f.read(65536)
            if not chunk:
                raise EOFError
            self.buf += chunk
        line, self.buf = self.buf.split(b"\n", 1)
        return json.loads(line)

    def cmd(self, *args):
        self.rid += 1
        rid = self.rid
        self.f.write(json.dumps({"command": list(args),
                                 "request_id": rid}).encode() + b"\n")
        while True:
            msg = self._line()
            if "event" in msg:
                self.events.append((time.perf_counter(), msg["event"]))
                continue
            if msg.get("request_id") == rid:
                return msg

    def wait_event(self, name, timeout=20):
        for t, ev in self.events:
            if ev == name:
                return t
        end = time.perf_counter() + timeout
        while time.perf_counter() < end:
            msg = self._line()
            if "event" in msg:
                t = time.perf_counter()
                self.events.append((t, msg["event"]))
                if msg["event"] == name:
                    return t
        return None

    def close(self):
        try:
            self.f.close()
        except OSError:
            pass


def one_run(mpv, argv_tail, extra_cmds=()):
    name = "jmsbench%d" % random.randint(0, 2 ** 40)
    argv = [mpv, "--idle=yes", "--input-ipc-server=" + name,
            "--input-terminal=no", "--terminal=no"] + argv_tail
    t0 = time.perf_counter()
    proc = subprocess.Popen(argv, stdout=subprocess.DEVNULL,
                            stderr=subprocess.DEVNULL)
    path = r"\\.\pipe" + "\\" + name
    t_pipe = None
    while time.perf_counter() - t0 < 15:
        if k32.WaitNamedPipeW(path, 0):
            t_pipe = time.perf_counter()
            break
        err = ctypes.get_last_error()
        if err not in (2, 3):     # exists but busy
            t_pipe = time.perf_counter()
            break
        if proc.poll() is not None:
            break
        time.sleep(0.001)
    res = {"pipe_ms": None, "ready_ms": None, "loaded_ms": None,
           "restart_ms": None}
    if t_pipe is None:
        proc.kill()
        return res
    p = Pipe(name)
    p.cmd("get_property", "mpv-version")
    t_ready = time.perf_counter()
    for c in extra_cmds:
        p.cmd(*c)
    t_load = time.perf_counter()
    p.cmd("loadfile", TESTSRC)
    t_loaded = p.wait_event("file-loaded")
    t_restart = p.wait_event("playback-restart")
    res.update(pipe_ms=(t_pipe - t0) * 1000, ready_ms=(t_ready - t0) * 1000,
               loaded_ms=(t_loaded - t_load) * 1000 if t_loaded else None,
               restart_ms=(t_restart - t_load) * 1000 if t_restart else None,
               total_to_frame_ms=(t_restart - t0) * 1000 if t_restart else None)
    try:
        p.cmd("quit")
    except (EOFError, OSError):
        pass
    p.close()
    try:
        proc.wait(5)
    except subprocess.TimeoutExpired:
        proc.kill()
    return res


def ipc_latency(mpv, shim_root, n=300):
    name = "jmsbench%d" % random.randint(0, 2 ** 40)
    proc = subprocess.Popen([mpv, "--idle=yes", "--no-config",
                             "--input-ipc-server=" + name, "--terminal=no",
                             "--vo=null", "--ao=null"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    path = r"\\.\pipe" + "\\" + name
    while not k32.WaitNamedPipeW(path, 0):
        time.sleep(0.005)
    out = {}
    p = Pipe(name)
    p.cmd("loadfile", TESTSRC)
    p.wait_event("playback-restart")
    ts = []
    for _ in range(n):
        t = time.perf_counter()
        p.cmd("get_property", "volume")
        ts.append((time.perf_counter() - t) * 1000)
    out["raw_pipe_get_ms_median"] = statistics.median(ts)
    out["raw_pipe_get_ms_p95"] = sorted(ts)[int(n * 0.95)]
    p.close()
    # Same mpv, through python-mpv-jsonipc (attach, no start).
    sys.path.insert(0, shim_root)
    import python_mpv_jsonipc as jipc
    t = time.perf_counter()
    m = jipc.MPV(start_mpv=False, ipc_socket=name)
    out["jsonipc_attach_ms"] = (time.perf_counter() - t) * 1000
    out["jsonipc_property_count"] = len(m.properties)
    ts = []
    for _ in range(n):
        t = time.perf_counter()
        _ = m.volume
        ts.append((time.perf_counter() - t) * 1000)
    out["jsonipc_get_ms_median"] = statistics.median(ts)
    out["jsonipc_get_ms_p95"] = sorted(ts)[int(n * 0.95)]
    ts = []
    for _ in range(n):
        t = time.perf_counter()
        m.volume = 100
        ts.append((time.perf_counter() - t) * 1000)
    out["jsonipc_set_ms_median"] = statistics.median(ts)
    # Observed-property delivery: how many playback-time notifications/s
    oid = []
    cnt = [0]
    m.bind_property_observer("playback-time", lambda *_: cnt.__setitem__(0, cnt[0] + 1))
    time.sleep(2.0)
    out["observer_playback_time_events_per_s"] = cnt[0] / 2.0
    try:
        m.command("quit")
    except Exception:
        pass
    try:
        m.terminate()
    except Exception:
        pass
    try:
        proc.wait(5)
    except subprocess.TimeoutExpired:
        proc.kill()
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shim-root", required=True)
    ap.add_argument("--mpv", default="mpv.exe")
    ap.add_argument("--runs", type=int, default=7)
    ap.add_argument("--json")
    a = ap.parse_args()

    cfg = tempfile.mkdtemp(prefix="jmsbench-cfg-")
    for n in ("mpv.conf", "input.conf"):
        open(os.path.join(cfg, n), "w").close()
    os.makedirs(os.path.join(cfg, "scripts"), exist_ok=True)

    mpvtk = shim_options(a.shim_root, cfg, "mpvtk")
    variants = {
        "bare (--no-config)": ["--no-config"],
        "shim mpvtk (as built)": to_argv(mpvtk),
        "shim mpvtk, no scripts": to_argv(
            shim_options(a.shim_root, cfg, "mpvtk", scripts=False)),
        "shim mpvtk, no force-window": to_argv(
            shim_options(a.shim_root, cfg, "mpvtk", force_window=False)),
        "shim mpv-OSC (+load-script @osc.lua)": to_argv(
            shim_options(a.shim_root, cfg, "mpv")),
    }
    extra = {"shim mpv-OSC (+load-script @osc.lua)":
             [("load-script", "@osc.lua")]}
    results = {k: [] for k in variants}
    order = list(variants)
    for r in range(a.runs + 1):            # first round is warm-up
        random.shuffle(order)
        for k in order:
            res = one_run(a.mpv, variants[k], extra.get(k, ()))
            if r:
                results[k].append(res)
    summary = {"variants": {}, "argv": {k: v for k, v in variants.items()}}
    for k, rows in results.items():
        s = {}
        for f in ("pipe_ms", "ready_ms", "loaded_ms", "restart_ms",
                  "total_to_frame_ms"):
            vals = [x[f] for x in rows if x.get(f) is not None]
            s[f] = round(statistics.median(vals), 1) if vals else None
            s[f + "_min"] = round(min(vals), 1) if vals else None
            s[f + "_max"] = round(max(vals), 1) if vals else None
        summary["variants"][k] = s
    summary["ipc"] = ipc_latency(a.mpv, a.shim_root)
    txt = json.dumps(summary, indent=2)
    print(txt)
    if a.json:
        with open(a.json, "w") as f:
            f.write(txt)


if __name__ == "__main__":
    main()
