"""The off-screen guard: a test run on the user's desktop stops before mpv.

``jellyfin_mpv_shim/offscreen_guard.py`` and the two lines in the package's
``__init__.py`` that call it whenever ``unittest`` is loaded. Run directly on
the desktop, the suite used to leave idle "Jellyfin MPV Shim" windows behind
(importing ``player.py`` starts a real mpv); now it exits with status 3 and
says how to run it off-screen.
"""

# Run as a script, this is what puts the repo root on sys.path -- without
# it `jellyfin_mpv_shim` resolves to whatever is pip-installed. A no-op
# under `discover`; tests/test_module_paths.py is the guard.
if __name__ == "__main__":
    import os
    import sys

    sys.path.insert(0, os.path.dirname(
        os.path.dirname(os.path.abspath(__file__))))

import io
import json
import os
import subprocess
import sys
import unittest
from unittest import mock

from jellyfin_mpv_shim import offscreen_guard as guard

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class CheckTests(unittest.TestCase):

    def run_check(self, on_user_desktop, override=None, name=None, **more):
        exits, out = [], io.StringIO()
        env = dict(os.environ)
        for known in (guard.OVERRIDE,) + guard.FORMER_OVERRIDES:
            env.pop(known, None)
        if override is not None:
            env[name or guard.OVERRIDE] = override
        env.update(more)
        with mock.patch.dict(os.environ, env, clear=True):
            guard.check("The tests", desktop=lambda: on_user_desktop,
                        exit=exits.append, stream=out)
        return exits, out.getvalue()

    def test_on_the_users_desktop_the_run_ends_and_says_how_to_run_it(self):
        exits, text = self.run_check(True)
        self.assertEqual(exits, [guard.EXIT_CODE])
        self.assertIn("The tests would open real mpv windows", text)
        self.assertIn("run_suite.py", text)
        self.assertIn("hidden_run.py", text)
        self.assertIn(guard.OVERRIDE + "=1", text)

    def test_on_a_private_desktop_it_carries_on(self):
        self.assertEqual(self.run_check(False), ([], ""))

    def test_the_override_lets_a_desktop_run_through(self):
        self.assertEqual(self.run_check(True, override="1"), ([], ""))

    def test_only_1_is_an_override(self):
        exits, _ = self.run_check(True, override="0")
        self.assertEqual(exits, [guard.EXIT_CODE])

    def test_the_override_is_nixies_and_the_message_names_it(self):
        self.assertEqual(guard.OVERRIDE, "NIXIE_TESTS_ON_DESKTOP")
        self.assertIn("NIXIE_TESTS_ON_DESKTOP=1", guard.message("The tests"))

    def test_the_seaglass_name_still_lets_a_desktop_run_through(self):
        self.assertEqual(guard.FORMER_OVERRIDES, ("SEAGLASS_TESTS_ON_DESKTOP",))
        self.assertEqual(
            self.run_check(True, override="1",
                           name="SEAGLASS_TESTS_ON_DESKTOP"), ([], ""))

    def test_the_new_name_wins_over_the_old_one(self):
        exits, _ = self.run_check(True, override="0",
                                  SEAGLASS_TESTS_ON_DESKTOP="1")
        self.assertEqual(exits, [guard.EXIT_CODE])
        self.assertEqual(
            self.run_check(True, override="1",
                           SEAGLASS_TESTS_ON_DESKTOP="0"), ([], ""))

    def test_an_empty_new_name_falls_back_to_the_old_one(self):
        self.assertTrue(guard.overridden(
            {"NIXIE_TESTS_ON_DESKTOP": "", "SEAGLASS_TESTS_ON_DESKTOP": "1"}))
        self.assertFalse(guard.overridden({}))
        self.assertFalse(guard.overridden({"SEAGLASS_TESTS_ON_DESKTOP": "0"}))


#: Imports the package in a fresh interpreter with a stand-in guard that
#: records its calls instead of ending the process.
PROBE = r"""
import json, sys, types
calls = []
fake = types.ModuleType("jellyfin_mpv_shim.offscreen_guard")
fake.check = calls.append
sys.modules["jellyfin_mpv_shim.offscreen_guard"] = fake
if %(unittest)r:
    import unittest
import jellyfin_mpv_shim
print(json.dumps(calls))
"""


class WiringTests(unittest.TestCase):

    def probe(self, with_unittest):
        proc = subprocess.run(
            [sys.executable, "-E", "-c", PROBE % {"unittest": with_unittest}],
            cwd=ROOT, capture_output=True, text=True, timeout=60)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        return json.loads(proc.stdout.strip().splitlines()[-1])

    def test_importing_the_package_under_unittest_runs_the_guard(self):
        self.assertEqual(self.probe(True),
                         ["Running the backend tests directly"])

    def test_the_app_never_runs_it(self):
        # No unittest loaded, as in the app: the guard is not even imported.
        self.assertEqual(self.probe(False), [])


class DesktopTests(unittest.TestCase):

    @unittest.skipIf(guard.overridden(), "running on the desktop on purpose")
    def test_this_run_is_off_the_users_desktop(self):
        # Reaching this line at all means the guard let the run through.
        self.assertFalse(guard.interactive_desktop())


if __name__ == "__main__":
    unittest.main()
