"""Nixie 1.7.5, the basics gate (8-lifecycle.ps1): a TCP forwarder on 127.0.0.1 in front of the fake server, so a run can take
the server away and bring it back as a network does, with the app's saved address unchanged.

    python -I -B g4_forward.py --target 127.0.0.1:<port> --dir <folder>

- <folder>/port.txt: the port it listens on (written once listening).
- <folder>/offline present: offline. Every open connection is reset, and every new one is accepted and reset at once (the
  app sees its connection refused or reset, as when the PC's network goes); each such attempt is counted. Removed: online.
- <folder>/stats.json (rewritten as it changes): {"offline": bool, "resets": N, "reset_at": [epoch, ...], "connections": N,
  "devices": {"<DeviceId>": first-seen epoch}}. DeviceId values are read from the requests' Authorization headers as they
  pass; nothing else of a request is kept (never a token).
- <folder>/stop present: it ends.

Standard library only. Runs inside the harness's kill-on-close job (drive.ps1 starts it), so it never outlives the run.
"""

import argparse
import json
import os
import re
import socket
import struct
import threading
import time

DEVICE = re.compile(rb'DeviceId="([^"\r\n]{1,128})"')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", required=True)
    ap.add_argument("--dir", required=True)
    args = ap.parse_args()
    host, port = args.target.rsplit(":", 1)
    target = (host, int(port))
    folder = args.dir
    os.makedirs(folder, exist_ok=True)
    flag, stop = os.path.join(folder, "offline"), os.path.join(folder, "stop")
    lock = threading.Lock()
    conns = set()
    stats = {"offline": False, "resets": 0, "reset_at": [], "connections": 0, "devices": {}}
    dirty = [True]

    def save():
        with lock:
            if not dirty[0]:
                return
            body = json.dumps(stats)
            dirty[0] = False
        tmp = os.path.join(folder, "stats.json.tmp")
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                f.write(body)
            os.replace(tmp, os.path.join(folder, "stats.json"))
        except OSError:
            # a reader has the file open (Windows refuses the replace then): written again at the next tick
            with lock:
                dirty[0] = True

    def reset(s):
        try:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
        except OSError:
            pass
        try:
            s.close()
        except OSError:
            pass

    def pump(src, dst, scan):
        tail = b""
        try:
            while True:
                data = src.recv(65536)
                if not data:
                    break
                if scan:
                    buf = tail + data
                    for m in DEVICE.finditer(buf):
                        dev = m.group(1).decode("ascii", "replace")
                        with lock:
                            if dev not in stats["devices"]:
                                stats["devices"][dev] = round(time.time(), 3)
                                dirty[0] = True
                    tail = buf[-256:]
                dst.sendall(data)
        except OSError:
            pass
        finally:
            for s in (src, dst):
                try:
                    s.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
            with lock:
                conns.discard(src)
                conns.discard(dst)

    def watch():
        while not os.path.exists(stop):
            off = os.path.exists(flag)
            with lock:
                changed = off != stats["offline"]
                stats["offline"] = off
                gone = list(conns) if changed and off else []
                if changed:
                    dirty[0] = True
                    conns.difference_update(gone)
            for s in gone:
                reset(s)
            save()
            time.sleep(0.1)
        os._exit(0)

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.bind(("127.0.0.1", 0))
    srv.listen(128)
    with open(os.path.join(folder, "port.txt"), "w", encoding="utf-8") as f:
        f.write(str(srv.getsockname()[1]))
    threading.Thread(target=watch, daemon=True).start()
    while True:
        client, _ = srv.accept()
        if os.path.exists(flag):
            with lock:
                stats["resets"] += 1
                stats["reset_at"].append(round(time.time(), 3))
                dirty[0] = True
            reset(client)
            continue
        try:
            upstream = socket.create_connection(target, timeout=10)
            upstream.settimeout(None)
        except OSError:
            reset(client)
            continue
        with lock:
            conns.update((client, upstream))
            stats["connections"] += 1
            dirty[0] = True
        threading.Thread(target=pump, args=(client, upstream, True), daemon=True).start()
        threading.Thread(target=pump, args=(upstream, client, False), daemon=True).start()


if __name__ == "__main__":
    main()
