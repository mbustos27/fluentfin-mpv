"""Run every backend benchmark, off-screen, and print one comparable summary.

    <venv>\\Scripts\\python.exe run_all.py --shim-root <checkout or backend\\> --out <dir>
        [--media <bench.mkv>] [--quick]

Before/after: run it once on the base commit and once on the change, with the
same --media and on an otherwise idle machine, then compare the two
summary.json files (or eyeball the printed tables). Every number here is a
median or a long-window average; differences under ~5 % or ~5 ms are noise on
this box.

Pieces (each also runnable alone, see its docstring):
  bench_import.py        -X importtime of what bridge mode loads
  bench_mpv_start.py     mpv.exe spawn -> IPC ready -> first frame, by option set;
                         raw vs python-mpv-jsonipc IPC round trip
  bench_player.py        start_playback against fake_jellyfin.py: HTTP/IPC
                         per phase, play timeline, steady-state CPU
                         (run 3x locally, 1x with 50 ms added API latency)
  bench_wait_property.py how late wait_property notices satisfied_by/abort

The venv's python must have the shim importable from --shim-root (sys.path
is set explicitly; nothing pip-installed is used for the shim itself).
"""

import argparse
import json
import os
import statistics
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from run_suite import DEFAULT_MPV  # noqa: E402


def hidden(cmd, log, env, timeout=900):
    full = [sys.executable, os.path.join(HERE, "hidden_run.py"), "--log", log,
            "--timeout", str(timeout), "--desktop",
            "jms-bench-%d" % int(time.time() * 1000)]
    for k, v in env.items():
        full += ["--env", "%s=%s" % (k, v)]
    full += ["--"] + cmd
    return subprocess.call(full)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shim-root", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--media", default=os.path.join(os.path.dirname(HERE),
                                                    "media", "bench.mkv"))
    ap.add_argument("--duration", type=float, default=180)
    ap.add_argument("--mpv-dir", default=DEFAULT_MPV)
    ap.add_argument("--quick", action="store_true",
                    help="fewer repetitions (smoke test, not for comparison)")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    sand = os.path.join(a.out, "env")
    env = {"PATH": a.mpv_dir + os.pathsep + os.environ.get("PATH", ""),
           "APPDATA": os.path.join(sand, "appdata"),
           "LOCALAPPDATA": os.path.join(sand, "localappdata"),
           "PYTHONIOENCODING": "utf-8"}
    for k in ("APPDATA", "LOCALAPPDATA"):
        os.makedirs(env[k], exist_ok=True)
    py = sys.executable
    runs = 2 if a.quick else 5
    summary = {"shim_root": a.shim_root, "when": time.strftime("%Y-%m-%d %H:%M:%S")}

    # 1. imports
    imp = os.path.join(a.out, "imports.json")
    hidden([py, os.path.join(HERE, "bench_import.py"), "--shim-root",
            a.shim_root, "--runs", str(runs), "--json", imp],
           os.path.join(a.out, "imports.log"), env)
    summary["imports"] = json.load(open(imp))

    # 2. mpv start
    st = os.path.join(a.out, "mpv_start.json")
    hidden([py, os.path.join(HERE, "bench_mpv_start.py"), "--shim-root",
            a.shim_root, "--runs", str(3 if a.quick else 9), "--json", st],
           os.path.join(a.out, "mpv_start.log"), env)
    summary["mpv_start"] = json.load(open(st))

    # 3. player, local server, N runs
    players = []
    for i in range(1 if a.quick else 3):
        cfg = os.path.join(a.out, "cfg-player-%d" % i)
        out = os.path.join(a.out, "player-%d.json" % i)
        hidden([py, os.path.join(HERE, "bench_player.py"), "--shim-root",
                a.shim_root, "--media", a.media, "--duration",
                str(a.duration), "--steady", "10" if a.quick else "60",
                "--config", cfg, "--json", out],
               os.path.join(a.out, "player-%d.log" % i), env)
        players.append(json.load(open(out)))
    summary["player_runs"] = players

    # 4. player, 50 ms per API request (a WAN stand-in)
    cfg = os.path.join(a.out, "cfg-player-wan")
    out = os.path.join(a.out, "player-wan50.json")
    hidden([py, os.path.join(HERE, "bench_player.py"), "--shim-root",
            a.shim_root, "--media", a.media, "--duration", str(a.duration),
            "--steady", "10", "--latency-ms", "50", "--config", cfg,
            "--json", out], os.path.join(a.out, "player-wan50.log"), env)
    summary["player_wan50"] = json.load(open(out))

    # 4b. player without trickplay: isolates the worker's GIL share right
    # after a start (its tile decode runs while start_playback returns)
    cfg = os.path.join(a.out, "cfg-player-notrick")
    out = os.path.join(a.out, "player-notrick.json")
    hidden([py, os.path.join(HERE, "bench_player.py"), "--shim-root",
            a.shim_root, "--media", a.media, "--duration", str(a.duration),
            "--steady", "10", "--no-trickplay", "--config", cfg,
            "--json", out], os.path.join(a.out, "player-notrick.log"), env)
    summary["player_notrick"] = json.load(open(out))

    # 5. wait_property
    wp = subprocess.run([py, os.path.join(HERE, "bench_wait_property.py"),
                         "--shim-root", a.shim_root, "--trials",
                         "10" if a.quick else "20"],
                        capture_output=True, text=True,
                        env=dict(os.environ, **{k: env[k] for k in
                                                ("APPDATA", "LOCALAPPDATA")}))
    summary["wait_property"] = wp.stdout.strip().splitlines()

    with open(os.path.join(a.out, "summary.json"), "w") as f:
        json.dump(summary, f, indent=2, default=str)

    # -- the comparable table ---------------------------------------------
    def med(key, sub="timings_ms"):
        v = [p[sub][key] for p in players if p[sub].get(key) is not None]
        return round(statistics.median(v), 1) if v else None

    print("\n== imports (median ms, wall of the import statement) ==")
    for k, v in summary["imports"].items():
        print("  %-60s %8s   self(player)=%s" % (
            k, v["wall_ms"], v["player_self_ms (mpv construct)"]))
    print("\n== mpv start (median ms) ==")
    for k, v in summary["mpv_start"]["variants"].items():
        print("  %-42s pipe %6s ready %6s loaded %6s first-frame %6s total %6s"
              % (k, v["pipe_ms"], v["ready_ms"], v["loaded_ms"],
                 v["restart_ms"], v["total_to_frame_ms"]))
    print("  ipc:", summary["mpv_start"]["ipc"])
    print("\n== player (median of %d runs, ms) ==" % len(players))
    for k in players[0]["timings_ms"]:
        print("  %-45s %8s" % (k, med(k)))
    print("  steady python CPU %% of a core: %s" % [
        p["steady"]["python_cpu_pct_of_one_core"] for p in players])
    print("  steady mpv CPU %% of a core:    %s" % [
        p["steady"]["mpv_cpu_pct_of_one_core"] for p in players])
    print("  steady IPC commands/s:         %s" % [
        p["ipc"]["steady"].get("per_s") for p in players])
    print("  IPC commands construct/play/play2: %s" % [
        (p["ipc"]["construct"]["count"], p["ipc"]["play"]["count"],
         p["ipc"]["play2"]["count"]) for p in players])
    print("  HTTP requests during play(): %s" % [
        len(p["http"]["play"]) for p in players])
    w = summary["player_wan50"]["timings_ms"]
    print("\n== player with +50 ms per API request ==")
    for k in ("play_call_to_file_loaded", "play_call_to_playback_restart",
              "play_call_returned", "play2_call_returned"):
        print("  %-45s %8s" % (k, w.get(k)))
    w = summary["player_notrick"]["timings_ms"]
    print("\n== player, trickplay off ==")
    for k in ("play_call_to_playback_restart", "play_call_returned",
              "play2_call_returned"):
        print("  %-45s %8s" % (k, w.get(k)))
    print("\n== wait_property ==")
    for line in summary["wait_property"]:
        print("  " + line)


if __name__ == "__main__":
    main()
