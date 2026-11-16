"""FluentFin MPV's bar in bridge mode: which one, its files, its options,
how it is loaded and what its messages do (`jellyfin_mpv_shim.fluentfin_bar`,
`fluentfin_osc`). Pure: no mpv, no player import. The scripts themselves
against a real mpv: tests/test_fluentfin_bar_lua.py.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import ast
import os
import pathlib
import re
import sys
import tempfile
import threading
import unittest

sys.argv = [sys.argv[0]]      # importing the shim reaches args.get_args()

from jellyfin_mpv_shim import fluentfin_bar, fluentfin_osc  # noqa: E402
from jellyfin_mpv_shim.fluentfin_bar import FORK, UOSC  # noqa: E402

PKG = pathlib.Path(fluentfin_bar.__file__).resolve().parent
LUA = pathlib.Path(fluentfin_bar.lua_script()).read_text(encoding="utf-8")
UOSC_MAIN = (pathlib.Path(fluentfin_bar.uosc_dir()) / "main.lua").read_text(
    encoding="utf-8")


class Bar:
    """`fluentfin_osc` in bridge mode with ``bar`` for a test."""

    def __init__(self, bar):
        self.bar = bar

    def __enter__(self):
        self._was = (fluentfin_osc.active(), fluentfin_osc.bar())
        fluentfin_osc.configure(True, bar=self.bar)

    def __exit__(self, *exc):
        active, bar = self._was
        fluentfin_osc.configure(active, bar=bar or FORK)


class ChooseTest(unittest.TestCase):

    def test_the_shims_default_style_gets_uosc(self):
        for style in ("mpvtk", "jellyfin"):
            for mode in ("embedded", "standalone"):
                with self.subTest(style=style, mode=mode):
                    self.assertEqual(fluentfin_bar.choose(style, mode), UOSC)

    def test_mpvs_look_on_purpose_is_the_fork(self):
        for style in ("mpv", "default"):
            for mode in ("embedded", "standalone", "attached"):
                with self.subTest(style=style, mode=mode):
                    self.assertEqual(fluentfin_bar.choose(style, mode), FORK)

    def test_no_controls_and_the_users_own_are_left_alone(self):
        for style in ("none", "custom", "something-newer"):
            with self.subTest(style=style):
                self.assertIsNone(fluentfin_bar.choose(style, "embedded"))

    def test_where_uosc_cannot_run_the_fork(self):
        # FluentFin's own mpv: no construction option (osd-fonts-dir).
        self.assertEqual(fluentfin_bar.choose("mpvtk", "attached"), FORK)
        # The user's mpv.conf points the OSD at fonts of theirs.
        self.assertEqual(fluentfin_bar.choose("mpvtk", "embedded",
                                              user_fonts_dir=True), FORK)
        # uosc not there, or not whole.
        with tempfile.TemporaryDirectory() as empty:
            self.assertEqual(fluentfin_bar.choose("mpvtk", "embedded",
                                                  base=empty), FORK)

    def test_every_vendored_file_is_checked(self):
        """The reviewer's case: a uosc with its main.lua and fonts but a
        lib file gone started, errored and left no bar. Each listed file is
        needed, and the list is the folder's."""
        files = fluentfin_bar.uosc_files()
        self.assertIn("scripts/uosc/lib/std.lua", files)
        with tempfile.TemporaryDirectory() as copy:
            for rel in files:
                dest = pathlib.Path(copy, *rel.split("/"))
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(b"")
            self.assertEqual(fluentfin_bar.missing(copy), [])
            self.assertEqual(fluentfin_bar.choose("mpvtk", "embedded",
                                                  base=copy), UOSC)
            for gone in ("scripts/uosc/lib/std.lua",
                         "scripts/uosc/elements/Timeline.lua",
                         "fonts/uosc_icons.otf"):
                with self.subTest(gone=gone):
                    path = pathlib.Path(copy, *gone.split("/"))
                    path.unlink()
                    self.assertEqual(fluentfin_bar.missing(copy), [gone])
                    self.assertEqual(fluentfin_bar.choose(
                        "mpvtk", "embedded", base=copy), FORK)
                    path.write_bytes(b"")
        # No list to check against: the fork.
        with tempfile.TemporaryDirectory() as nowhere:
            listless = os.path.join(nowhere, "uosc-files.txt")
            self.assertEqual(fluentfin_bar.missing(manifest=listless),
                             ["uosc-files.txt"])

    def test_the_list_is_the_vendored_folder(self):
        root = pathlib.Path(fluentfin_bar.ASSETS)
        on_disk = sorted(
            p.relative_to(root).as_posix()
            for sub in ("scripts/uosc", "fonts")
            for p in (root / sub).rglob("*") if p.is_file())
        self.assertEqual(sorted(fluentfin_bar.uosc_files()), on_disk)

    def test_the_vendored_copy_is_whole(self):
        self.assertEqual(fluentfin_bar.missing(), [])
        self.assertTrue(os.path.isfile(fluentfin_bar.lua_script()))


class UoscOptionsTest(unittest.TestCase):

    def opts(self, **kw):
        kw.setdefault("accent", "#93c9ce")
        kw.setdefault("scale", 1.0)
        kw.setdefault("bar_colour", "#101010")
        kw.setdefault("bar_alpha", 64)
        return dict(o.split("=", 1) for o in
                    fluentfin_bar.uosc_script_opts(**kw))

    def test_fluentfins_look(self):
        opts = self.opts(scale=1.5)
        self.assertEqual(opts["uosc-controls"], fluentfin_bar.CONTROLS)
        self.assertEqual(opts["uosc-color"],
                         "foreground=93C9CE,foreground_text=000000,"
                         "background=101010,background_text=FFFFFF")
        # FluentFin's scrim is 75 % opaque.
        self.assertEqual(opts["uosc-opacity"],
                         "timeline=0.75,controls=0,top_bar=0.75,volume=0.75")
        self.assertEqual((opts["uosc-scale"], opts["uosc-scale_fullscreen"]),
                         ("1.5", "1.5"))
        # The wheel over the seek bar is no seek, a volume notch is 5, no
        # cache readout, no window buttons (the window is FluentFin's).
        for key, value in (("timeline_step", "0"), ("volume_step", "5"),
                           ("timeline_style", "bar"),
                           ("buffered_time_threshold", "0"),
                           ("timeline_cache", "no"),
                           ("top_bar_controls", "no"),
                           ("languages", "en"),
                           ("disable_elements",
                            "idle_indicator,audio_indicator")):
            with self.subTest(key=key):
                self.assertEqual(opts["uosc-" + key], value)

    def test_every_option_is_one_uosc_declares(self):
        """mp.options drops an option the script does not declare, with a
        warning: the look would silently not apply."""
        table = UOSC_MAIN[UOSC_MAIN.index("defaults = {"):]
        table = table[:table.index("\n}\n")]
        for key in self.opts():
            name = key[len("uosc-"):]
            with self.subTest(name=name):
                self.assertRegex(table, r"\n\t%s =\s" % re.escape(name))

    def test_every_managed_button_is_one_fluentfin_lua_fills(self):
        wanted = re.findall(r"button:(\w+)", fluentfin_bar.CONTROLS)
        self.assertEqual(wanted, ["fluentfin_prev", "fluentfin_next",
                                  "fluentfin_skip"])
        for name in wanted:
            with self.subTest(name=name):
                self.assertIn('button("%s"' % name, LUA)

    def test_every_command_button_has_an_icon_and_a_command(self):
        """uosc's `command:<icon>:<command>` takes exactly two parameters
        (Controls:init_options); a third colon breaks the button."""
        for item in fluentfin_bar.CONTROLS.split(","):
            if item.startswith("command:"):
                config = item.split("?", 1)[0]
                with self.subTest(item=item):
                    self.assertEqual(len(config.split(":")), 3)


class NoSelfUpdateTest(unittest.TestCase):
    """uosc's self-updater ran PowerShell to fetch and run an installer
    (``irm .../installers/windows.ps1 | iex``). FluentFin MPV pins uosc and
    re-vendors it by hand: no updater, no way to reach one."""

    def test_uosc_cannot_update_itself(self):
        uosc = pathlib.Path(fluentfin_bar.uosc_dir())
        self.assertFalse((uosc / "elements" / "Updater.lua").exists())
        for path in uosc.rglob("*.lua"):
            text = path.read_text(encoding="utf-8")
            with self.subTest(path=path.name):
                self.assertNotIn("require('elements/Updater')", text)
                self.assertNotIn("installers/", text)
                self.assertNotIn("'script-binding uosc/update'", text)
        main = (uosc / "main.lua").read_text(encoding="utf-8")
        update = main[main.index("bind_command('update'"):]
        update = update[:update.index("end)")]
        self.assertNotIn("require", update)


class LicencesTest(unittest.TestCase):
    """What ships with the bar carries its licences: uosc's LGPL-2.1 and
    Material Symbols' Apache-2.0 (uosc's icon font); the release archive
    has no licence for either font."""

    def test_the_notices_and_texts_ship_with_it(self):
        here = pathlib.Path(fluentfin_bar.ASSETS).parent
        notice = (here / "THIRD-PARTY.md").read_text(encoding="utf-8")
        for needed in ("LGPL-2.1", "Apache-2.0", "uosc_icons.otf",
                       "uosc_textures.ttf", "Material Symbols",
                       "LICENSE.Apache-2.0", "LICENSE.LGPL"):
            with self.subTest(needed=needed):
                self.assertIn(needed, notice)
        apache = (here / "LICENSE.Apache-2.0").read_text(encoding="utf-8")
        self.assertIn("Apache License", apache)
        self.assertIn("Version 2.0, January 2004", apache)
        self.assertIn("TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND "
                      "DISTRIBUTION", apache)
        lgpl = pathlib.Path(fluentfin_bar.uosc_dir(), "LICENSE.LGPL")
        self.assertIn("GNU LESSER GENERAL PUBLIC LICENSE",
                      lgpl.read_text(encoding="utf-8"))


class ScriptOptsTest(unittest.TestCase):

    def opts(self, conf, bar):
        with Bar(bar):
            return fluentfin_osc.script_opts(confdir=conf, accent="#93C9CE",
                                             scale=1.0)

    def prefixes(self, opts):
        return sorted({o.split("-", 1)[0] for o in opts})

    def test_uosc_gets_its_own_the_forks_and_fluentfin_luas(self):
        """The fork's too: a uosc that will not load leaves the fork,
        which then still has FluentFin's look."""
        with tempfile.TemporaryDirectory() as conf:
            opts = self.opts(conf, UOSC)
        self.assertEqual(self.prefixes(opts), ["fluentfin", "osc", "uosc"])
        self.assertIn("fluentfin-step=5", opts)

    def test_the_fork_gets_no_uoscs(self):
        with tempfile.TemporaryDirectory() as conf:
            self.assertEqual(self.prefixes(self.opts(conf, FORK)),
                             ["fluentfin", "osc"])

    def test_a_uosc_conf_of_the_users_wins(self):
        with tempfile.TemporaryDirectory() as conf:
            os.makedirs(os.path.join(conf, "script-opts"))
            pathlib.Path(conf, "script-opts", "uosc.conf").write_text(
                "", encoding="utf-8")
            self.assertEqual(self.prefixes(self.opts(conf, UOSC)),
                             ["fluentfin", "osc"])


class ConstructionOptionsTest(unittest.TestCase):

    def options(self, bar, mpv_conf=""):
        with tempfile.TemporaryDirectory() as conf, Bar(bar):
            pathlib.Path(conf, "mpv.conf").write_text(mpv_conf,
                                                      encoding="utf-8")
            opts = fluentfin_osc.apply_to_options({}, confdir=conf)
        opts.pop("osd_font", None)
        return opts

    def test_uosc_gets_its_fonts_and_a_0_to_100_volume(self):
        self.assertEqual(self.options(UOSC), {
            "osd_fonts_dir": fluentfin_bar.fonts_dir(), "volume_max": 100})

    def test_not_over_the_users_volume_max(self):
        self.assertEqual(self.options(UOSC, "volume-max=150\n"),
                         {"osd_fonts_dir": fluentfin_bar.fonts_dir()})

    def test_nothing_for_the_fork_or_outside_bridge_mode(self):
        self.assertEqual(self.options(FORK), {})
        fluentfin_osc.configure(False)
        self.assertEqual(fluentfin_osc.apply_to_options({}), {})


class FakePlayer:
    """mpv as ``load-script`` meets it: the command answers success as soon
    as the script is queued, whether or not its start then errors. A script
    that started and errored is gone, and a message to it is refused, as mpv
    refuses one to a script it does not have (``uosc_dies``). ``gone``: mpv
    itself is (every command raises, as on a closed pipe)."""

    def __init__(self, uosc_dies=False, gone=False):
        self.commands = []
        self.uosc_dies = uosc_dies
        self.gone = gone
        self.scripts = set()

    def command(self, *args):
        if self.gone:
            raise BrokenPipeError("mpv went away")
        self.commands.append(args)
        if args[0] == "load-script":
            name = os.path.splitext(os.path.basename(args[1]))[0]
            if not (name == "uosc" and self.uosc_dies):
                self.scripts.add(name)
        elif args[0] == "script-message-to" and args[1] not in self.scripts:
            raise RuntimeError("Can't find script '%s'" % args[1])


class LoadTest(unittest.TestCase):

    def setUp(self):
        was = fluentfin_bar.START_CHECK
        fluentfin_bar.START_CHECK = 0.05
        self.addCleanup(setattr, fluentfin_bar, "START_CHECK", was)

    def settle(self):
        for thread in threading.enumerate():
            if thread.name == "fluentfin-uosc-start":
                thread.join(5)

    def test_uosc_then_fluentfin_lua(self):
        player = FakePlayer()
        with Bar(UOSC):
            self.assertTrue(fluentfin_osc.load_bar(player))
        self.settle()
        self.assertEqual(player.commands[:2], [
            ("load-script", fluentfin_bar.uosc_dir()),
            ("load-script", fluentfin_bar.lua_script())])
        self.assertNotIn(fluentfin_osc.OSC_SCRIPT,
                         [os.path.basename(c[1]) for c in player.commands],
                         "uosc runs: no fork on top of it")

    def test_the_fork_is_the_callers_and_fluentfin_lua_goes_with_it(self):
        player = FakePlayer()
        with Bar(FORK):
            self.assertFalse(fluentfin_osc.load_bar(player))
        self.settle()
        self.assertEqual(player.commands,
                         [("load-script", fluentfin_bar.lua_script())])

    def test_a_uosc_whose_start_errors_leaves_the_fork(self):
        """The reviewer's case: a broken file of uosc's. mpv answered
        ``load-script`` with success, uosc then died (having turned mpv's
        own OSC off), and the bridge loaded no other bar: none at all."""
        player = FakePlayer(uosc_dies=True)
        with Bar(UOSC):
            self.assertTrue(fluentfin_osc.load_bar(player),
                            "load-script answered success")
        self.settle()
        loaded = [os.path.basename(c[1]) for c in player.commands
                  if c[0] == "load-script"]
        self.assertEqual(loaded, ["uosc", "fluentfin.lua",
                                  fluentfin_osc.OSC_SCRIPT])

    def test_an_mpv_that_is_gone_loads_nothing(self):
        player = FakePlayer(gone=True)
        with Bar(UOSC):
            self.assertFalse(fluentfin_osc.load_bar(player))
        self.settle()
        self.assertEqual(player.commands, [])

    def test_nothing_outside_bridge_mode(self):
        player = FakePlayer()
        fluentfin_osc.configure(False)
        self.assertFalse(fluentfin_osc.load_bar(player))
        self.assertEqual(player.commands, [])

    def test_the_player_asks_before_loading_the_fork(self):
        src = (PKG / "player.py").read_text(encoding="utf-8")
        body = src[src.index("    def _load_classic_osc("):]
        body = body[:body.index("\n    def ")]
        self.assertLess(body.index("fluentfin_osc.load_bar(self._player)"),
                        body.index('get_resource("trickplay-osc.lua")'))


class MessagesTest(unittest.TestCase):

    class Pm:
        def __init__(self):
            self.tasks = []

        def put_task(self, fn, *args):
            self.tasks.append(fn.__name__)

        def skip_intro(self):
            pass

        def play_next(self):
            pass

        def play_prev(self):
            pass

    def setUp(self):
        self.addCleanup(setattr, fluentfin_osc, "skip_handler",
                        fluentfin_osc.skip_handler)
        fluentfin_osc.skip_handler = None

    def test_each_message_queues_the_shims_own_action(self):
        pm = self.Pm()
        with Bar(UOSC):
            for name in ("fluentfin-skip", "fluentfin-next",
                         "fluentfin-prev"):
                self.assertTrue(fluentfin_osc.on_message(pm, [name]))
            self.assertFalse(fluentfin_osc.on_message(pm, ["fluentfin-x"]))
            self.assertFalse(fluentfin_osc.on_message(pm, []))
        self.assertEqual(pm.tasks, ["skip_intro", "play_next", "play_prev"])
        fluentfin_osc.configure(False)
        self.assertFalse(fluentfin_osc.on_message(pm, ["fluentfin-skip"]))

    def test_the_names_are_fluentfin_luas_and_the_players(self):
        sent = set(re.findall(r'"(fluentfin-(?:skip|next|prev))"', LUA))
        self.assertEqual(sent, set(fluentfin_osc.MESSAGES))
        tree = ast.parse((PKG / "player.py").read_text(encoding="utf-8"))
        cls = next(n for n in tree.body if isinstance(n, ast.ClassDef)
                   and n.name == "PlayerManager")
        methods = {n.name for n in cls.body if isinstance(n, ast.FunctionDef)}
        for action in fluentfin_osc.MESSAGES.values():
            with self.subTest(action=action):
                self.assertIn(action, methods)

    def test_the_player_hands_them_over(self):
        src = (PKG / "player.py").read_text(encoding="utf-8")
        body = src[src.index("    def _on_client_message("):]
        body = body[:body.index("\n    def ")]
        self.assertIn('args[0].startswith("fluentfin-")', body)
        self.assertIn("fluentfin_osc.on_message(self, args)", body)


class BridgeChoosesTest(unittest.TestCase):
    """Read from the source (start_player imports the player)."""

    def test_the_bar_is_chosen_before_the_player_is_built(self):
        src = (PKG / "bridge.py").read_text(encoding="utf-8")
        body = src[src.index("    def start_player("):]
        body = body[:body.index("\n    def ")]
        self.assertLess(body.index("fluentfin_bar.choose(settings.osc_style"),
                        body.index("fluentfin_osc.configure(True, bar=bar)"))
        self.assertLess(body.index("fluentfin_osc.configure(True, bar=bar)"),
                        body.index("from . import player"))
        self.assertLess(body.index('_override("kb_kill_shader", "K")'),
                        body.index("from . import player"))


if __name__ == "__main__":
    unittest.main()
