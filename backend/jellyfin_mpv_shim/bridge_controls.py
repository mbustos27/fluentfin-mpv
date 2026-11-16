"""Who draws the player controls over the embedded video: ``set_controls``
(BRIDGE.md section 4) -- fluentfin-mpv, bridge mode only.

``"mpv"`` is the shim's OSC, as without the bridge. ``"ui"`` is FluentFin
drawing its own controls over the video: mpv's OSC is hidden
(`PlayerManager.enable_osc`, the shim's own lever for "no controls"), and
while the UI's bottom bar is up the subtitles move up out from under it
(``sub-pos``).

**The shim writes ``sub-pos`` itself, on every item start**
(`PlayerManager.update_subtitle_visuals`, from its ``subtitle_position``
setting: bottom 100, middle 80, top 0). So that setting is the base a lift
is made from, and every item start puts the lift back *after* that write:
the bridge calls `UiControls.apply` with ``item_started`` from its
``send_timeline_initial`` hook, which `_play_media` reaches after
``update_subtitle_visuals`` (`Bridge._install_hooks`). Applied at the load
start instead, the shim's write landed second and the subtitles sat under
the bar for the whole item.

Both outlive a queue item on purpose (docs/mpv-backends.md section 6: mpv is
not re-created between items), and a restarted mpv (a new player handle)
brings its OSC back at its own default: the item start re-applies both.
Nothing is written while nothing has moved: ``"mpv"`` and an inset of 0
leave the shim's own value alone.

Pure over a duck-typed player: no mpv, no shim imports, so the sequences can
be asserted without a player (tests/test_bridge_controls.py).
"""

import threading

#: mpv's range for ``sub-pos``.
SUB_POS_MIN, SUB_POS_MAX = 0.0, 150.0


def _format(value):
    """``sub-pos`` for mpv's ``set`` command: a string (docs/mpv-backends.md
    section 1 -- a number is refused on the external backend)."""
    return ("%.2f" % value).rstrip("0").rstrip(".")


class UiControls:
    """The last ``set_controls``, and what it has done to which mpv."""

    def __init__(self):
        self._lock = threading.Lock()
        self.controls = "mpv"
        self.inset = 0.0
        #: The player handle `_written` is about.
        self._player = None
        #: The ``sub-pos`` that player has as far as we know, None while it
        #: is the shim's own and untouched by us.
        self._written = None

    def set(self, controls, inset):
        with self._lock:
            self.controls = controls
            self.inset = inset if controls == "ui" else 0.0

    @property
    def ui(self):
        with self._lock:
            return self.controls == "ui"

    def apply(self, pm, base, item_started=False):
        """Make ``pm``'s mpv match the last ``set_controls``. ``base`` is
        the shim's own ``sub-pos`` (its subtitle position setting).
        ``item_started``: the shim has just written that base itself, so
        whatever was lifted before is gone. mpv errors propagate (the
        caller knows which ones are a lost mpv)."""
        with self._lock:
            controls, inset = self.controls, self.inset
        pm.enable_osc(controls == "mpv" and pm.osc_enabled)
        self._apply_sub_pos(pm._player, float(base), inset, item_started)

    def _apply_sub_pos(self, player, base, inset, item_started):
        with self._lock:
            if player is not self._player:
                # A new mpv: nothing of ours on it.
                self._player, self._written = player, None
            if item_started and self._written is not None:
                self._written = base
            written = self._written
        if written is None and inset <= 0:
            return              # untouched, and nothing to move
        target = min(SUB_POS_MAX, max(SUB_POS_MIN, base - 100.0 * inset))
        if written is not None and abs(written - target) < 0.005:
            return
        if written is None and abs(base - target) < 0.005:
            return
        player.command("set", "sub-pos", _format(target))
        with self._lock:
            if self._player is player:
                self._written = target
