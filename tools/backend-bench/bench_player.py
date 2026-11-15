"""End-to-end play-path benchmark of the shim, the way bridge mode drives it.

    python hidden_run.py --log out.txt --env PATH=<mpv dir>;%PATH% ... -- ^
        python bench_player.py --shim-root <checkout> --media bench.mkv ^
            --duration 180 --steady 30 --json result.json

Builds a Jellyfin client from a token (no websocket, as BRIDGE.md says) against
a local stand-in server (fake_jellyfin.py), imports the real player (which
starts a real mpv.exe through the external backend), then:

  construct  import + PlayerManager() -- mpv spawn, IPC attach, _init_mpv
  play       event_handler.start_playback(client, [item]) until it returns,
             with the time to mpv's file-loaded and playback-restart events
  steady     N seconds of playback with the timeline and action threads
             running: process CPU (python + mpv), IPC commands/s by
             property and thread, HTTP requests/s, events/s
  seek       playerManager.seek(+60 s) -> playback-restart
  stop       playerManager.stop()

Instrumentation is monkeypatching only (python_mpv_jsonipc.MPVInter.command /
command_async / event_callback, a few PlayerManager methods wrapped with a
timer). Nothing in the shim is edited. Every HTTP request is logged by the
fake server with a timestamp, so the play path's round trips are counted and
ordered rather than inferred.
"""

import argparse
import collections
import ctypes
import json
import os
import statistics
import sys
import threading
import time
from ctypes import wintypes as wt

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenProcess.restype = wt.HANDLE
k32.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(wt.FILETIME)] * 4


def proc_cpu_s(pid):
    h = k32.OpenProcess(0x1000, False, pid)   # QUERY_LIMITED_INFORMATION
    if not h:
        return None
    c, e, k, u = (wt.FILETIME() for _ in range(4))
    k32.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k),
                        ctypes.byref(u))
    k32.CloseHandle(h)

    def f(t):
        return ((t.dwHighDateTime << 32) | t.dwLowDateTime) / 1e7
    return f(k) + f(u)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shim-root", required=True)
    ap.add_argument("--media", required=True)
    ap.add_argument("--duration", type=float, required=True)
    ap.add_argument("--steady", type=float, default=30)
    ap.add_argument("--config", default=None)
    ap.add_argument("--latency-ms", type=float, default=0.0,
                    help="added server latency per request (simulates a WAN)")
    ap.add_argument("--json")
    ap.add_argument("--no-trickplay", action="store_true")
    ap.add_argument("--live", action="store_true",
                    help="serve a no-duration stream (Live TV / .strm path)")
    ap.add_argument("--skip-seek", action="store_true")
    ap.add_argument("--no-playstate-hook", action="store_true",
                    help="leave on_playstate unset (upstream CLI mode)")
    a = ap.parse_args()

    import tempfile
    cfg = a.config or tempfile.mkdtemp(prefix="jmsbench-player-")
    os.makedirs(cfg, exist_ok=True)

    import subprocess
    from fake_jellyfin import ITEM_ID, USER_ID, TOKEN, read_log
    http_log = os.path.join(cfg, "http-log.jsonl")
    port_file = os.path.join(cfg, "server-url.txt")
    for p in (http_log, port_file):
        if os.path.exists(p):
            os.remove(p)
    server_argv = [sys.executable, "-I", os.path.join(HERE, "fake_jellyfin.py"),
                   "--media", a.media, "--duration", str(a.duration),
                   "--log", http_log, "--port-file", port_file,
                   "--latency-ms", str(a.latency_ms)]
    if a.no_trickplay:
        server_argv.append("--no-trickplay")
    if a.live:
        server_argv.append("--live")
    server = subprocess.Popen(server_argv)
    for _ in range(200):
        if os.path.exists(port_file):
            break
        time.sleep(0.05)
    with open(port_file) as f:
        url = f.read().strip()

    # -- instrumentation, before anything under jellyfin_mpv_shim ---------
    import python_mpv_jsonipc as jipc

    PHASE = ["boot"]
    ipc_rows = []          # (phase, cmd, arg, ms, thread, t)
    ev_rows = []           # (phase, t, event, name)
    marks = {}
    lock = threading.Lock()

    orig_cmd = jipc.MPVInter.command

    def cmd(self, command, *args):
        t = time.perf_counter()
        try:
            return orig_cmd(self, command, *args)
        finally:
            arg = args[0] if args and isinstance(args[0], str) else (
                str(args[0]) if args else "")
            with lock:
                ipc_rows.append((PHASE[0], command, arg,
                                 (time.perf_counter() - t) * 1000,
                                 threading.current_thread().name, t))

    jipc.MPVInter.command = cmd
    orig_async = jipc.MPVInter.command_async

    def cmd_async(self, on_reply, command, *args):
        with lock:
            ipc_rows.append((PHASE[0], command + " (async)",
                             args[0] if args and isinstance(args[0], str)
                             else "", 0.0, threading.current_thread().name,
                             time.perf_counter()))
        return orig_async(self, on_reply, command, *args)

    jipc.MPVInter.command_async = cmd_async
    orig_ev = jipc.MPVInter.event_callback

    def ev(self, data):
        if "event" in data:
            t = time.perf_counter()
            with lock:
                ev_rows.append((PHASE[0], t, data["event"], data.get("name")))
            if data["event"] in ("file-loaded", "playback-restart"):
                marks.setdefault("%s:%s" % (PHASE[0], data["event"]), t)
        return orig_ev(self, data)

    jipc.MPVInter.event_callback = ev
    orig_proc = jipc.MPVProcess.__init__

    def proc_init(self, *aa, **kw):
        t = time.perf_counter()
        try:
            return orig_proc(self, *aa, **kw)
        finally:
            marks["mpvprocess_ms"] = (time.perf_counter() - t) * 1000

    jipc.MPVProcess.__init__ = proc_init

    # -- the shim, as bridge mode would load it -----------------------------
    sys.argv = [sys.argv[0], "--config", cfg]
    sys.path.insert(0, a.shim_root)
    t_imp0 = time.perf_counter()
    from jellyfin_mpv_shim.conf import settings
    conf_path = os.path.join(cfg, "conf.json")
    settings.load(conf_path)
    settings.check_updates = False          # bridge contract: no update checks
    if a.no_trickplay:
        settings.thumbnail_enable = False
    t_conf = time.perf_counter()
    PHASE[0] = "construct"
    from jellyfin_mpv_shim import player as player_mod
    pm = player_mod.playerManager
    t_player = time.perf_counter()
    PHASE[0] = "wire"
    from jellyfin_mpv_shim.event_handler import start_playback
    from jellyfin_mpv_shim.timeline import timelineManager
    from jellyfin_mpv_shim.action_thread import actionThread
    from jellyfin_mpv_shim import bifdecode
    t_wired_imports = time.perf_counter()

    # Per-call timers on the periodic work.
    calls = collections.defaultdict(list)

    def timed(owner, name):
        f = getattr(owner, name)

        def w(*aa, **kw):
            t = time.perf_counter()
            try:
                return f(*aa, **kw)
            finally:
                with lock:
                    calls[name].append(((time.perf_counter() - t) * 1000,
                                        PHASE[0]))
        setattr(owner, name, w)

    for n in ("update", "send_timeline", "push_playstate", "_pump_trickplay",
              "get_timeline_options"):
        timed(pm, n)
    timed(bifdecode, "decompress_tiles")

    # Spans on the play path, to attribute the time before loadfile.
    spans = []          # (phase, name, t_start, ms)

    def span(owner, name, label=None):
        f = getattr(owner, name)

        def w(*aa, **kw):
            t = time.perf_counter()
            try:
                return f(*aa, **kw)
            finally:
                with lock:
                    spans.append((PHASE[0], label or name, t,
                                  (time.perf_counter() - t) * 1000))
        setattr(owner, name, w)

    from jellyfin_mpv_shim import media as media_mod
    from jellyfin_mpv_shim import event_handler as eh_mod
    span(media_mod.Media, "__init__", "Media.__init__")
    span(media_mod.Video, "__init__", "Video.__init__ (GET item)")
    span(media_mod, "is_local_domain")
    span(media_mod, "get_profile")
    span(media_mod.Video, "get_playback_url")
    span(media_mod.Video, "get_intro", "get_intro (MediaSegments)")
    span(media_mod.Video, "resolve_tracks_for_negotiation")
    for n in ("play", "_play_media", "_apply_auth_headers",
              "_warm_shader_scope", "configure_streams",
              "send_timeline_initial", "upd_player_hide",
              "_apply_render_presets", "set_paused"):
        span(pm, n)
    span(pm.syncplay, "in_group", "syncplay.in_group")

    # Bridge mode will hook the now-playing snapshot to emit `state` events,
    # which turns push_playstate's property reads on (it returns early when
    # nobody listens). Counted, so its rate shows up in the results.
    playstates = []
    if not a.no_playstate_hook:
        pm.on_playstate = lambda payload: playstates.append(
            (PHASE[0], time.perf_counter()))

    timelineManager.start()
    pm.timeline_trigger = timelineManager.trigger
    actionThread.start()
    pm.action_trigger = actionThread.trigger

    # Client from a token, as credential_client() / bridge connect does it.
    from jellyfin_apiclient_python import JellyfinClient
    client = JellyfinClient(allow_multiple_clients=True)
    client.config.data["app.default"] = True
    client.config.app("FluentFin", "1.0.0", "bench", "bench-device")
    client.config.data["http.user_agent"] = "bench"
    client.config.data["auth.ssl"] = False
    client.config.auth(url, USER_ID, TOKEN, False)
    client.logged_in = True
    client.start(websocket=False)
    t_client = time.perf_counter()

    # -- play ---------------------------------------------------------------
    PHASE[0] = "play"
    pid = pm._player.mpv_process.process.pid
    cpu_py0, cpu_mpv0 = time.process_time(), proc_cpu_s(pid)
    t_play = time.perf_counter()
    ok = start_playback(client, [ITEM_ID])
    t_play_ret = time.perf_counter()
    cpu_py_play = time.process_time() - cpu_py0

    # -- steady -------------------------------------------------------------
    PHASE[0] = "steady"
    time.sleep(1.0)        # let the action thread see "live" first
    cpu_py0, cpu_mpv0 = time.process_time(), proc_cpu_s(pid)
    t_s0 = time.perf_counter()
    time.sleep(a.steady)
    t_s1 = time.perf_counter()
    cpu_py_steady = time.process_time() - cpu_py0
    cpu_mpv_steady = proc_cpu_s(pid) - cpu_mpv0
    pos_after_steady = pm._player.playback_time

    # -- seek ---------------------------------------------------------------
    PHASE[0] = "seek"
    t_seek = time.perf_counter()
    if not a.skip_seek:
        pm.seek(60)        # relative, the remote-control / bridge seek_relative
    t_seek_ret = time.perf_counter()
    time.sleep(2.0)

    # -- stop ---------------------------------------------------------------
    PHASE[0] = "stop"
    t_stop = time.perf_counter()
    pm.stop()
    t_stop_ret = time.perf_counter()
    time.sleep(2.0)

    # -- play2: the same start on a warm mpv (the common case) -------------
    PHASE[0] = "play2"
    t_play2 = time.perf_counter()
    start_playback(client, [ITEM_ID])
    t_play2_ret = time.perf_counter()
    time.sleep(3.0)
    PHASE[0] = "stop2"
    pm.stop()
    time.sleep(1.0)

    # -- play3: after an idle-quit (mpv_idle_quit, default on, 300 s) -------
    PHASE[0] = "idlequit"
    pm.idle_quit()
    time.sleep(1.5)
    PHASE[0] = "play3"
    t_play3 = time.perf_counter()
    start_playback(client, [ITEM_ID])
    t_play3_ret = time.perf_counter()
    time.sleep(2.0)
    PHASE[0] = "stop3"
    pm.stop()
    time.sleep(1.0)

    # -- the client's own HTTP cost, isolated: 30 progress posts, 30 GETs ---
    PHASE[0] = "httpbench"
    post_ms, get_ms = [], []
    opts = {"ItemId": ITEM_ID, "PositionTicks": 0, "IsPaused": True,
            "PlaySessionId": "benchsession", "MediaSourceId": ITEM_ID}
    for _ in range(30):
        t = time.perf_counter()
        client.jellyfin.session_progress(opts)
        post_ms.append((time.perf_counter() - t) * 1000)
    for _ in range(30):
        t = time.perf_counter()
        client.jellyfin.get_item(ITEM_ID)
        get_ms.append((time.perf_counter() - t) * 1000)
    PHASE[0] = "end"

    time.sleep(0.3)
    http = read_log(http_log)
    server.kill()

    def ms(x):
        return round(x * 1000, 1)

    def ipc_summary(phase, per_s=None):
        rows = [r for r in ipc_rows if r[0] == phase]
        by = collections.Counter()
        tot = collections.defaultdict(float)
        thr = collections.Counter()
        for _p, c, arg, d, th, _t in rows:
            key = c + ((" " + arg) if c in ("get_property", "set_property",
                                            "observe_property", "load-script",
                                            "keybind", "define-section",
                                            "enable-section") and arg else "")
            by[key] += 1
            tot[key] += d
            thr[th.split("-")[0] if th else "?"] += 1
        out = {"count": len(rows),
               "total_ms": round(sum(r[3] for r in rows), 1),
               "median_ms": round(statistics.median([r[3] for r in rows]), 3)
               if rows else None,
               "by_command": {k: [v, round(tot[k], 1)]
                              for k, v in by.most_common()},
               "by_thread": dict(thr.most_common())}
        if per_s:
            out["per_s"] = round(len(rows) / per_s, 2)
        return out

    def http_rows(t_from, t_to):
        return [{"t_ms": ms(r["t"] - t_from), "method": r["method"],
                 "path": r["path"].split("?")[0], "status": r["status"],
                 "server_ms": round(r["ms"], 1)}
                for r in http if t_from <= r["t"] <= t_to]

    def call_stats(name, phase):
        v = [d for d, p in calls.get(name, []) if p == phase]
        if not v:
            return None
        return {"n": len(v), "median_ms": round(statistics.median(v), 2),
                "max_ms": round(max(v), 2), "total_ms": round(sum(v), 1)}

    def timeline(phase, t_from, t_to):
        """Everything that happened in a window, in order: IPC commands
        (with how long each waited for mpv), mpv events, HTTP requests."""
        rows = []
        for p, c, arg, d, th, t in ipc_rows:
            if p == phase:
                rows.append((t, "ipc", "%s %s" % (c, arg), round(d, 2),
                             th.split("-")[0] if th else "?"))
        for p, t, e, n in ev_rows:
            if p == phase and e not in ("log-message",):
                rows.append((t, "event", e + ((":" + n) if n else ""), None,
                             ""))
        for p, name, t, d in spans:
            if p == phase:
                rows.append((t, "span", name, round(d, 2), ""))
        for r in http:
            if t_from <= r["t"] <= t_to:
                rows.append((r.get("t_start", r["t"]), "http", "%s %s" % (
                    r["method"], r["path"].split("?")[0]),
                    round(r["ms"], 1), "server"))
        rows.sort()
        return [[round((t - t_from) * 1000, 1), k, what, d, th]
                for t, k, what, d, th in rows]

    steady_s = t_s1 - t_s0
    ev_steady = collections.Counter(
        (e if e != "property-change" else "property-change:%s" % n)
        for p, _t, e, n in ev_rows if p == "steady")
    res = {
        "started": ok,
        "timings_ms": {
            "conf_import_and_load": ms(t_conf - t_imp0),
            "player_import_and_construct": ms(t_player - t_conf),
            "  of_which_MPVProcess_spawn_to_pipe": round(
                marks.get("mpvprocess_ms", 0), 1),
            "event_handler_timeline_action_imports": ms(
                t_wired_imports - t_player),
            "client_from_token": ms(t_client - t_wired_imports),
            "play_call_to_file_loaded": ms(
                marks.get("play:file-loaded", t_play) - t_play),
            "play_call_to_playback_restart": ms(
                marks.get("play:playback-restart", t_play) - t_play),
            "play_call_returned": ms(t_play_ret - t_play),
            "seek_call_returned": ms(t_seek_ret - t_seek),
            "seek_to_playback_restart": ms(
                marks.get("seek:playback-restart", t_seek) - t_seek),
            "stop_call_returned": ms(t_stop_ret - t_stop),
            "play2_call_to_file_loaded": ms(
                marks.get("play2:file-loaded", t_play2) - t_play2),
            "play2_call_to_playback_restart": ms(
                marks.get("play2:playback-restart", t_play2) - t_play2),
            "play2_call_returned": ms(t_play2_ret - t_play2),
            "play3_after_idle_quit_to_playback_restart": ms(
                marks.get("play3:playback-restart", t_play3) - t_play3),
            "play3_after_idle_quit_returned": ms(t_play3_ret - t_play3),
        },
        "client_http_ms": {
            "session_progress_post_median": round(statistics.median(post_ms), 2),
            "session_progress_post_max": round(max(post_ms), 2),
            "get_item_median": round(statistics.median(get_ms), 2),
            "get_item_max": round(max(get_ms), 2),
        },
        "play_timeline": timeline("play", t_play, t_play_ret + 0.05),
        "play2_timeline": timeline("play2", t_play2, t_play2_ret + 0.05),
        "ipc": {
            "construct": ipc_summary("construct"),
            "play": ipc_summary("play"),
            "steady": ipc_summary("steady", steady_s),
            "seek": ipc_summary("seek"),
            "stop": ipc_summary("stop"),
            "play2": ipc_summary("play2"),
            "play3": ipc_summary("play3"),
        },
        "http": {
            "play": http_rows(t_play, t_play_ret),
            "play_to_steady_start": http_rows(t_play_ret, t_s0),
            "steady": collections.Counter(
                r["path"].split("?")[0] for r in http
                if t_s0 <= r["t"] <= t_s1),
            "seek": http_rows(t_seek, t_stop),
            "stop": http_rows(t_stop, t_play2),
            "play2": http_rows(t_play2, t_play2_ret + 3.0),
            "all_count": len(http),
        },
        "steady": {
            "seconds": round(steady_s, 2),
            "python_cpu_pct_of_one_core": round(
                100 * cpu_py_steady / steady_s, 2),
            "mpv_cpu_pct_of_one_core": round(
                100 * cpu_mpv_steady / steady_s, 2),
            "events": dict(ev_steady.most_common()),
            "position_after_steady_s": pos_after_steady,
            "playstate_pushes_per_s": round(sum(
                1 for p, _t in playstates if p == "steady") / steady_s, 2),
        },
        "play_python_cpu_ms": round(cpu_py_play * 1000, 1),
        "periodic_calls": {n: {"play": call_stats(n, "play"),
                               "steady": call_stats(n, "steady")}
                           for n in calls},
    }
    txt = json.dumps(res, indent=2, default=str)
    print(txt)
    if a.json:
        with open(a.json, "w") as f:
            f.write(txt)
    sys.stdout.flush()
    try:
        pm.terminate()
    except Exception:
        pass
    os._exit(0)


if __name__ == "__main__":
    main()
