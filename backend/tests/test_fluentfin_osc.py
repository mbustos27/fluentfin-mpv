"""FluentFin MPV: the title mpv shows and the bridge's OSC options.

`jellyfin_mpv_shim.fluentfin_osc` imports neither the player nor a backend,
so nothing here opens a window. The OSC's own behaviour (the subtitle raise,
the wheel) is measured against a real mpv in `test_fluentfin_osc_lua`; which
OSC the player loads, and what `sub-add` is told, in
`test_fluentfin_osc_player`.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import os
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

sys.argv = [sys.argv[0]]      # importing the package reaches args.get_args()

from jellyfin_mpv_shim import fluentfin_osc  # noqa: E402
from jellyfin_mpv_shim.fluentfin_osc import media_title  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parent.parent
PKG = ROOT / "jellyfin_mpv_shim"

SUNNY = "It's Always Sunny in Philadelphia"


def episode(**kw):
    item = {"Type": "Episode", "Name": "Mac's Big Break",
            "SeriesName": SUNNY, "ParentIndexNumber": 6, "IndexNumber": 4}
    item.update(kw)
    return item


class Active:
    """`fluentfin_osc` in bridge mode for the duration of a test."""

    def __enter__(self):
        self._was = fluentfin_osc.active()
        fluentfin_osc.configure(True)

    def __exit__(self, *exc):
        fluentfin_osc.configure(self._was)


class EpisodeTitleTest(unittest.TestCase):

    def test_series_number_and_name(self):
        self.assertEqual(media_title(episode(), True),
                         SUNNY + " — S6E4 · Mac's Big Break")

    def test_a_name_that_repeats_the_series_is_left_out(self):
        """The user's screenshot: a library preferring embedded titles
        stores the show as the episode's Name, and upstream's format then
        said the series twice."""
        title = media_title(episode(Name=SUNNY), True)
        self.assertEqual(title, SUNNY + " — S6E4")
        self.assertEqual(title.count("Sunny"), 1)

    def test_the_repeat_is_found_whatever_the_spelling(self):
        for name in ("its always sunny in philadelphia",
                     "It's Always Sunny in Philadelphia ",
                     "IT'S ALWAYS SUNNY IN PHILADELPHIA!"):
            with self.subTest(name=name):
                self.assertEqual(media_title(episode(Name=name), True),
                                 SUNNY + " — S6E4")

    def test_no_numbers(self):
        self.assertEqual(
            media_title(episode(ParentIndexNumber=None, IndexNumber=None),
                        True), SUNNY + " — Mac's Big Break")

    def test_one_number_alone_is_no_number(self):
        self.assertEqual(media_title(episode(IndexNumber=None), True),
                         SUNNY + " — Mac's Big Break")

    def test_specials_are_season_zero(self):
        self.assertEqual(media_title(episode(ParentIndexNumber=0), True),
                         SUNNY + " — S0E4 · Mac's Big Break")

    def test_a_double_episode(self):
        self.assertEqual(media_title(episode(IndexNumberEnd=5), True),
                         SUNNY + " — S6E4-5 · Mac's Big Break")

    def test_no_series_name(self):
        self.assertEqual(media_title(episode(SeriesName=None), True),
                         "S6E4 · Mac's Big Break")

    def test_nothing_but_a_name(self):
        self.assertEqual(
            media_title({"Type": "Episode", "Name": "Pilot"}, True), "Pilot")

    def test_an_empty_item(self):
        self.assertEqual(media_title({}, True), "")
        self.assertEqual(media_title(None, False), "")


class OtherTitlesTest(unittest.TestCase):

    def test_a_film_keeps_its_year(self):
        self.assertEqual(media_title({"Type": "Movie", "Name": "Heat",
                                      "ProductionYear": 1995}, False),
                         "Heat (1995)")

    def test_a_film_without_a_year(self):
        self.assertEqual(media_title({"Type": "Movie", "Name": "Heat"},
                                     False), "Heat")

    def test_anything_else_is_its_name(self):
        """A music video carries disc and track in the same fields an
        episode's numbers live in; it is not an episode."""
        self.assertEqual(media_title({"Type": "MusicVideo", "Name": "Clip",
                                      "ParentIndexNumber": 1,
                                      "IndexNumber": 3}, False), "Clip")


class VideoTitleTest(unittest.TestCase):
    """`Video.get_proper_title` is what `force-media-title` is set from."""

    def video(self, item, transcode=False):
        from jellyfin_mpv_shim.media import Video

        v = Video.__new__(Video)
        v.item = item
        v.is_tv = item.get("Type") == "Episode"
        v.is_transcode = transcode
        return v

    def test_an_episode(self):
        self.assertEqual(self.video(episode(Name=SUNNY)).get_proper_title(),
                         SUNNY + " — S6E4")

    def test_the_transcode_mark_stays(self):
        self.assertEqual(
            self.video(episode(), transcode=True).get_proper_title(),
            SUNNY + " — S6E4 · Mac's Big Break (Transcode)")


class ScriptOptsTest(unittest.TestCase):

    def opts(self, **kw):
        with tempfile.TemporaryDirectory() as conf:
            kw.setdefault("confdir", conf)
            kw.setdefault("accent", "#93C9CE")
            kw.setdefault("scale", 1.0)
            return fluentfin_osc.script_opts(**kw)

    def test_nothing_outside_bridge_mode(self):
        fluentfin_osc.configure(False)
        self.assertEqual(self.opts(), [])

    def test_what_the_bridge_asks_for(self):
        with Active():
            opts = self.opts()
        for want in ("osc-showcache=no", "osc-tracklabels=name",
                     "osc-raisesubs=yes", "osc-showplaylist=no",
                     "osc-layout=bottombar", "osc-seekbarfg_color=#93C9CE",
                     "osc-background_color=#101010", "osc-boxalpha=64",
                     "osc-wheelvolume=5", "osc-scalewindowed=1",
                     "osc-scalefullscreen=1"):
            with self.subTest(want=want):
                self.assertIn(want, opts)
        # The fork's, and fluentfin.lua's (it goes with either bar).
        for opt in opts:
            self.assertTrue(opt.startswith(("osc-", "fluentfin-")), opt)
        self.assertIn("fluentfin-step=5", opts)
        self.assertFalse([o for o in opts if o.startswith("uosc-")],
                         "uosc's only with uosc")

    def test_every_patch_option_exists_in_the_fork(self):
        """An option the OSC does not declare is dropped by mp.options with
        a warning: the look would silently not apply."""
        src = (PKG / "trickplay-osc.lua").read_text(encoding="utf-8")
        table = src[src.index("local user_opts = {"):]
        table = table[:table.index("\n}\n")]
        with Active():
            opts = [o for o in self.opts() if o.startswith("osc-")]
        self.assertTrue(opts)
        for opt in opts:
            key = opt[len("osc-"):].split("=", 1)[0]
            with self.subTest(key=key):
                self.assertRegex(table, r"\n\s+%s = " % key)

    def test_text_scaling_reaches_both_scales(self):
        with Active():
            opts = self.opts(scale=1.5)
        self.assertIn("osc-scalewindowed=1.5", opts)
        self.assertIn("osc-scalefullscreen=1.5", opts)

    def test_an_osc_conf_of_the_users_wins(self):
        """script-opts outrank osc.conf: sending ours would override every
        line of theirs."""
        with tempfile.TemporaryDirectory() as conf:
            os.makedirs(os.path.join(conf, "script-opts"))
            with open(os.path.join(conf, "script-opts", "osc.conf"), "w",
                      encoding="utf-8"):
                pass
            with Active():
                opts = fluentfin_osc.script_opts(
                    confdir=conf, accent="#000000", scale=1.0)
            self.assertEqual([o for o in opts if o.startswith("osc-")], [])


class OscScriptOptsTest(unittest.TestCase):
    """`mpv_options.osc_script_opts` is the one place the player takes
    script-opts from."""

    def test_the_mpv_style_gets_them_in_bridge_mode(self):
        from jellyfin_mpv_shim.mpv_options import osc_script_opts

        with Active():
            opts = osc_script_opts("mpv")
        self.assertEqual(opts[0], "osc-idlescreen=no")
        self.assertIn("osc-raisesubs=yes", opts)

    def test_and_nothing_changes_outside_it(self):
        from jellyfin_mpv_shim.mpv_options import osc_script_opts

        fluentfin_osc.configure(False)
        self.assertEqual(osc_script_opts("mpv"), ["osc-idlescreen=no"])

    def test_no_other_style_gets_them(self):
        from jellyfin_mpv_shim.mpv_options import osc_script_opts

        with Active():
            for style in ("mpvtk", "custom", "none"):
                with self.subTest(style=style):
                    self.assertNotIn("osc-raisesubs=yes",
                                     osc_script_opts(style))


class OsdFontTest(unittest.TestCase):

    def test_the_font_in_bridge_mode_on_windows(self):
        with tempfile.TemporaryDirectory() as conf, Active(), \
                mock.patch.object(fluentfin_osc.sys, "platform", "win32"):
            opts = fluentfin_osc.apply_to_options({}, confdir=conf)
        self.assertEqual(opts, {"osd_font": fluentfin_osc.FONT})

    def test_not_outside_bridge_mode(self):
        fluentfin_osc.configure(False)
        with mock.patch.object(fluentfin_osc.sys, "platform", "win32"):
            self.assertEqual(fluentfin_osc.apply_to_options({}), {})

    def test_not_over_the_users_mpv_conf(self):
        """For the external backend a construction option is a command-line
        argument, which mpv.conf cannot override."""
        with tempfile.TemporaryDirectory() as conf, Active(), \
                mock.patch.object(fluentfin_osc.sys, "platform", "win32"):
            with open(os.path.join(conf, "mpv.conf"), "w",
                      encoding="utf-8") as fh:
                fh.write("# mine\nosd-font=Comic Sans MS\n")
            self.assertEqual(fluentfin_osc.apply_to_options(
                {}, confdir=conf), {})

    def test_a_profile_is_not_a_top_level_setting(self):
        with tempfile.TemporaryDirectory() as conf, Active(), \
                mock.patch.object(fluentfin_osc.sys, "platform", "win32"):
            with open(os.path.join(conf, "mpv.conf"), "w",
                      encoding="utf-8") as fh:
                fh.write("[big]\nosd-font=Comic Sans MS\n")
            self.assertIn("osd_font", fluentfin_osc.apply_to_options(
                {}, confdir=conf))

    def test_build_mpv_options_applies_it(self):
        src = (PKG / "mpv_options.py").read_text(encoding="utf-8")
        body = src[src.index("def build_mpv_options("):]
        self.assertIn("fluentfin_osc.apply_to_options(mpv_options)", body)


class WindowsStateTest(unittest.TestCase):

    def test_the_accent_is_light2_of_the_palette(self):
        """AccentPalette is eight RGBA entries, Light3 first: Light2 is the
        second, which WinUI fills a slider with in the dark theme."""
        palette = bytes.fromhex(
            "B4E8EA00 93C9CE00 5199A700 447F8B00 35667200 22465200 "
            "0C222D00 88179800".replace(" ", ""))
        self.assertEqual(fluentfin_osc.accent_colour(palette), "#93C9CE")

    def test_no_palette_is_winuis_default(self):
        self.assertEqual(fluentfin_osc.accent_colour(b""),
                         fluentfin_osc.DEFAULT_ACCENT)
        self.assertEqual(fluentfin_osc.accent_colour(12),
                         fluentfin_osc.DEFAULT_ACCENT)

    def test_text_scale(self):
        for value, want in ((None, 1.0), ("x", 1.0), (100, 1.0),
                            (150, 1.5), (225, 2.25), (300, 2.25), (50, 1.0)):
            with self.subTest(value=value):
                with mock.patch.object(fluentfin_osc, "_reg_value",
                                       return_value=value):
                    self.assertEqual(fluentfin_osc.text_scale(), want)


class SidecarLabelsTest(unittest.TestCase):

    def video(self, streams):
        class V:
            media_source = {"MediaStreams": streams}
        return V()

    def test_title_and_language_of_that_stream(self):
        v = self.video([
            {"Type": "Audio", "Index": 2, "Language": "jpn"},
            {"Type": "Subtitle", "Index": 2, "Language": "eng",
             "Title": " SDH "},
            {"Type": "Subtitle", "Index": 3, "Language": "spa"}])
        self.assertEqual(fluentfin_osc.sidecar_labels(v, 2), ("SDH", "eng"))
        self.assertEqual(fluentfin_osc.sidecar_labels(v, 3), ("", "spa"))

    def test_nothing_known(self):
        self.assertEqual(fluentfin_osc.sidecar_labels(self.video([]), 1),
                         ("", ""))
        self.assertEqual(fluentfin_osc.sidecar_labels(object(), 1), ("", ""))


class BridgeTurnsItOnTest(unittest.TestCase):
    """Read from the source: the player is built at import, and its OSC and
    options are chosen then, so the switch must come first."""

    def test_before_the_player_is_imported(self):
        src = (PKG / "bridge.py").read_text(encoding="utf-8")
        body = src[src.index("    def start_player("):]
        body = body[:body.index("\n    def ")]
        self.assertLess(body.index("fluentfin_osc.configure(True"),
                        body.index("from . import player"))


if __name__ == "__main__":
    unittest.main()
