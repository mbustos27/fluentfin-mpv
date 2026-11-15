"""Why the first request on a fresh JellyfinClient session is slow (no mpv needed).

    python first_request.py --media bench.mkv
Starts fake_jellyfin in its own process, builds a client from a token the way
bridge mode will, and times the 1st..5th GET item; the 1st under cProfile.
"""
import argparse, cProfile, io, os, pstats, subprocess, sys, tempfile, time
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from fake_jellyfin import ITEM_ID, USER_ID, TOKEN
ap = argparse.ArgumentParser(); ap.add_argument("--media", required=True)
ap.add_argument("--profile", action="store_true"); a = ap.parse_args()
d = tempfile.mkdtemp(); pf = os.path.join(d, "url")
srv = subprocess.Popen([sys.executable, "-I", os.path.join(HERE, "fake_jellyfin.py"), "--media", a.media,
                        "--duration", "180", "--log", os.path.join(d, "log"), "--port-file", pf])
while not os.path.exists(pf): time.sleep(0.02)
url = open(pf).read().strip()
t = time.perf_counter()
from jellyfin_apiclient_python import JellyfinClient
t_imp = time.perf_counter() - t
def client():
    c = JellyfinClient(allow_multiple_clients=True)
    c.config.data["app.default"] = True
    c.config.app("FluentFin", "1.0.0", "bench", "bench-device")
    c.config.data["auth.ssl"] = False
    c.config.auth(url, USER_ID, TOKEN, False); c.logged_in = True; c.start(websocket=False)
    return c
for rnd in range(2):
    c = client(); times = []
    for i in range(5):
        pr = cProfile.Profile() if (a.profile and i == 0 and rnd == 1) else None
        t = time.perf_counter()
        if pr: pr.enable()
        c.jellyfin.get_item(ITEM_ID)
        if pr: pr.disable()
        times.append(round((time.perf_counter() - t) * 1000, 2))
        if pr:
            s = io.StringIO(); pstats.Stats(pr, stream=s).sort_stats("cumulative").print_stats(25); print(s.getvalue())
    print("client %d (%s): GET item ms = %s" % (rnd + 1, "first in process" if rnd == 0 else "second client, same process", times))
print("import jellyfin_apiclient_python ms = %.1f" % (t_imp * 1000))
srv.kill()
