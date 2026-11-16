"""OSC visual harness: the real bridge, in a Win32 window of our own, against
a fake Jellyfin server serving an episode with external subtitles and
trickplay. Fake token only. Screenshots are PrintWindow of our own window.

    python osc_harness.py --backend <backend dir> --out <dir> --mkv <file>
                          [--size 1280x720] [--tag before] [--python <venv>]
                          [--runtime-opt osc-scalewindowed=1.5]

The bridge is started from ``--backend`` (``-I -c`` with that folder on
sys.path: a ``-m`` run would import the venv's editable install, i.e. the
main repo, whatever worktree this runs from). It plays an episode whose
Name is its series (the user's case), seeks to a subtitle, then captures:
OSC hidden, OSC shown (``osc-visibility always``), the seek bar hovered
(trickplay), after the wheel, hidden again; and reads ``sub-pos``,
``volume`` and ``time-pos`` over mpv's IPC (``mpv_ext_ipc``) at each step.
``--mkv``: any video; the e2e harness makes one with mpv's encoder.

The bar is whichever the bridge chose (``--osc-style``, default the shim's
``mpvtk``: uosc; ``mpv``: the fork). uosc is shown and hidden with its
``set-min-visibility``, the fork with ``osc-visibility``. The episode has an
Intro segment (25-60 s, so the seek to 31 s lands in it: the Skip button and
prompt) and a second item in the queue (the Next button). ``--fullscreen``
adds a full-screen step (the host window resized to ``--fs-size``, as
FluentFin's full screen does, and ``set_fullscreen`` sent).

Every process this starts is in a kill-on-close job the harness puts itself
in first, so nothing outlives it. The host window is off-screen, never
activated and has no taskbar button.
"""

import argparse
import ctypes
import io
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from ctypes import wintypes as wt

HERE = os.path.dirname(os.path.abspath(__file__))
WT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))   # repo root
sys.path.insert(0, os.path.join(WT, "tools", "backend-harness", "rr"))
sys.path.insert(0, os.path.join(WT, "backend", "tools"))

from fakejf2 import FakeJF  # noqa: E402
import bridge_smoke  # noqa: E402

MPV = shutil.which("mpv.exe") or shutil.which("mpv") or os.path.expandvars(
    r"%LOCALAPPDATA%\Microsoft\WinGet\Packages"
    r"\mpv-player.mpv-CI.MSVC_Microsoft.Winget.Source_8wekyb3d8bbwe\mpv.exe")
TOKEN = "osc-harness-fake-token-0123456789"
USER = "0123456789abcdef0123456789abcdef"
ITEM = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
ITEM2 = "dddddddddddddddddddddddddddddddd"
SERIES = "It's Always Sunny in Philadelphia"

k32 = ctypes.WinDLL("kernel32", use_last_error=True)


def own_job():
    """Put this process in a kill-on-close job: every child dies with us."""
    class BASIC(ctypes.Structure):
        _fields_ = [("a", ctypes.c_int64), ("b", ctypes.c_int64),
                    ("LimitFlags", wt.DWORD), ("c", ctypes.c_size_t),
                    ("d", ctypes.c_size_t), ("e", wt.DWORD),
                    ("f", ctypes.c_size_t), ("g", wt.DWORD), ("h", wt.DWORD)]

    class IOC(ctypes.Structure):
        _fields_ = [(n, ctypes.c_ulonglong) for n in "abcdef"]

    class EXT(ctypes.Structure):
        _fields_ = [("Basic", BASIC), ("Io", IOC),
                    ("p", ctypes.c_size_t), ("q", ctypes.c_size_t),
                    ("r", ctypes.c_size_t), ("s", ctypes.c_size_t)]

    k32.CreateJobObjectW.restype = wt.HANDLE
    k32.SetInformationJobObject.argtypes = [wt.HANDLE, ctypes.c_int,
                                            ctypes.c_void_p, wt.DWORD]
    k32.AssignProcessToJobObject.argtypes = [wt.HANDLE, wt.HANDLE]
    job = k32.CreateJobObjectW(None, None)
    info = EXT()
    info.Basic.LimitFlags = 0x2000          # KILL_ON_JOB_CLOSE
    k32.SetInformationJobObject(job, 9, ctypes.byref(info),
                                ctypes.sizeof(info))
    k32.GetCurrentProcess.restype = wt.HANDLE
    ok = k32.AssignProcessToJobObject(job, wt.HANDLE(-1))
    print("job: assigned=%s" % bool(ok), flush=True)
    return job                               # kept open for our lifetime


# ---------------------------------------------------------------- media

def make_srt(path, lang_word):
    lines = []
    n = 1
    for t in range(0, 180, 3):
        def ts(s):
            return "%02d:%02d:%02d,000" % (s // 3600, (s % 3600) // 60, s % 60)
        lines.append("%d\n%s --> %s\n%s line %d, the quick brown fox\n"
                     "jumps over the lazy dog (%s)\n"
                     % (n, ts(t), ts(t + 3), lang_word, n, lang_word))
        n += 1
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))


def tile_jpeg(index, width=320, height=180, cols=10, rows=10, count=180):
    from PIL import Image, ImageDraw
    img = Image.new("RGB", (width * cols, height * rows), (30, 30, 30))
    d = ImageDraw.Draw(img)
    for k in range(cols * rows):
        frame = index * cols * rows + k
        if frame >= count:
            break
        x, y = (k % cols) * width, (k // cols) * height
        hue = (frame * 37) % 255
        d.rectangle([x, y, x + width - 1, y + height - 1],
                    fill=(hue, 255 - hue, (hue * 3) % 255))
        d.text((x + 20, y + 70), "frame %d  (%d:%02d)"
               % (frame, frame // 60, frame % 60), fill=(0, 0, 0))
    out = io.BytesIO()
    img.save(out, "JPEG", quality=80)
    return out.getvalue()


class EpisodeJF(FakeJF):
    """fakejf2 with an Episode, external subtitles and trickplay."""

    def __init__(self, media, srts, name, movie=False):
        self.ep_name = name
        self.movie = movie
        self.srts = srts
        self.tiles = {}
        super().__init__("osc", TOKEN, media, USER)
        H = self.httpd.RequestHandlerClass
        orig = H.do_GET
        outer = self

        def do_GET(h):
            path = h.path.split("?")[0]
            m = re.search(r"/Subtitles/(\d+)/", path)
            if m and int(m.group(1)) in outer.srts:
                data = open(outer.srts[int(m.group(1))], "rb").read()
                h.send_response(200)
                h.send_header("Content-Type", "text/plain; charset=utf-8")
                h.send_header("Content-Length", str(len(data)))
                h.end_headers()
                h.wfile.write(data)
                return
            if path.startswith("/MediaSegments/"):
                data = json.dumps({"Items": [{
                    "Id": "c" * 32, "ItemId": ITEM, "Type": "Intro",
                    "StartTicks": 25 * 10 ** 7, "EndTicks": 60 * 10 ** 7}]
                }).encode()
                h.send_response(200)
                h.send_header("Content-Type", "application/json")
                h.send_header("Content-Length", str(len(data)))
                h.end_headers()
                h.wfile.write(data)
                return
            m = re.search(r"/Trickplay/(\d+)/(\d+)\.jpg", path)
            if m:
                i = int(m.group(2))
                if i not in outer.tiles:
                    outer.tiles[i] = tile_jpeg(i)
                data = outer.tiles[i]
                h.send_response(200)
                h.send_header("Content-Type", "image/jpeg")
                h.send_header("Content-Length", str(len(data)))
                h.end_headers()
                h.wfile.write(data)
                return
            return orig(h)

        H.do_GET = do_GET

    def item(self, item_id):
        src = self.source(item_id)
        base = {"Id": item_id, "Name": self.ep_name, "MediaType": "Video",
                "RunTimeTicks": 1800000000,
                "UserData": {"PlaybackPositionTicks": 0, "Played": False},
                "MediaSources": [src], "ServerId": "srvosc",
                "Trickplay": {item_id: {"320": {
                    "Width": 320, "Height": 180, "TileWidth": 10,
                    "TileHeight": 10, "ThumbnailCount": 180,
                    "Interval": 1000, "Bandwidth": 0}}}}
        if self.movie:
            base.update({"Type": "Movie", "ProductionYear": 2019})
        else:
            base.update({"Type": "Episode", "SeriesName": SERIES,
                         "SeriesId": "b" * 32, "IndexNumber": 4,
                         "ParentIndexNumber": 6})
        return base

    def source(self, item_id):
        streams = [
            {"Type": "Video", "Index": 0, "Codec": "mpeg4", "Width": 1280,
             "Height": 720, "IsDefault": True},
            {"Type": "Audio", "Index": 1, "Codec": "aac", "Language": "eng",
             "Channels": 2, "DisplayTitle": "English - AAC - Stereo",
             "IsDefault": True},
        ]
        for idx, (lang, title) in ((2, ("eng", "English - SUBRIP")),
                                   (3, ("spa", "Spanish - SUBRIP"))):
            streams.append({
                "Type": "Subtitle", "Index": idx, "Codec": "subrip",
                "Language": lang, "DisplayTitle": title, "IsExternal": True,
                "IsTextSubtitleStream": True,
                "SupportsExternalStream": True, "DeliveryMethod": "External",
                "DeliveryUrl": "/Videos/%s/%s/Subtitles/%d/0/Stream.srt"
                % (item_id, item_id, idx)})
        return {"Id": item_id, "Path": "/media/%s.mkv" % item_id,
                "Protocol": "File", "Container": "mkv",
                "Size": os.path.getsize(self.media_path),
                "SupportsDirectPlay": True, "SupportsDirectStream": True,
                "SupportsTranscoding": True, "RunTimeTicks": 1800000000,
                "DefaultAudioStreamIndex": 1, "DefaultSubtitleStreamIndex": 2,
                "MediaStreams": streams}


# ---------------------------------------------------------------- window

class Host(bridge_smoke.HostWindow):
    """bridge_smoke's host window, but sized by client area, without
    activation and without a taskbar button."""

    def __init__(self, cw, ch, x, y):
        self.want_client = (cw, ch)
        super().__init__(x=x, y=y, width=cw + 16, height=ch + 39)

    def _run(self):
        ctypes_, wt_, u = self.ctypes, self.wt, self.user32

        class WNDCLASSW(ctypes_.Structure):
            _fields_ = [("style", wt_.UINT), ("lpfnWndProc", self.WNDPROC),
                        ("cbClsExtra", ctypes_.c_int),
                        ("cbWndExtra", ctypes_.c_int),
                        ("hInstance", wt_.HINSTANCE), ("hIcon", wt_.HICON),
                        ("hCursor", wt_.HANDLE),
                        ("hbrBackground", wt_.HBRUSH),
                        ("lpszMenuName", wt_.LPCWSTR),
                        ("lpszClassName", wt_.LPCWSTR)]

        class MINMAX(ctypes_.Structure):
            _fields_ = [("r", wt_.POINT), ("maxsize", wt_.POINT),
                        ("maxpos", wt_.POINT), ("mintrack", wt_.POINT),
                        ("maxtrack", wt_.POINT)]

        def wndproc(hwnd, msg, wparam, lparam):
            if msg == 0x0024:            # WM_GETMINMAXINFO: allow 4K windows
                mm = ctypes_.cast(lparam, ctypes_.POINTER(MINMAX)).contents
                mm.maxtrack.x = 8000
                mm.maxtrack.y = 5000
                return 0
            if msg == 0x0002:
                u.PostQuitMessage(0)
                return 0
            return u.DefWindowProcW(hwnd, msg, wparam, lparam)

        self._wndproc = self.WNDPROC(wndproc)
        hinst = self.kernel32.GetModuleHandleW(None)
        wc = WNDCLASSW()
        wc.lpfnWndProc = self._wndproc
        wc.hInstance = hinst
        wc.lpszClassName = "OscHarnessHost"
        wc.hbrBackground = self.gdi32.CreateSolidBrush(
            bridge_smoke.HOST_COLOUR_REF)
        u.RegisterClassW(ctypes_.byref(wc))
        x, y, w, h = self.geometry
        # WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, shown without activation;
        # WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: no taskbar button, no focus.
        self.hwnd = u.CreateWindowExW(0x00000080 | 0x08000000,
                                      "OscHarnessHost", "osc harness",
                                      0x02CF0000, x, y, w, h, None, None,
                                      hinst, None)
        u.SetWindowPos.argtypes = [wt_.HWND, wt_.HWND, ctypes_.c_int,
                                   ctypes_.c_int, ctypes_.c_int,
                                   ctypes_.c_int, wt_.UINT]
        # Exact client size.
        r = wt_.RECT()
        u.GetClientRect(self.hwnd, ctypes_.byref(r))
        cw, ch = self.want_client
        w2 = w + (cw - (r.right - r.left))
        h2 = h + (ch - (r.bottom - r.top))
        u.SetWindowPos(self.hwnd, None, x, y, w2, h2, 0x0010 | 0x0004)
        u.ShowWindow(self.hwnd, 4)       # SW_SHOWNOACTIVATE
        self._ready.set()
        self._frame = (w2 - cw, h2 - ch)
        msg = wt_.MSG()
        while u.GetMessageW(ctypes_.byref(msg), None, 0, 0) > 0:
            u.TranslateMessage(ctypes_.byref(msg))
            u.DispatchMessageW(ctypes_.byref(msg))

    def resize_client(self, cw, ch):
        """The client area to ``cw`` x ``ch``, in place, never activated (a
        stand-in for FluentFin's full screen, which resizes the video's
        window). The embedded mpv follows its parent."""
        fw, fh = self._frame
        self.user32.SetWindowPos(self.hwnd, None, 0, 0, cw + fw, ch + fh,
                                 0x0002 | 0x0004 | 0x0010)  # NOMOVE|NOZORDER|NOACTIVATE
        time.sleep(0.5)


# ---------------------------------------------------------------- mpv ipc

class Ipc:
    def __init__(self, name, timeout=30):
        path = r"\\.\pipe\%s" % name
        deadline = time.time() + timeout
        while True:
            try:
                self.fh = open(path, "r+b", buffering=0)
                break
            except OSError:
                if time.time() > deadline:
                    raise
                time.sleep(0.2)
        self.buf = b""
        self.rid = 0
        self.cmd(["disable_event", "all"])

    def _line(self):
        while b"\n" not in self.buf:
            chunk = self.fh.read(65536)
            if not chunk:
                raise EOFError
            self.buf += chunk
        line, self.buf = self.buf.split(b"\n", 1)
        return json.loads(line)

    def cmd(self, args):
        self.rid += 1
        self.fh.write((json.dumps({"command": args, "request_id": self.rid})
                       + "\n").encode())
        while True:
            msg = self._line()
            if msg.get("request_id") == self.rid:
                return msg

    def get(self, prop):
        return self.cmd(["get_property", prop]).get("data")


# ---------------------------------------------------------------- bridge

class Bridge(bridge_smoke.BridgeProcess):
    """BridgeProcess, but importing the package from ``backend`` -- a
    ``-m`` run would take the venv's editable install (the main repo)."""

    def __init__(self, python, out_dir, name, backend, conf, env):
        self.name = name
        self.config_dir = os.path.join(out_dir, name + "-config")
        os.makedirs(self.config_dir, exist_ok=True)
        with open(os.path.join(self.config_dir, "conf.json"), "w",
                  encoding="utf-8") as fh:
            json.dump(conf, fh)
        # Silent, always: the test config's own mpv.conf (never the user's).
        with open(os.path.join(self.config_dir, "mpv.conf"), "w",
                  encoding="utf-8") as fh:
            fh.write("ao=null\nmute=yes\n")
        self.log_file = os.path.join(out_dir, name + "-bridge.log")
        self.stderr_path = os.path.join(out_dir, name + "-stderr.txt")
        self._stderr = open(self.stderr_path, "wb")
        # --text-scale: Windows' text size as the backend would read it from
        # the registry, set in this bridge process only (the user's
        # registry is never touched).
        scale = (env or {}).get("OSC_HARNESS_TEXT_SCALE")
        patch = ""
        if scale:
            patch = ("from jellyfin_mpv_shim import fluentfin_osc as _f; "
                     "_f.text_scale = lambda value=None: %r; "
                     % (int(scale) / 100.0))
        boot = ("import sys; sys.path.insert(0, %r); %s"
                "from jellyfin_mpv_shim.bridge import main; "
                "sys.exit(main(%r))"
                % (backend, patch, ["--config-dir", self.config_dir,
                                    "--log-file", self.log_file]))
        self.proc = subprocess.Popen([python, "-I", "-c", boot],
                                     stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE,
                                     stderr=self._stderr, cwd=out_dir,
                                     env=env)
        self.lines, self.messages, self.not_json = [], [], []
        self._cond = threading.Condition()
        self._next_id = 0
        self._reader = threading.Thread(target=self._read, daemon=True)
        self._reader.start()


def shot(host, path):
    _x, _y, w, h = host.client_on_screen()
    bridge_smoke.screenshot(path, host.hwnd, w, h)
    print("    shot: %s (%dx%d)" % (path, w, h), flush=True)
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--backend", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--mkv", required=True)
    ap.add_argument("--size", default="1280x720")
    ap.add_argument("--tag", default="run")
    ap.add_argument("--python", default=sys.executable,
                    help="interpreter with the shim's dependencies")
    ap.add_argument("--pos", default="-3000,100",
                    help="window position (off-screen by default)")
    ap.add_argument("--name", default=SERIES,
                    help="the episode's Name (default: the series name, "
                    "as on the user's server)")
    ap.add_argument("--movie", action="store_true")
    ap.add_argument("--hover-y", type=float, default=0.979)
    ap.add_argument("--text-scale", type=int, default=None)
    ap.add_argument("--osc-style", default="mpvtk",
                    help="conf.json osc_style (mpvtk: uosc, mpv: the fork)")
    ap.add_argument("--fullscreen", action="store_true")
    ap.add_argument("--fs-size", default="1920x1080")
    ap.add_argument("--runtime-opt", action="append", default=[],
                    help="script-opts entry appended over IPC once playing "
                    "(e.g. osc-scalewindowed=1.5, a Windows text scale)")
    a = ap.parse_args()
    job = own_job()  # noqa: F841
    out = os.path.abspath(a.out)
    os.makedirs(out, exist_ok=True)
    cw, ch = (int(v) for v in a.size.split("x"))
    px, py = (int(v) for v in a.pos.split(","))

    media_dir = os.path.dirname(os.path.abspath(a.mkv))
    srts = {2: os.path.join(media_dir, "eng.srt"),
            3: os.path.join(media_dir, "spa.srt")}
    make_srt(srts[2], "English")
    make_srt(srts[3], "Spanish")
    server = EpisodeJF(a.mkv, srts, a.name, movie=a.movie)

    env = dict(os.environ)
    sandbox = os.path.join(out, "env-" + a.tag)
    for k in ("APPDATA", "LOCALAPPDATA", "TEMP", "TMP"):
        env[k] = os.path.join(sandbox, k.lower())
        os.makedirs(env[k], exist_ok=True)
    pipe = "osc-harness-%d-%s" % (os.getpid(), a.tag)
    conf = {"mpv_ext_ipc": pipe, "mpv_ext_path": MPV,
            "mpv_log_level": "info", "osc_style": a.osc_style}
    if a.text_scale:
        env["OSC_HARNESS_TEXT_SCALE"] = str(a.text_scale)
    host = Host(cw, ch, px, py)
    python = a.python
    b = Bridge(python, out, a.tag, os.path.abspath(a.backend), conf, env)
    report = {"tag": a.tag, "size": a.size}
    try:
        b.wait_event("hello", timeout=30)
        r = b.request("connect", {
            "protocol": 1,
            "server": {"url": server.url, "id": "srvosc", "name": "fake"},
            "user": {"id": USER, "name": "u"},
            "accessToken": TOKEN,
            "device": {"id": "osc-harness", "name": "osc harness"},
            "client": {"name": "FluentFin", "version": "0.0.1"},
            "window": {"mode": "embedded", "hwnd": host.hwnd}}, timeout=90)
        print("connect:", r, flush=True)
        ipc = Ipc(pipe)
        mark = b.mark()
        r = b.request("play", {"itemIds": [ITEM, ITEM2]}, timeout=60)
        print("play:", r, flush=True)
        st = b.wait_event("state", lambda d: d.get("state") == "playing",
                          timeout=60, since=mark)
        print("playing:", bool(st), flush=True)
        time.sleep(3)
        for opt in a.runtime_opt:
            print("runtime opt:", opt, ipc.cmd(
                ["change-list", "script-opts", "append", opt]), flush=True)
        b.request("seek", {"positionTicks": 31 * 10 ** 7})
        time.sleep(1.5)
        b.request("pause")
        time.sleep(1.5)
        tracks = ipc.get("track-list")
        report["tracks"] = [{k: t.get(k) for k in (
            "id", "type", "lang", "title", "external", "selected", "codec")}
            for t in tracks]
        for p in ("media-title", "sub-pos", "osd-font", "script-opts",
                  "osd-dimensions", "mpv-version", "aid", "sid",
                  "osd-fonts-dir", "volume-max", "user-data/fluentfin"):
            report[p] = ipc.get(p)
        uosc = report["user-data/fluentfin"] is not None and (
            report["user-data/fluentfin"].get("bar") is not None)
        report["bar"] = "uosc" if uosc else "fork"
        report["segments"] = [m for m in b.messages
                              if m.get("event") in ("segments", "next")]
        print(json.dumps(report, indent=1), flush=True)

        def show(always):
            if uosc:
                ipc.cmd(["script-message-to", "uosc", "set-min-visibility",
                         "1" if always else "0"])
            else:
                ipc.cmd(["script-message", "osc-visibility",
                         "always" if always else "auto", "no-osd"])

        report["shot_hidden"] = shot(host, os.path.join(
            out, "%s-1-osc-hidden.png" % a.tag))
        show(True)
        time.sleep(2.5)
        report["sub_pos_shown"] = ipc.get("sub-pos")
        report["shot_shown"] = shot(host, os.path.join(
            out, "%s-2-osc-shown.png" % a.tag))
        # Hover the middle of the seek bar (the bar row is the last ~4%).
        ipc.cmd(["mouse", str(cw // 2), str(int(ch * a.hover_y))])
        time.sleep(0.4)
        ipc.cmd(["mouse", str(cw // 2 + 3), str(int(ch * a.hover_y))])
        time.sleep(2.5)
        ipc.cmd(["mouse", str(cw // 2 + 6), str(int(ch * a.hover_y))])
        time.sleep(0.6)
        report["shot_hover"] = shot(host, os.path.join(
            out, "%s-3-seekbar-hover.png" % a.tag))
        # The wheel: over the bar (the OSC's input section), then over the
        # video; and the horizontal wheel must not seek.
        wheel = {"start": ipc.get("volume"), "pos0": ipc.get("time-pos")}
        ipc.cmd(["keypress", "WHEEL_DOWN"])
        time.sleep(0.3)
        wheel["bar_down"] = ipc.get("volume")
        ipc.cmd(["mouse", str(cw // 2), str(ch // 3)])
        time.sleep(0.3)
        for _ in range(3):
            ipc.cmd(["keypress", "WHEEL_UP"])
            time.sleep(0.15)
        wheel["video_up3"] = ipc.get("volume")
        ipc.cmd(["keypress", "WHEEL_LEFT"])
        ipc.cmd(["keypress", "WHEEL_RIGHT"])
        time.sleep(0.5)
        wheel["pos1"] = ipc.get("time-pos")
        report["wheel"] = wheel
        print("wheel:", wheel, flush=True)
        report["shot_wheel"] = shot(host, os.path.join(
            out, "%s-3b-after-wheel.png" % a.tag))
        if a.fullscreen:
            fw, fh_ = (int(v) for v in a.fs_size.split("x"))
            host.resize_client(fw, fh_)
            b.request("set_fullscreen", {"fullscreen": True})
            time.sleep(2.5)
            ipc.cmd(["mouse", str(fw // 2), str(int(fh_ * 0.9))])
            time.sleep(1.0)
            report["sub_pos_fullscreen"] = ipc.get("sub-pos")
            report["shot_fullscreen"] = shot(host, os.path.join(
                out, "%s-3c-fullscreen.png" % a.tag))
            host.resize_client(cw, ch)
            b.request("set_fullscreen", {"fullscreen": False})
            time.sleep(1.5)
        ipc.cmd(["mouse", str(cw // 2), str(ch // 5)])
        show(False)
        time.sleep(3)
        report["sub_pos_hidden_again"] = ipc.get("sub-pos")
        report["shot_hidden_again"] = shot(host, os.path.join(
            out, "%s-4-osc-hidden-again.png" % a.tag))
        # Auto mode, shown by a mouse move alone (the everyday path).
        ipc.cmd(["mouse", str(cw // 2), str(int(ch * 0.85))])
        time.sleep(0.25)
        report["sub_pos_auto_shown"] = ipc.get("sub-pos")
        time.sleep(3)
        report["sub_pos_auto_hidden"] = ipc.get("sub-pos")
        b.request("stop")
        time.sleep(1)
        r = b.request("shutdown", timeout=40)
        report["shutdown"] = r
        report["exit"] = b.finish(30)
    finally:
        b.kill()
        host.close()
        server.close()
        with open(os.path.join(out, "%s-report.json" % a.tag), "w",
                  encoding="utf-8") as fh:
            json.dump(report, fh, indent=1)
    print(json.dumps({k: v for k, v in report.items()
                      if k.startswith("sub_pos") or k in ("exit",)}),
          flush=True)


if __name__ == "__main__":
    main()
