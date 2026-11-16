"""Embedded-window mode: mpv draws into a window another process owns.

Only the bridge (`jellyfin_mpv_shim.bridge`, docs/bridge.md) turns this on.
There, FluentFin owns a child window over its player page and hands its
handle over in ``connect``; mpv renders into it with ``--wid``. From then on
the window is FluentFin's in every respect the shim used to decide for
itself:

* **mpv never resizes, moves, retitles, fullscreens or raises it**, and the
  shim never saves a geometry for it. The construction options say so
  (`apply_to_options`), and every runtime path in ``player_window`` that
  would write geometry, maximize, minimize, drag or raise returns early.
* **Fullscreen becomes a request.** The shim's fullscreen key, mpv's own
  double-click binding, the OSC button and ESC all end up as
  ``on_fullscreen_request(bool)``; the UI decides and answers with
  ``set_fullscreen``, which lands in `ui_fullscreen`. mpv's own
  ``fullscreen`` property is kept equal to `ui_fullscreen` so the OSC draws
  the right button -- measured on mpv 0.41 / Windows: with ``--wid`` the
  property is stored and the parent window does not move or resize.

State is module-level because the player is a module-level singleton that
is built when ``player`` is first imported, and the window has to be known
before that: ``configure`` is called first, then the import.

Imports nothing from the player or a backend, like ``mpv_options``, so the
option mapping stays testable without opening a window.
"""

import logging
import threading

log = logging.getLogger("window")

#: The window mpv renders into, or None for an ordinary top-level window.
hwnd = None

#: fluentfin-mpv player controls: mpv runs in the UI's own process and the
#: shim only attached to it (BRIDGE.md section 4, ``attached``). There is no
#: window of mpv's at all; the UI's is in every respect what an embedded
#: one is here (full screen is a request, nothing is sized or raised), and
#: no construction option reaches that mpv (`apply_to_options`).
attached = False

#: Whether the UI says its window is fullscreen right now. Only meaningful
#: while embedded; the UI reports it with the bridge's ``set_fullscreen``.
ui_fullscreen = False

#: Called with the fullscreen state the user asked for (a bool). Set by the
#: bridge; None means nobody is listening and the request is dropped.
on_fullscreen_request = None

#: True while mpv runs without ``force-window``: a pre-warmed ``connect``
#: (BRIDGE.md section 4) until its first play, which claims the surface
#: (`claim_surface`). Read by `apply_to_options`, so an mpv the
#: player has to re-create builds the surface iff it was claimed.
surface_deferred = False

#: Held across "read and clear `surface_deferred`": exactly one caller gets
#: True and writes ``force-window``. A leaf lock.
_surface_lock = threading.Lock()

#: Held across "read `ui_fullscreen`, write mpv's ``fullscreen``": by the
#: put-back (action thread) and by the owner's answer (the bridge's control
#: worker). Without it the put-back could read the old state, the answer
#: land, and the put-back then write the old state over it -- mpv's
#: property disagreeing with the window, which the observer reads as a new
#: request. A leaf lock: nothing is waited for while it is held but the
#: one property write.
fullscreen_lock = threading.Lock()

#: Construction options that would size, place, title or decorate a
#: top-level window. Dropped when embedded: the window is not ours.
#: ``auto_window_resize`` stays (off), since off is already "never resize".
_TOPLEVEL_OPTIONS = ("geometry", "window_maximized", "border", "title",
                     "x11_name", "wayland_app_id")


def configure(window_handle, attach=False, defer_surface=False):
    """Embed into ``window_handle`` (an int), or ``None`` for a top-level
    window; ``attach``: the UI's own mpv (no window, no handle). Call before
    the player is first imported.

    ``defer_surface`` (embedded only; ignored for a top-level window and
    when attached, where the surface is the UI's): mpv starts without its
    video output -- no window of its own inside ``window_handle`` -- until
    `claim_surface` (BRIDGE.md section 4, ``connect.prewarm``)."""
    global hwnd, ui_fullscreen, attached, surface_deferred
    hwnd = None if window_handle is None else int(window_handle)
    attached = bool(attach) and hwnd is None
    ui_fullscreen = False
    with _surface_lock:
        surface_deferred = bool(defer_surface) and hwnd is not None


def active():
    """True while mpv renders into somebody else's window (embedded, or the
    UI's own mpv the shim attached to)."""
    return hwnd is not None or attached


def claim_surface():
    """The first play of a pre-warmed backend: the video output exists
    from now on, for the rest of the process. True exactly once, for
    the caller that must now turn mpv's ``force-window`` on (a deferred
    surface); False when there was nothing deferred."""
    global surface_deferred
    with _surface_lock:
        claimed, surface_deferred = surface_deferred, False
    return claimed


def request_fullscreen(want):
    """Ask the window's owner for fullscreen on or off. Never raises: this
    runs from mpv's event thread and from key handlers."""
    handler = on_fullscreen_request
    if handler is None:
        log.debug("fullscreen request (%s) with nobody listening", want)
        return
    try:
        handler(bool(want))
    except Exception:
        log.debug("fullscreen request handler failed", exc_info=True)


def apply_to_options(mpv_options):
    """Rewrite a `build_mpv_options` dict for the embedded window, in place.

    * ``wid`` is what embeds, as a decimal string. The bridge only lets
      ``0 < hwnd <= 0xFFFFFFFF`` through: a negative or wider value makes
      mpv open its own top-level window instead.
    * ``force_window`` + ``idle``: the video surface exists from the moment
      mpv does, before the first file, so the owner's panel never shows a
      gap where mpv will be. Not while the surface is deferred
      (`surface_deferred`, a pre-warmed connect): then mpv idles with no
      video output, and its first play turns ``force-window`` on.
    * ``fullscreen`` starts at the UI's state so the mirror holds from the
      first frame.
    * ``window_dragging`` off: a drag on the video would otherwise ask mpv
      to move a window it does not own (mpv 0.33+).
    * ``background_color`` black, so the idle surface matches letterboxing
      whatever an mpv.conf says. The colour option, not ``background``,
      which an mpv before 0.38 refuses to start with.
    """
    if not active() or attached:
        # Attached: the shim does not start that mpv, so nothing here would
        # reach it (python-mpv-jsonipc drops construction options when it
        # only connects); and there is no window to give it.
        return mpv_options
    for key in _TOPLEVEL_OPTIONS:
        mpv_options.pop(key, None)
    mpv_options["wid"] = str(hwnd & 0xFFFFFFFF)
    mpv_options["force_window"] = not surface_deferred
    mpv_options["idle"] = True
    mpv_options["fullscreen"] = bool(ui_fullscreen)
    mpv_options["window_dragging"] = False
    mpv_options["background_color"] = "#000000"
    return mpv_options
