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

#: Whether the UI says its window is fullscreen right now. Only meaningful
#: while embedded; the UI reports it with the bridge's ``set_fullscreen``.
ui_fullscreen = False

#: Called with the fullscreen state the user asked for (a bool). Set by the
#: bridge; None means nobody is listening and the request is dropped.
on_fullscreen_request = None

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


def configure(window_handle):
    """Embed into ``window_handle`` (an int), or ``None`` for a top-level
    window. Call before the player is first imported."""
    global hwnd, ui_fullscreen
    hwnd = None if window_handle is None else int(window_handle)
    ui_fullscreen = False


def active():
    """True while mpv renders into somebody else's window."""
    return hwnd is not None


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
      gap where mpv will be.
    * ``fullscreen`` starts at the UI's state so the mirror holds from the
      first frame.
    * ``window_dragging`` off: a drag on the video would otherwise ask mpv
      to move a window it does not own (mpv 0.33+).
    * ``background_color`` black, so the idle surface matches letterboxing
      whatever an mpv.conf says. The colour option, not ``background``,
      which an mpv before 0.38 refuses to start with.
    """
    if not active():
        return mpv_options
    for key in _TOPLEVEL_OPTIONS:
        mpv_options.pop(key, None)
    mpv_options["wid"] = str(hwnd & 0xFFFFFFFF)
    mpv_options["force_window"] = True
    mpv_options["idle"] = True
    mpv_options["fullscreen"] = bool(ui_fullscreen)
    mpv_options["window_dragging"] = False
    mpv_options["background_color"] = "#000000"
    return mpv_options
