"""FluentFin MPV: the playback bar mpv draws in bridge mode.

FluentFin MPV's own module (not upstream). In bridge mode the shim builds mpv
with ``osc_style`` "mpv" (its library browser, which the "mpvtk" HUD needs,
is off), and `fluentfin_osc` decides which bar that style loads:

* **uosc** (``backend/fluentfin/mpv``, vendored, see ``VENDORED.md`` there)
  with ``fluentfin.lua`` beside it: the default, for the shim's own default
  style ("mpvtk", or its old name "jellyfin").
* **the fork** (``trickplay-osc.lua`` with FluentFin's patches,
  `fluentfin_osc.script_opts`): ``osc_style`` "mpv" in conf.json, and the
  fallback wherever uosc cannot run -- its files missing, an ``attached``
  window (FluentFin's own mpv: no construction option reaches it, so
  ``osd-fonts-dir`` cannot point at uosc's icon font), or a user ``mpv.conf``
  that sets ``osd-fonts-dir`` itself.
* nothing of ours for "none" (no controls) and "custom" (the user's own
  OSC), as without the bridge.

``fluentfin.lua`` goes with either bar: FluentFin's key map (the same keys
as FluentFin's own controls) and the Skip prompt; with uosc it also raises
the subtitles above the bar, makes the wheel the volume, fills uosc's
managed buttons (previous, next, skip) and hides uosc when the UI draws the
controls (``set_controls`` ``"ui"``).

Imports nothing from the player or a backend, like `fluentfin_osc`.
"""

import logging
import os

log = logging.getLogger("fluentfin_bar")

#: The bars.
UOSC = "uosc"
FORK = "fork"

#: FluentFin's mpv assets: ``backend/fluentfin/mpv``, beside the package.
ASSETS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(
    __file__))), "fluentfin", "mpv")

#: What uosc needs to run: its entry point and its two fonts (icons, and the
#: textures its bar backgrounds are drawn with).
UOSC_FILES = (("scripts", "uosc", "main.lua"),
              ("fonts", "uosc_icons.otf"),
              ("fonts", "uosc_textures.ttf"))

#: The volume step of a wheel notch and of Up/Down: FluentFin's own
#: controls step the same 5 (`fluentfin_osc.WHEEL_STEP`).
STEP = 5

#: The bar's buttons, left to right. ``button:fluentfin_*`` are uosc's
#: managed buttons, filled by ``fluentfin.lua``: previous and next in the
#: shim's queue (mpv's own playlist holds one item, so uosc's prev/next
#: would do nothing), and the Skip button of a segment on offer.
CONTROLS = ",".join((
    "play-pause",
    "gap",
    "button:fluentfin_prev",
    "button:fluentfin_next",
    "gap",
    "command:replay_10:seek -10?Back 10 seconds",
    "command:forward_30:seek 30?Forward 30 seconds",
    "space",
    "button:fluentfin_skip",
    "gap",
    "<has_many_audio>audio",
    "<video>subtitles",
    "<has_many_edition>editions",
    "<has_chapter>chapters",
    "gap",
    "fullscreen",
))

#: uosc elements FluentFin never shows: the idle screen's (the embedded
#: window sits idle between items) and the audio-only one.
DISABLED = "idle_indicator,audio_indicator"


def assets(base=None):
    return base if base is not None else ASSETS


def uosc_dir(base=None):
    return os.path.join(assets(base), "scripts", "uosc")


def fonts_dir(base=None):
    return os.path.join(assets(base), "fonts")


def lua_script(base=None):
    return os.path.join(assets(base), "scripts", "fluentfin.lua")


def missing(base=None):
    """The uosc files that are not there, as relative paths (empty: none)."""
    root = assets(base)
    return [os.path.join(*parts) for parts in UOSC_FILES
            if not os.path.isfile(os.path.join(root, *parts))]


def choose(osc_style, window_mode, user_fonts_dir=False, base=None):
    """The bar for a run: `UOSC`, `FORK`, or None for "none" and "custom".

    ``osc_style`` is the configured value (conf.json), not the resolved
    one: bridge mode resolves the shim's default "mpvtk" to "mpv" (no
    library browser), and only the configured value still says whether the
    user picked mpv's look on purpose."""
    if osc_style in ("mpv", "default"):
        return FORK
    if osc_style not in ("mpvtk", "jellyfin"):
        return None         # "none", "custom", or one the shim handles
    if window_mode == "attached":
        return FORK
    if user_fonts_dir:
        log.info("mpv.conf sets osd-fonts-dir: uosc's icons would be "
                 "missing, so the bar is mpv's.")
        return FORK
    gone = missing(base)
    if gone:
        log.warning("uosc is not complete (%s missing); the bar is mpv's.",
                    ", ".join(gone))
        return FORK
    return UOSC


def _number(value):
    return ("%.2f" % value).rstrip("0").rstrip(".")


def uosc_script_opts(accent, scale, bar_colour, bar_alpha):
    """uosc's options (``uosc-*`` ``script-opts``) in FluentFin's look.

    ``accent`` is ``#RRGGBB`` (the seek bar's fill), ``scale`` Windows'
    text scale, ``bar_colour``/``bar_alpha`` the scrim FluentFin's own
    controls sit on (ASS alpha: 0 opaque .. 255 clear)."""
    hex6 = accent.lstrip("#").upper()
    opacity = _number(round(1 - bar_alpha / 255.0, 2))
    return [
        "uosc-controls=" + CONTROLS,
        # The title (force-media-title: "Series - S6E4 . Episode") on top,
        # with no window buttons: the window is FluentFin's.
        "uosc-top_bar=always",
        "uosc-top_bar_controls=no",
        # Netflix-like: nothing on the picture while the bar is hidden.
        "uosc-progress=never",
        # The seek bar: the elapsed part filled with the accent (as a WinUI
        # slider; "line" draws only a cursor), no cache ranges and no
        # buffered-time readout, and the wheel over it is not a seek. uosc
        # has one foreground colour, so its icons take the accent too.
        "uosc-timeline_style=bar",
        "uosc-timeline_cache=no",
        "uosc-buffered_time_threshold=0",
        "uosc-timeline_step=0",
        "uosc-volume=right",
        "uosc-volume_step=%d" % STEP,
        "uosc-color=foreground=%s,foreground_text=000000,background=%s,"
        "background_text=FFFFFF" % (hex6, bar_colour.lstrip("#").upper()),
        "uosc-opacity=timeline=%s,controls=0,top_bar=%s,volume=%s"
        % (opacity, opacity, opacity),
        "uosc-scale=" + _number(scale),
        "uosc-scale_fullscreen=" + _number(scale),
        "uosc-autohide=yes",
        "uosc-pause_indicator=flash",
        # Its translations are not vendored.
        "uosc-languages=en",
        "uosc-disable_elements=" + DISABLED,
    ]


def lua_script_opts():
    """``fluentfin.lua``'s options (``fluentfin-*``). It needs no word on
    which bar runs: it raises the subtitles, takes the wheel and fills the
    buttons only once uosc has published ``user-data/fluentfin/bar`` (its
    one local patch), and the fork does the first two itself."""
    return ["fluentfin-step=%d" % STEP]


def construction_options(mpv_options, bar, user_sets, base=None):
    """uosc's construction options, in place: its fonts (``osd-fonts-dir``;
    `choose` gave the fork to a user ``mpv.conf`` that sets one) and
    ``volume-max=100``, so its volume slider ends where FluentFin's does,
    unless ``mpv.conf`` sets it. ``user_sets(option)`` reads mpv.conf."""
    if bar != UOSC:
        return mpv_options
    mpv_options["osd_fonts_dir"] = fonts_dir(base)
    if not user_sets("volume-max"):
        mpv_options["volume_max"] = 100
    return mpv_options


def load(player, bar, base=None):
    """Load ``bar``'s scripts into ``player`` (after its ``script-opts``
    were appended, so they read them at their start). True when this
    loaded the bar itself (uosc); False when the caller loads the fork.
    ``fluentfin.lua`` goes with either. Errors are logged, never raised:
    a uosc that will not load leaves the fork to the caller (whose
    options were appended with uosc's for that, `fluentfin_osc`)."""
    loaded = False
    if bar == UOSC:
        try:
            player.command("load-script", uosc_dir(base))
            loaded = True
        except Exception:
            log.error("Could not load uosc; the bar is mpv's.", exc_info=True)
    try:
        player.command("load-script", lua_script(base))
    except Exception:
        log.error("Could not load fluentfin.lua.", exc_info=True)
    if loaded:
        log.info("Bridge mode: the bar is uosc, with FluentFin's look.")
    return loaded
