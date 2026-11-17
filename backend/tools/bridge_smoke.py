#!/usr/bin/env python3
"""End-to-end smoke test for the bridge (docs/bridge.md section 6).

Spawns ``python -I -m jellyfin_mpv_shim.bridge`` as a child process, exactly
the way FluentFin does, and drives it over stdin/stdout. Needs no Jellyfin
server and no credentials: the server-less half of the protocol is checked
as-is, and playback goes through ``--dev``'s ``play_file`` with mpv's own
test pattern (``av://lavfi:testsrc``), which needs no file either.

Four sessions, each a fresh process with a fresh config directory:

* **basic** -- ``hello`` first, ``ping``, ``get_state`` before ``connect``,
  ``play`` before ``connect`` (``not_connected``), an unknown command
  (``unknown_cmd``), ``play_file`` without ``--dev`` (``unknown_cmd``), a
  garbage line (no response: an ``error`` event with ``bad_args``, and the
  process keeps answering), bad ``connect`` args, ``shutdown`` -> exit 0.
* **eof** -- closing stdin is a shutdown: exit 0.
* **standalone** (``--dev``) -- window-only ``connect``; a ``connect`` to a
  dead server (127.0.0.1:9) is ``server_unreachable`` and its token appears
  nowhere in stdout, stderr or the log file; ``play_file`` reaches
  ``playing`` and ``position`` advances for ~3 s; pause, volume, a second
  ``connect`` with another window (``window_locked``), ``stop`` (``ended``
  ``stopped``, ``idle``), ``shutdown`` -> exit 0.
* **embedded** (``--dev``, Windows) -- a plain Win32 window made here with
  ctypes, its hwnd handed over; ``play_file`` for ~3 s, then a screenshot of
  the window's client area (``PrintWindow`` on the window itself, never a
  screen copy, so nothing on top of it is captured) is saved and checked for the test pattern; a double-click on the video and
  the ``f`` key each produce ``fullscreen_request`` and leave the window
  where it was; ``set_fullscreen`` lands in ``state``; ``shutdown``.
* **prewarm** (``--dev``, Windows) -- the same kind of window, with
  ``connect.prewarm`` (BRIDGE.md section 4): mpv runs but makes no window
  inside ours until the first ``play_file``, which makes it and draws there
  (screenshot checked); it stays after ``stop``.

Every session also asserts that nothing but JSON objects ever appeared on
stdout. Each session's config dir gets an ``mpv.conf`` with ``ao=null``: no
sound from a test run (the test pattern has no audio anyway).

    backend\\.venv\\Scripts\\python.exe backend\\tools\\bridge_smoke.py [--out DIR] [--this-checkout]

``--this-checkout`` runs the bridge from the checkout this file is in
(``python -E -s``, cwd = its backend folder) instead of ``-I``, which
imports the installed package -- with an editable install, the checkout it
was made from (a git worktree shares the main checkout's venv).

Exit status 0 when every check passed. The screenshot path is printed; look
at it -- the pixel check is a backstop, not a substitute.
"""

import argparse
import json
import os
import subprocess
import sys
import tempfile
import threading
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TESTSRC = "av://lavfi:testsrc=size=1280x720:rate=30"
TOKEN = "smoke-token-5f3c2a9e8d7b6a01"
#: Set by --this-checkout: run the bridge from the checkout this file is in.
THIS_CHECKOUT = False
#: The host window's background: brown, which testsrc never draws.
HOST_RGB = (0x80, 0x60, 0x40)
HOST_COLOUR_REF = HOST_RGB[0] | (HOST_RGB[1] << 8) | (HOST_RGB[2] << 16)


class Results:
    def __init__(self):
        self.rows = []

    def check(self, session, name, ok, detail=""):
        self.rows.append((session, name, bool(ok), detail))
        mark = "PASS" if ok else "FAIL"
        print("  [%s] %s%s" % (mark, name, (" -- %s" % detail) if detail
                                and not ok else ""), flush=True)
        return ok

    @property
    def failed(self):
        return [r for r in self.rows if not r[2]]


class BridgeProcess:
    """One bridge child process and everything it wrote."""

    def __init__(self, python, out_dir, name, dev=False, conf=None):
        self.name = name
        self.config_dir = os.path.join(out_dir, name + "-config")
        os.makedirs(self.config_dir, exist_ok=True)
        # No sound from a test run: mpv reads mpv.conf from the shim's
        # config dir (mpv_options: config_dir), this run's own.
        with open(os.path.join(self.config_dir, "mpv.conf"), "w",
                  encoding="utf-8") as fh:
            fh.write("ao=null" + chr(10))
        if conf is not None:
            with open(os.path.join(self.config_dir, "conf.json"), "w",
                      encoding="utf-8") as fh:
                json.dump(conf, fh)
        self.log_file = os.path.join(out_dir, name + "-bridge.log")
        self.stderr_path = os.path.join(out_dir, name + "-stderr.txt")
        self._stderr = open(self.stderr_path, "wb")
        # -I as FluentFin starts it, which imports whatever the interpreter
        # has installed (an editable install points at one checkout); with
        # --this-checkout, the code next to this file (-E -s: still no
        # PYTHON* variables and no user site; ROOT, the cwd, comes first).
        isolation = ["-E", "-s"] if THIS_CHECKOUT else ["-I"]
        argv = [python] + isolation + ["-m", "jellyfin_mpv_shim.bridge",
                "--config-dir", self.config_dir, "--log-file", self.log_file]
        if dev:
            argv.append("--dev")
        self.proc = subprocess.Popen(argv, stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE,
                                     stderr=self._stderr, cwd=ROOT)
        self.lines = []          # raw stdout lines
        self.messages = []       # parsed, in order
        self.not_json = []
        self._cond = threading.Condition()
        self._next_id = 0
        self._reader = threading.Thread(target=self._read, daemon=True)
        self._reader.start()

    def _read(self):
        for raw in self.proc.stdout:
            with self._cond:
                self.lines.append(raw)
                try:
                    text = raw.decode("utf-8")
                    if text.startswith("﻿") or not text.endswith("\n"):
                        raise ValueError("BOM or unterminated line")
                    msg = json.loads(text)
                    if not isinstance(msg, dict):
                        raise ValueError("not an object")
                    self.messages.append(msg)
                except Exception as error:
                    self.not_json.append((raw, str(error)))
                self._cond.notify_all()
        with self._cond:
            self._cond.notify_all()

    def send_raw(self, data):
        self.proc.stdin.write(data)
        self.proc.stdin.flush()

    def request(self, cmd, args=None, timeout=15):
        with self._cond:
            self._next_id += 1
            req_id = self._next_id
        msg = {"id": req_id, "cmd": cmd}
        if args is not None:
            msg["args"] = args
        self.send_raw((json.dumps(msg) + "\n").encode("utf-8"))
        found = self.wait_for(lambda m: m.get("id") == req_id, timeout)
        return found[1] if found else None

    def mark(self):
        with self._cond:
            return len(self.messages)

    def wait_for(self, predicate, timeout=15, since=0):
        deadline = time.monotonic() + timeout
        with self._cond:
            while True:
                for index in range(since, len(self.messages)):
                    if predicate(self.messages[index]):
                        return index, self.messages[index]
                remaining = deadline - time.monotonic()
                if remaining <= 0 or self.proc.poll() is not None and \
                        not self._reader.is_alive():
                    return None
                self._cond.wait(min(remaining, 0.2))

    def wait_event(self, name, predicate=None, timeout=15, since=0):
        found = self.wait_for(
            lambda m: m.get("event") == name
            and (predicate is None or predicate(m.get("data") or {})),
            timeout, since)
        return found[1]["data"] if found else None

    def events(self, name, since=0):
        with self._cond:
            return [m["data"] for m in self.messages[since:]
                    if m.get("event") == name]

    def finish(self, timeout=30):
        try:
            return self.proc.wait(timeout)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(10)
            return None
        finally:
            self._reader.join(5)
            self._stderr.close()

    def kill(self):
        if self.proc.poll() is None:
            self.proc.kill()
        self.finish(10)


def ok(response):
    return bool(response) and response.get("ok") is True


def error_code(response):
    if not response or response.get("ok") is not False:
        return None
    return (response.get("error") or {}).get("code")


def check_stdout(results, session, bridge):
    results.check(session, "stdout carried nothing but JSON objects "
                  "(%d lines)" % len(bridge.lines), not bridge.not_json,
                  repr(bridge.not_json[:3]))


# ---------------------------------------------------------------------------
# Sessions


def check_no_reported(results, session, bridge):
    """BRIDGE.md section 5: ``reported`` is never sent for ``play_file``
    -- it has no play session -- nor at its shutdown."""
    reported = bridge.events("reported")
    results.check(session, "play_file: no reported event", not reported,
                  repr(reported))


def session_basic(results, python, out_dir):
    s = "basic"
    print("\n== %s" % s, flush=True)
    b = BridgeProcess(python, out_dir, s)
    try:
        first = b.wait_for(lambda m: True, 20)
        results.check(s, "hello is the first message",
                      first and first[0] == 0
                      and first[1].get("event") == "hello"
                      and first[1]["data"].get("protocol") == 1,
                      repr(first))
        results.check(s, "ping", ok(b.request("ping")))
        state = b.request("get_state")
        results.check(s, "get_state before connect is idle/unconnected",
                      ok(state) and state["result"]["state"] == "idle"
                      and state["result"]["connected"] is False
                      and state["result"]["window"] is None, repr(state))
        results.check(s, "play before connect -> not_connected",
                      error_code(b.request("play", {"itemIds": ["x"]}))
                      == "not_connected")
        results.check(s, "seek before connect -> not_connected",
                      error_code(b.request("seek", {"positionTicks": 1}))
                      == "not_connected")
        results.check(s, "unknown command -> unknown_cmd",
                      error_code(b.request("frobnicate")) == "unknown_cmd")
        results.check(s, "play_file without --dev -> unknown_cmd",
                      error_code(b.request("play_file", {"url": TESTSRC}))
                      == "unknown_cmd")
        mark = b.mark()
        b.send_raw(b"this is not json\n")
        b.send_raw(b'{"cmd": "ping"}\n')
        bad = b.wait_event("error", lambda d: d.get("code") == "bad_args",
                           since=mark)
        results.check(s, "garbage line -> error event bad_args, no response",
                      bad is not None and bad.get("fatal") is False, repr(bad))
        results.check(s, "still answering after garbage", ok(b.request("ping")))
        results.check(s, "missing cmd with an id -> bad_args",
                      _raw_request(b, b'{"id": 900, "args": {}}\n')
                      == "bad_args")
        results.check(s, "connect with protocol 2 -> bad_args",
                      error_code(b.request("connect", {
                          "protocol": 2, "window": {"mode": "standalone"}}))
                      == "bad_args")
        results.check(s, "connect without server outside --dev -> bad_args",
                      error_code(b.request("connect", {
                          "protocol": 1, "window": {"mode": "standalone"}}))
                      == "bad_args")
        results.check(s, "embedded connect with hwnd 0 -> bad_args",
                      error_code(b.request("connect", {
                          "protocol": 1,
                          "window": {"mode": "embedded", "hwnd": 0}}))
                      == "bad_args")
        results.check(s, "shutdown -> ok", ok(b.request("shutdown")))
        code = b.finish()
        results.check(s, "exit status 0", code == 0, repr(code))
        check_stdout(results, s, b)
    finally:
        b.kill()


def _raw_request(b, data):
    mark = b.mark()
    b.send_raw(data)
    found = b.wait_for(lambda m: m.get("id") == 900, 10, since=mark)
    return error_code(found[1]) if found else None


def session_eof(results, python, out_dir):
    s = "eof"
    print("\n== %s" % s, flush=True)
    b = BridgeProcess(python, out_dir, s)
    try:
        results.check(s, "hello", b.wait_event("hello", timeout=20) is not None)
        b.proc.stdin.close()
        code = b.finish()
        results.check(s, "stdin EOF -> exit 0", code == 0, repr(code))
        check_stdout(results, s, b)
    finally:
        b.kill()


def _play_testsrc(results, s, b):
    mark = b.mark()
    started = time.monotonic()
    response = b.request("play_file", {"url": TESTSRC, "title": "testsrc"})
    results.check(s, "play_file accepted", ok(response), repr(response))
    playing = b.wait_event("state", lambda d: d.get("state") == "playing",
                           timeout=20, since=mark)
    results.check(s, "state reaches playing", playing is not None)
    if playing is not None:
        print("    play_file -> state playing: %.0f ms"
              % ((time.monotonic() - started) * 1000), flush=True)
    start = b.mark()
    time.sleep(3.2)
    positions = [p["positionTicks"] for p in b.events("position", start)]
    results.check(s, "position events advance over ~3 s (%d events)"
                  % len(positions),
                  len(positions) >= 4 and all(
                      later > earlier for earlier, later
                      in zip(positions, positions[1:])), repr(positions))
    return playing is not None


def session_standalone(results, python, out_dir):
    s = "standalone"
    print("\n== %s" % s, flush=True)
    # Debug logging, so the apiclient dumps its request headers and the
    # redaction has the Authorization line to deal with.
    b = BridgeProcess(python, out_dir, s, dev=True,
                      conf={"mpv_log_level": "debug"})
    try:
        b.wait_event("hello", timeout=20)
        response = b.request("connect", {"protocol": 1,
                                         "window": {"mode": "standalone"}},
                             timeout=60)
        results.check(s, "window-only connect (dev) -> mpv version",
                      ok(response) and response["result"].get("mpv")
                      and response["result"].get("mpvBackend")
                      in ("external", "libmpv"), repr(response))
        if ok(response):
            print("    mpv: %s (%s)" % (response["result"]["mpv"],
                                        response["result"]["mpvBackend"]))
        response = b.request("connect", {
            "protocol": 1,
            "server": {"url": "http://127.0.0.1:9", "id": "s", "name": "x"},
            "user": {"id": "0123456789abcdef0123456789abcdef", "name": "u"},
            "accessToken": TOKEN,
            "device": {"id": "smoke-device", "name": "Smoke"},
            "client": {"name": "FluentFin", "version": "0.0.1"},
            "window": {"mode": "standalone"},
        }, timeout=60)
        results.check(s, "connect to a dead server -> server_unreachable",
                      error_code(response) == "server_unreachable",
                      repr(response))
        state = b.request("get_state")
        results.check(s, "still not connected after the failed connect",
                      ok(state) and state["result"]["connected"] is False)
        if _play_testsrc(results, s, b):
            mark = b.mark()
            results.check(s, "pause", ok(b.request("pause")))
            results.check(s, "state paused", b.wait_event(
                "state", lambda d: d.get("state") == "paused", 5, mark))
            mark = b.mark()
            results.check(s, "set_volume 40", ok(b.request(
                "set_volume", {"volume": 40})))
            results.check(s, "unpause", ok(b.request("unpause")))
            results.check(s, "state playing at volume 40", b.wait_event(
                "state", lambda d: d.get("state") == "playing"
                and d.get("volume") == 40, 5, mark))
            results.check(s, "set_volume 101 -> bad_args", error_code(
                b.request("set_volume", {"volume": 101})) == "bad_args")
            results.check(s, "seek with a string -> bad_args", error_code(
                b.request("seek", {"positionTicks": "10"})) == "bad_args")
            results.check(s, "set_audio with nothing from Jellyfin -> "
                          "no_playback", error_code(
                              b.request("set_audio", {"index": 1}))
                          == "no_playback")
            results.check(s, "a different window -> window_locked",
                          error_code(b.request("connect", {
                              "protocol": 1, "window": {
                                  "mode": "embedded", "hwnd": 12345}}))
                          == "window_locked")
            results.check(s, "the same window again -> ok", ok(b.request(
                "connect", {"protocol": 1,
                            "window": {"mode": "standalone"}})))
            mark = b.mark()
            results.check(s, "stop", ok(b.request("stop")))
            ended = b.wait_event("ended", timeout=10, since=mark)
            results.check(s, "ended reason stopped",
                          ended and ended.get("reason") == "stopped",
                          repr(ended))
            results.check(s, "state idle after stop", b.wait_event(
                "state", lambda d: d.get("state") == "idle", 10, mark))
        results.check(s, "shutdown", ok(b.request("shutdown", timeout=40)))
        code = b.finish()
        results.check(s, "exit status 0", code == 0, repr(code))
        check_stdout(results, s, b)
        check_no_reported(results, s, b)
        leaked = []
        for path in (b.stderr_path, b.log_file):
            with open(path, "rb") as fh:
                if TOKEN.encode() in fh.read():
                    leaked.append(os.path.basename(path))
        if any(TOKEN.encode() in line for line in b.lines):
            leaked.append("stdout")
        results.check(s, "the access token is in no output", not leaked,
                      repr(leaked))
    finally:
        b.kill()


# ---------------------------------------------------------------------------
# Embedded: a Win32 window of our own


class HostWindow:
    """A plain top-level window with a brown background, pumped on its own
    thread -- the stand-in for FluentFin's player panel. Brown because
    testsrc never draws it (its bars and gradient are all full-brightness
    hues), so a screenshot of a window mpv did not draw into is
    unmistakable."""

    def __init__(self, x=120, y=120, width=960, height=540):
        import ctypes
        from ctypes import wintypes

        self.ctypes = ctypes
        self.wt = wintypes
        self.user32 = ctypes.WinDLL("user32", use_last_error=True)
        self.gdi32 = ctypes.WinDLL("gdi32")
        self.kernel32 = ctypes.WinDLL("kernel32")
        u = self.user32
        u.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT,
                                     wintypes.WPARAM, wintypes.LPARAM]
        u.DefWindowProcW.restype = ctypes.c_ssize_t
        u.CreateWindowExW.restype = wintypes.HWND
        u.CreateWindowExW.argtypes = [
            wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR,
            wintypes.DWORD, ctypes.c_int, ctypes.c_int, ctypes.c_int,
            ctypes.c_int, wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE,
            wintypes.LPVOID]
        u.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT,
                                   wintypes.WPARAM, wintypes.LPARAM]
        u.EnableWindow.argtypes = [wintypes.HWND, wintypes.BOOL]
        u.GetWindowRect.argtypes = [wintypes.HWND,
                                    ctypes.POINTER(wintypes.RECT)]
        u.GetClientRect.argtypes = [wintypes.HWND,
                                    ctypes.POINTER(wintypes.RECT)]
        u.ClientToScreen.argtypes = [wintypes.HWND,
                                     ctypes.POINTER(wintypes.POINT)]
        u.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR,
                                     ctypes.c_int]
        u.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR,
                                    ctypes.c_int]
        self.gdi32.CreateSolidBrush.restype = wintypes.HBRUSH
        try:
            u.SetProcessDPIAware()
        except Exception:
            pass
        self.WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wintypes.HWND,
                                          wintypes.UINT, wintypes.WPARAM,
                                          wintypes.LPARAM)
        self.geometry = (x, y, width, height)
        self.hwnd = None
        self._ready = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        self._ready.wait(10)

    def _run(self):
        ctypes, wt, u = self.ctypes, self.wt, self.user32

        class WNDCLASSW(ctypes.Structure):
            _fields_ = [("style", wt.UINT), ("lpfnWndProc", self.WNDPROC),
                        ("cbClsExtra", ctypes.c_int),
                        ("cbWndExtra", ctypes.c_int),
                        ("hInstance", wt.HINSTANCE), ("hIcon", wt.HICON),
                        ("hCursor", wt.HANDLE), ("hbrBackground", wt.HBRUSH),
                        ("lpszMenuName", wt.LPCWSTR),
                        ("lpszClassName", wt.LPCWSTR)]

        def wndproc(hwnd, msg, wparam, lparam):
            if msg == 0x0002:           # WM_DESTROY
                u.PostQuitMessage(0)
                return 0
            return u.DefWindowProcW(hwnd, msg, wparam, lparam)

        self._wndproc = self.WNDPROC(wndproc)
        hinst = self.kernel32.GetModuleHandleW(None)
        wc = WNDCLASSW()
        wc.lpfnWndProc = self._wndproc
        wc.hInstance = hinst
        wc.lpszClassName = "BridgeSmokeHost"
        wc.hbrBackground = self.gdi32.CreateSolidBrush(HOST_COLOUR_REF)
        u.RegisterClassW(ctypes.byref(wc))
        x, y, w, h = self.geometry
        # WS_OVERLAPPEDWINDOW | WS_VISIBLE | WS_CLIPCHILDREN, and no extended
        # style: not topmost. The screenshot is PrintWindow on this window,
        # which draws it whatever lies over it, so it need not sit over the
        # user's desktop.
        self.hwnd = u.CreateWindowExW(0, "BridgeSmokeHost",
                                      "bridge smoke host", 0x16CF0000,
                                      x, y, w, h, None, None, hinst, None)
        self._ready.set()
        msg = wt.MSG()
        while u.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            u.TranslateMessage(ctypes.byref(msg))
            u.DispatchMessageW(ctypes.byref(msg))

    def rect(self):
        r = self.wt.RECT()
        self.user32.GetWindowRect(self.hwnd, self.ctypes.byref(r))
        return (r.left, r.top, r.right, r.bottom)

    def title(self):
        buf = self.ctypes.create_unicode_buffer(256)
        self.user32.GetWindowTextW(self.hwnd, buf, 256)
        return buf.value

    def client_on_screen(self):
        r = self.wt.RECT()
        self.user32.GetClientRect(self.hwnd, self.ctypes.byref(r))
        p = self.wt.POINT(0, 0)
        self.user32.ClientToScreen(self.hwnd, self.ctypes.byref(p))
        return p.x, p.y, r.right - r.left, r.bottom - r.top

    def children(self):
        found = []
        callback_type = self.ctypes.WINFUNCTYPE(self.wt.BOOL, self.wt.HWND,
                                                self.wt.LPARAM)

        def callback(hwnd, _):
            buf = self.ctypes.create_unicode_buffer(128)
            self.user32.GetClassNameW(hwnd, buf, 128)
            found.append((hwnd, buf.value))
            return True

        self.user32.EnumChildWindows(self.hwnd, callback_type(callback), 0)
        return found

    def enable_children(self):
        """mpv disables its embedded window so input goes to the owner;
        FluentFin re-enables it, and so do we, to click it."""
        for hwnd, _cls in self.children():
            self.user32.EnableWindow(hwnd, True)

    def post_double_click(self, target):
        x, y = self.client_on_screen()[2] // 2, self.client_on_screen()[3] // 2
        lparam = (y << 16) | (x & 0xFFFF)
        for _ in range(2):
            self.user32.PostMessageW(target, 0x0200, 0, lparam)  # MOUSEMOVE
            self.user32.PostMessageW(target, 0x0201, 1, lparam)  # LBUTTONDOWN
            self.user32.PostMessageW(target, 0x0202, 0, lparam)  # LBUTTONUP
            time.sleep(0.05)

    def post_key(self, target, vk):
        scan = self.user32.MapVirtualKeyW(vk, 0)
        down = 1 | (scan << 16)
        up = down | (1 << 30) | (1 << 31)
        self.user32.PostMessageW(target, 0x0100, vk, down)   # WM_KEYDOWN
        time.sleep(0.03)
        self.user32.PostMessageW(target, 0x0101, vk, up)     # WM_KEYUP

    def close(self):
        if self.hwnd:
            self.user32.PostMessageW(self.hwnd, 0x0010, 0, 0)   # WM_CLOSE
            self._thread.join(5)


def screenshot(path, hwnd, width, height):
    """The window's client area, drawn by the window itself: PrintWindow
    with PW_CLIENTONLY | PW_RENDERFULLCONTENT (3), which includes mpv's
    child. Never a copy of the screen, so nothing on top of the window (a
    notification, another app) is captured. DPI-aware first, so the size is
    in the same physical pixels ours is."""
    script = (
        "Add-Type -AssemblyName System.Drawing;"
        "Add-Type -TypeDefinition 'using System; "
        "using System.Runtime.InteropServices;"
        "public static class SmokeShot { [DllImport(\"user32.dll\")] public "
        "static extern bool SetProcessDPIAware(); [DllImport(\"user32.dll\")]"
        " public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, "
        "uint flags); }';"
        "[SmokeShot]::SetProcessDPIAware() | Out-Null;"
        "$b = New-Object System.Drawing.Bitmap %d, %d;"
        "$g = [System.Drawing.Graphics]::FromImage($b);"
        "$hdc = $g.GetHdc();"
        "$ok = [SmokeShot]::PrintWindow([IntPtr]%d, $hdc, 3);"
        "$g.ReleaseHdc($hdc);"
        "if (-not $ok) { $g.Dispose(); $b.Dispose(); exit 2 };"
        "$b.Save('%s', [System.Drawing.Imaging.ImageFormat]::Png);"
        "$g.Dispose(); $b.Dispose()"
        % (width, height, hwnd, path.replace("'", "''")))
    subprocess.run(["powershell", "-NoProfile", "-NonInteractive",
                    "-Command", script], check=True, timeout=60,
                   capture_output=True)
    return path


def analyse(path):
    """(distinct colours in a 64x36 sample, fraction showing the host
    window's own background), or None without Pillow."""
    try:
        from PIL import Image
    except Exception:
        return None
    with Image.open(path) as img:
        data = img.convert("RGB").resize((64, 36)).tobytes()
    pixels = [tuple(data[i:i + 3]) for i in range(0, len(data), 3)]
    host = sum(1 for pixel in pixels
               if all(abs(a - b) <= 16 for a, b in zip(pixel, HOST_RGB)))
    return len(set(pixels)), host / len(pixels)


def session_embedded(results, python, out_dir):
    s = "embedded"
    print("\n== %s" % s, flush=True)
    host = HostWindow()
    if not host.hwnd:
        results.check(s, "host window created", False)
        return
    rect_before, title_before = host.rect(), host.title()
    b = BridgeProcess(python, out_dir, s, dev=True)
    try:
        b.wait_event("hello", timeout=20)
        started = time.monotonic()
        response = b.request("connect", {
            "protocol": 1, "window": {"mode": "embedded", "hwnd": host.hwnd}},
            timeout=60)
        results.check(s, "embedded connect (hwnd %d)" % host.hwnd,
                      ok(response), repr(response))
        if not ok(response):
            return
        print("    connect answered in %.0f ms"
              % ((time.monotonic() - started) * 1000), flush=True)
        time.sleep(1.0)
        mpv_children = [h for h, cls in host.children() if cls == "mpv"]
        results.check(s, "mpv made a child window inside ours",
                      bool(mpv_children), repr(host.children()))
        if _play_testsrc(results, s, b):
            _x, _y, w, h = host.client_on_screen()
            path = screenshot(os.path.join(out_dir, "embedded-window.png"),
                              host.hwnd, w, h)
            print("    screenshot: %s" % path, flush=True)
            verdict = analyse(path)
            if verdict is not None:
                colours, host_share = verdict
                results.check(s, "screenshot shows video, not the host "
                              "background (%d colours, %.1f%% host colour)"
                              % (colours, host_share * 100),
                              colours >= 12 and host_share < 0.02)
            host.enable_children()
            target = mpv_children[0] if mpv_children else host.hwnd
            mark = b.mark()
            host.post_double_click(target)
            request = b.wait_event("fullscreen_request", timeout=5,
                                   since=mark)
            results.check(s, "double-click -> fullscreen_request true",
                          request == {"fullscreen": True}, repr(request))
            mark = b.mark()
            results.check(s, "set_fullscreen true", ok(b.request(
                "set_fullscreen", {"fullscreen": True})))
            results.check(s, "state fullscreen true", b.wait_event(
                "state", lambda d: d.get("fullscreen") is True, 5, mark))
            mark = b.mark()
            host.post_key(target, 0x46)     # 'F'
            request = b.wait_event("fullscreen_request", timeout=5,
                                   since=mark)
            results.check(s, "f key -> fullscreen_request false",
                          request == {"fullscreen": False}, repr(request))
            results.check(s, "set_fullscreen false", ok(b.request(
                "set_fullscreen", {"fullscreen": False})))
            time.sleep(0.5)
            results.check(s, "no further fullscreen_request (no echo)",
                          len(b.events("fullscreen_request", mark)) == 1,
                          repr(b.events("fullscreen_request", mark)))
            results.check(s, "our window was not moved or resized",
                          host.rect() == rect_before,
                          "%r -> %r" % (rect_before, host.rect()))
            results.check(s, "our window was not retitled",
                          host.title() == title_before, host.title())
        results.check(s, "shutdown", ok(b.request("shutdown", timeout=40)))
        code = b.finish()
        results.check(s, "exit status 0", code == 0, repr(code))
        check_stdout(results, s, b)
        check_no_reported(results, s, b)
    finally:
        b.kill()
        host.close()


def session_prewarm(results, python, out_dir):
    """``connect.prewarm`` (BRIDGE.md section 4) with the real mpv: after
    the connect mpv runs (its version is in the answer) but has made no
    window inside ours; the first ``play_file`` makes it and draws there;
    after ``stop`` it is still there (the surface stays for the process)."""
    s = "prewarm"
    print("\n== %s" % s, flush=True)
    host = HostWindow()
    if not host.hwnd:
        results.check(s, "host window created", False)
        return
    b = BridgeProcess(python, out_dir, s, dev=True)
    try:
        b.wait_event("hello", timeout=20)
        started = time.monotonic()
        response = b.request("connect", {
            "protocol": 1, "prewarm": True,
            "window": {"mode": "embedded", "hwnd": host.hwnd}}, timeout=60)
        connect_ms = (time.monotonic() - started) * 1000
        results.check(s, "pre-warmed connect (hwnd %d)" % host.hwnd,
                      ok(response), repr(response))
        if not ok(response):
            return
        print("    connect answered in %.0f ms: %s"
              % (connect_ms, response.get("result")), flush=True)
        results.check(s, "mpv is running (its version is in the answer)",
                      bool((response.get("result") or {}).get("mpv")),
                      repr(response))
        time.sleep(1.5)
        results.check(s, "no window of mpv's inside ours while idle",
                      not host.children(), repr(host.children()))
        results.check(s, "volume works while idle", ok(b.request(
            "set_volume", {"volume": 60})))
        mark = b.mark()
        started = time.monotonic()
        response = b.request("play_file", {"url": TESTSRC, "title": "t"})
        results.check(s, "play_file accepted", ok(response), repr(response))
        playing = b.wait_event("state", lambda d: d.get("state") == "playing",
                               timeout=20, since=mark)
        results.check(s, "state reaches playing", playing is not None)
        if playing is not None:
            print("    play_file -> state playing: %.0f ms (the video output "
                  "made by this item)" % ((time.monotonic() - started) * 1000),
                  flush=True)
            time.sleep(2.0)
            mpv_children = [h for h, cls in host.children() if cls == "mpv"]
            results.check(s, "the first item made mpv's window inside ours",
                          bool(mpv_children), repr(host.children()))
            _x, _y, w, h = host.client_on_screen()
            path = screenshot(os.path.join(out_dir, "prewarm-window.png"),
                              host.hwnd, w, h)
            print("    screenshot: %s" % path, flush=True)
            verdict = analyse(path)
            if verdict is not None:
                colours, host_share = verdict
                results.check(s, "screenshot shows video, not the host "
                              "background (%d colours, %.1f%% host colour)"
                              % (colours, host_share * 100),
                              colours >= 12 and host_share < 0.02)
            results.check(s, "stop", ok(b.request("stop")))
            b.wait_event("state", lambda d: d.get("state") == "idle", 10)
            time.sleep(1.0)
            results.check(s, "mpv's window stays after stop",
                          [h for h, cls in host.children() if cls == "mpv"]
                          == mpv_children, repr(host.children()))
        results.check(s, "shutdown", ok(b.request("shutdown", timeout=40)))
        code = b.finish()
        results.check(s, "exit status 0", code == 0, repr(code))
        check_stdout(results, s, b)
        check_no_reported(results, s, b)
    finally:
        b.kill()
        host.close()


def _offscreen_guard():
    """This checkout's ``offscreen_guard.py``, loaded by path (this script
    may not import the package it tests). The sessions open real mpv
    windows, so the smoke test runs on a private desktop: hidden_run.py."""
    import importlib.util

    path = os.path.join(ROOT, "jellyfin_mpv_shim", "offscreen_guard.py")
    spec = importlib.util.spec_from_file_location("_offscreen_guard", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    global THIS_CHECKOUT
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    default_python = os.path.join(ROOT, ".venv", "Scripts" if os.name == "nt"
                                  else "bin", "python")
    if os.name == "nt":
        default_python += ".exe"
    parser.add_argument("--python", default=default_python
                        if os.path.exists(default_python) else sys.executable,
                        help="interpreter that has the shim installed")
    parser.add_argument("--out", help="folder for logs and the screenshot "
                        "(default: a new temporary folder)")
    parser.add_argument("--only", choices=("basic", "eof", "standalone",
                                           "embedded", "prewarm"),
                        action="append", help="run only these sessions")
    parser.add_argument("--this-checkout", action="store_true",
                        help="run the bridge from the checkout this script "
                        "is in (python -E -s, cwd = that backend folder) "
                        "rather than with -I, which imports whatever the "
                        "interpreter has installed -- for an editable "
                        "install, the checkout it was made from")
    options = parser.parse_args()
    _offscreen_guard().check("tools\\bridge_smoke.py")
    THIS_CHECKOUT = options.this_checkout
    out_dir = options.out or tempfile.mkdtemp(prefix="bridge-smoke-")
    os.makedirs(out_dir, exist_ok=True)
    print("python: %s\noutput: %s\nbackend: %s" % (
        options.python, out_dir,
        ROOT if THIS_CHECKOUT else "as installed (-I)"), flush=True)
    sessions = options.only or ["basic", "eof", "standalone", "embedded",
                                "prewarm"]
    results = Results()
    for name in sessions:
        if name in ("embedded", "prewarm") and os.name != "nt":
            print("\n== %s: skipped (Windows only)" % name)
            continue
        globals()["session_" + name](results, options.python, out_dir)
    print("\n%d checks, %d failed" % (len(results.rows),
                                      len(results.failed)))
    for session, name, _ok, detail in results.failed:
        print("  FAIL %s: %s %s" % (session, name, detail))
    return 1 if results.failed else 0


if __name__ == "__main__":
    sys.exit(main())
