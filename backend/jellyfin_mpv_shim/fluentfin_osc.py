"""FluentFin MPV: the title and the on-screen controller inside FluentFin.

FluentFin MPV's own module (not upstream). Two things live here:

* :func:`media_title`, the title the shim hands mpv (``force-media-title``)
  and so the one the OSC shows: ``"Series — S6E4 · Episode"`` for an
  episode, the film's name (and year) for a film. Used in every mode.
* The bar's look in **bridge mode** (``configure(True, bar)``, called by
  ``bridge.ShimRuntime.start_player`` before the player is imported, like
  ``window_embed``). Which bar is `fluentfin_bar.choose`'s: uosc with
  ``fluentfin.lua`` by default, or the shim's own OSC fork
  (``trickplay-osc.lua``), which draws trickplay previews on any mpv -- a
  0.41.0-dev build reports 0.41 and has no OSC Preview API, so the stock
  OSC drew none -- and carries the FluentFin patches (marked
  ``FluentFin MPV`` there), each behind an option whose default is
  upstream's look. :func:`script_opts` turns them on: no cache readout,
  track names instead of "1/2", subtitles moved above the bar while it is
  shown, FluentFin's player colours (its ``PlayerControlsScrimBrush``, the
  Windows accent on the seek bar), sizes that follow Windows text scaling;
  and for uosc, the same look in uosc's options.

Imports nothing from the player or a backend (like ``mpv_options`` and
``window_embed``), so all of it is testable without opening a window.
"""

import logging
import os
import re
import sys

from . import fluentfin_bar

log = logging.getLogger("fluentfin_osc")

#: Whether the bridge runs this process. Module state because the player is
#: a module-level singleton built at import, after `configure`.
_active = False

#: The bar the bridge loads (`fluentfin_bar.UOSC`, `fluentfin_bar.FORK`),
#: None for osc_style "none"/"custom".
_bar = None

#: The OSC the bridge loads (a resource of this package).
OSC_SCRIPT = "trickplay-osc.lua"

#: FluentFin's font. "Segoe UI Variable" is a variable font whose families
#: are the optical sizes; "Text" is the one WinUI sets body text in. libass
#: falls back to its default font where it is missing (Windows 10).
FONT = "Segoe UI Variable Text"

#: FluentFin's `PlayerControlsScrimBrush` (Styles/Button.xaml): #101010 at
#: 75 % opacity. ASS alpha is transparency, 0 opaque .. 255 clear.
BAR_COLOUR = "#101010"
BAR_ALPHA = 64

#: WinUI's dark-theme accent fill for the default Windows blue, used when
#: the user's own accent cannot be read.
DEFAULT_ACCENT = "#60CDFF"

#: The seek bar's empty track: white at ~35 % (ASS alpha 165), quieter than
#: upstream's and close to WinUI's slider rail on a dark scrim.
TRACK_ALPHA = 165

#: The mouse wheel over mpv's window: volume +-5 a notch, 0..100, and never
#: a seek (mpv's defaults are +-2, and a seek on the horizontal wheel).
#: FluentFin's own controls step the same 5, so a wheel feels the same
#: whichever window it lands on.
WHEEL_STEP = 5

#: Whether mpv shows its own volume bar for a wheel change. Off once
#: FluentFin draws a volume indicator of its own from the ``state`` events,
#: or the user would see two.
WHEEL_OSD = True


def configure(enabled, bar=fluentfin_bar.FORK):
    """Turn the bridge's look on or off, with the bar `fluentfin_bar.choose`
    picked. Call before the player is first imported."""
    global _active, _bar
    _active = bool(enabled)
    _bar = bar if _active else None


def bar():
    """The bar of this run, None outside bridge mode."""
    return _bar


def active():
    """True in bridge mode."""
    return _active


# --------------------------------------------------------------- the title

def _same_words(a, b):
    """Equal once case, spacing and punctuation are ignored ("It's Always
    Sunny" vs "Its always sunny")."""
    def norm(s):
        return re.sub(r"\W+", "", (s or "").casefold())
    return bool(norm(a)) and norm(a) == norm(b)


def _number(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def episode_number(item):
    """``"S6E4"`` (``"S1E1-2"`` for a double episode), or ``""`` when the
    server has no season and episode number. Season 0 (specials) is a real
    season and stays ``S0``."""
    season = _number(item.get("ParentIndexNumber"))
    episode = _number(item.get("IndexNumber"))
    if season is None or episode is None:
        return ""
    se = "S%dE%d" % (season, episode)
    end = _number(item.get("IndexNumberEnd"))
    if end is not None and end > episode:
        se += "-%d" % end
    return se


def media_title(item, is_tv):
    """The title mpv shows for ``item`` (a Jellyfin item dict).

    An episode is ``"Series — S6E4 · Episode"``. Its own ``Name`` is left
    out when it only repeats the series -- what a library that prefers
    embedded titles stores for a file whose title tag is the show, and how
    "It's Always Sunny in Philadelphia - s6e04 - It's Always Sunny in
    Philadelphia" came about -- or when it is empty. A film keeps
    upstream's ``"Name (Year)"``; anything else is its name.
    """
    item = item or {}
    name = (item.get("Name") or "").strip()
    if is_tv:
        series = (item.get("SeriesName") or "").strip()
        if _same_words(name, series):
            name = ""
        detail = " · ".join(p for p in (episode_number(item), name) if p)
        title = " — ".join(p for p in (series, detail) if p)
        return title or (item.get("Name") or "")
    if item.get("Type") == "Movie":
        year = item.get("ProductionYear")
        if year is not None and name:
            return "%s (%s)" % (name, year)
    return name


def sidecar_labels(video, sub_id):
    """``(title, language)`` for mpv's ``sub-add`` of a Jellyfin subtitle
    sidecar: the stream's own ``Title`` and ``Language``, ``""`` where the
    server has none.

    mpv knows nothing about a sidecar but its url, and titles it with the
    url's last part ("Stream.srt"), so its track list -- and the OSC's
    track button, which reads it -- could not say which language it is.
    """
    source = getattr(video, "media_source", None) or {}
    for stream in source.get("MediaStreams") or []:
        if stream.get("Type") == "Subtitle" and stream.get("Index") == sub_id:
            return ((stream.get("Title") or "").strip(),
                    (stream.get("Language") or "").strip())
    return "", ""


# ----------------------------------------------------------- Windows state

def _reg_value(path, name):
    """A value under HKEY_CURRENT_USER, or None (not Windows, no key)."""
    if not sys.platform.startswith("win32"):
        return None
    try:
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
            return winreg.QueryValueEx(key, name)[0]
    except Exception:
        return None


def accent_colour(palette=None):
    """FluentFin's accent as ``#RRGGBB``: what WinUI fills a slider with in
    the dark theme, ``SystemAccentColorLight2``.

    Windows stores the palette it derived from the user's accent as
    ``AccentPalette``, eight RGBA entries from Light3 to Dark3, so Light2 is
    the second; reading it gives the exact colour FluentFin's own controls
    use, which no formula of ours would. ``palette`` is for tests.
    """
    if palette is None:
        palette = _reg_value(
            r"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
            "AccentPalette")
    # A REG_BINARY comes back as bytes; anything else (missing, a number:
    # bytes(12) would be twelve zeros, i.e. black) is not a palette.
    if isinstance(palette, (bytes, bytearray)) and len(palette) >= 8:
        r, g, b = palette[4], palette[5], palette[6]
        return "#%02X%02X%02X" % (r, g, b)
    return DEFAULT_ACCENT


def text_scale(value=None):
    """Windows' "Text size" (Settings > Accessibility), as a factor: 1.0 to
    2.25. FluentFin's text grows with it; the OSC's does too. ``value`` is
    the registry's percentage, for tests."""
    if value is None:
        value = _reg_value(r"Software\Microsoft\Accessibility",
                           "TextScaleFactor")
    pct = _number(value)
    if pct is None:
        return 1.0
    return max(100, min(225, pct)) / 100.0


# ------------------------------------------------------- the user's config

def _confdir():
    from . import conffile
    from .constants import APP_NAME

    return conffile.confdir(APP_NAME)


def user_osc_conf(confdir=None):
    """Whether the config folder has an ``osc.conf`` of the user's.

    ``script-opts`` outrank ``osc.conf``, so sending ours would override
    every line of theirs. Somebody who wrote one has taken the OSC's look
    over, and gets none of ours -- the same rule as never writing over
    their ``mpv.conf``.
    """
    try:
        base = confdir if confdir is not None else _confdir()
        return os.path.isfile(os.path.join(base, "script-opts", "osc.conf"))
    except Exception:
        log.debug("could not look for osc.conf", exc_info=True)
        return False


def user_uosc_conf(confdir=None):
    """Whether the config folder has a ``script-opts/uosc.conf`` of the
    user's: then uosc's look is theirs (`user_osc_conf`'s rule)."""
    try:
        base = confdir if confdir is not None else _confdir()
        return os.path.isfile(os.path.join(base, "script-opts", "uosc.conf"))
    except Exception:
        log.debug("could not look for uosc.conf", exc_info=True)
        return False


def user_sets(option, confdir=None):
    """Whether the user's ``mpv.conf`` sets ``option`` at its top level
    (profiles are conditional, see `mpv_options.hwdec_pinned_by_config`)."""
    try:
        base = confdir if confdir is not None else _confdir()
        with open(os.path.join(base, "mpv.conf"), encoding="utf-8",
                  errors="replace") as fh:
            for line in fh:
                line = line.split("#", 1)[0].strip()
                if line.startswith("["):
                    break
                key = line.partition("=")[0].strip().lstrip("-")
                if key == option:
                    return True
    except OSError:
        pass
    except Exception:
        log.debug("could not read mpv.conf", exc_info=True)
    return False


# ------------------------------------------------------------- the options

def script_opts(confdir=None, accent=None, scale=None):
    """The ``script-opts`` entries for the bridge's bar, in the order they
    are appended; empty outside bridge mode. The fork's (``osc-*``) unless
    the user has an ``osc.conf`` of their own -- also with uosc, so a uosc
    that will not load falls back to the fork in FluentFin's look --, then
    uosc's (``uosc-*``, unless a ``uosc.conf`` of theirs) and
    ``fluentfin.lua``'s. ``accent`` and ``scale`` are for tests."""
    if not _active:
        return []
    accent = accent or accent_colour()
    scale = scale if scale is not None else text_scale()
    opts = [] if user_osc_conf(confdir) else _fork_opts(accent, scale)
    if _bar == fluentfin_bar.UOSC and not user_uosc_conf(confdir):
        opts.extend(fluentfin_bar.uosc_script_opts(accent, scale, BAR_COLOUR,
                                                   BAR_ALPHA))
    if _bar is not None:
        opts.extend(fluentfin_bar.lua_script_opts())
    return opts


def _fork_opts(accent, scale):
    scale = ("%.2f" % scale).rstrip("0").rstrip(".")
    return [
        # The subtitle raise is measured against the bottom bar.
        "osc-layout=bottombar",
        # The patches (trickplay-osc.lua, "FluentFin MPV").
        "osc-showcache=no",
        # mpv's playlist only ever holds the item playing; the queue is the
        # shim's, so those two buttons could never do anything.
        "osc-showplaylist=no",
        "osc-tracklabels=name",
        "osc-raisesubs=yes",
        "osc-background_color=" + BAR_COLOUR,
        "osc-boxalpha=%d" % BAR_ALPHA,
        # A WinUI slider: a thin quiet rail, the accent up to a round
        # handle, no buffered ranges drawn over it.
        "osc-seekbarstyle=knob",
        "osc-seekbarhandlesize=0.6",
        "osc-seekrangestyle=none",
        "osc-seekbarrail=4",
        "osc-seekbarfill=4",
        "osc-seekbarfg_color=" + accent,
        "osc-seekbarbg_color=#FFFFFF",
        "osc-seekbarbg_alpha=%d" % TRACK_ALPHA,
        "osc-timecodesize=20",
        "osc-tcspace=80",
        "osc-unicodeminus=yes",
        "osc-wheelvolume=%d" % WHEEL_STEP,
        "osc-wheelvolume_osd=%s" % ("yes" if WHEEL_OSD else "no"),
        "osc-scalewindowed=" + scale,
        "osc-scalefullscreen=" + scale,
    ]


def apply_to_options(mpv_options, confdir=None):
    """Construction options for the bridge's look, in place: the OSD font
    (the OSC's text, and mpv's own messages, in FluentFin's font) unless
    the user's ``mpv.conf`` sets one (Windows only), and uosc's fonts and
    volume range (`fluentfin_bar.construction_options`). A no-op outside
    bridge mode."""
    if (_active and sys.platform.startswith("win32")
            and not user_sets("osd-font", confdir)):
        mpv_options["osd_font"] = FONT
    if _active:
        fluentfin_bar.construction_options(
            mpv_options, _bar, lambda option: user_sets(option, confdir))
    return mpv_options


def load_bar(player):
    """`PlayerManager._load_classic_osc` in bridge mode: load this run's
    bar into ``player``. True when it did (uosc); False when the caller
    loads the fork, as it would without the bridge."""
    if not _active or _bar is None:
        return False
    return fluentfin_bar.load(player, _bar)


#: ``fluentfin.lua``'s messages (``script-message <name>``) -> the shim's
#: own action, the same ones its keys and its HUD's buttons run.
MESSAGES = {
    "fluentfin-skip": "skip_intro",
    "fluentfin-next": "play_next",
    "fluentfin-prev": "play_prev",
}


def on_message(pm, args):
    """A ``fluentfin-*`` client message (``PlayerManager._on_client_message``,
    on mpv's event thread): queued on the action thread, as the shim's own
    keys do, never run here. Unknown names are ignored."""
    action = MESSAGES.get(args[0]) if args else None
    if action is None or not _active:
        return False
    pm.put_task(getattr(pm, action))
    return True
