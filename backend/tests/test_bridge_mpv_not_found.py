"""fluentfin-mpv (release 1.0): ``connect`` answers ``mpv_not_found`` when the mpv
the backend would start itself cannot be found (BRIDGE.md section 2), and
``mpv_failed`` for every other way mpv fails to start.

No player import: the runtime is test_bridge's fake, whose ``start_player``
raises what it is given.
"""

import os
import tempfile
import unittest
from unittest import mock

from tests.test_bridge import SERVER, BridgeCase
from jellyfin_mpv_shim import bridge as bridge_mod  # noqa: E402
from jellyfin_mpv_shim import bridge_protocol as proto  # noqa: E402
from jellyfin_mpv_shim.conf import settings  # noqa: E402


def spawn_missing():
    """What ``CreateProcess`` raises for an executable that is not there:
    ENOENT, ``[WinError 2]``, no file named."""
    return FileNotFoundError(2, "The system cannot find the file specified")


class MpvNotFoundTest(BridgeCase):
    def setUp(self):
        super().setUp()
        patcher = mock.patch.object(settings, "mpv_ext_path", None)
        patcher.start()
        self.addCleanup(patcher.stop)

    def answer(self, window):
        response = self.send("connect", dict(SERVER, protocol=1, window=window))
        self.assertFalse(response["ok"])
        return response["error"]

    def test_no_mpv_binary_is_mpv_not_found_for_a_window_of_the_backends(self):
        for window in ({"mode": "embedded", "hwnd": 4242}, {"mode": "standalone"}):
            with self.subTest(window=window["mode"]):
                self.runtime.fail_start = spawn_missing()
                error = self.answer(window)
                self.assertEqual(error["code"], "mpv_not_found")
                self.assertIn("mpv.exe was not found", error["message"])
                # as for mpv_failed: no window is fixed, a retry may connect
                self.assertIsNone(self.bridge.window)

    def test_the_backend_never_starts_an_attached_mpv_so_never_misses_one(self):
        self.runtime.fail_start = spawn_missing()
        error = self.answer({"mode": "attached", "ipc": "fluentfin-mpv-1-0011223344556677"})
        self.assertEqual(error["code"], "mpv_failed")

    def test_a_named_missing_file_or_an_error_without_errno_is_mpv_failed(self):
        named = FileNotFoundError(2, "No such file or directory", "osc.lua")
        # (OSError(2, ...) would itself be a FileNotFoundError: Python maps ENOENT to it)
        for error in (named, FileNotFoundError("mpv.exe"), PermissionError(13, "Access is denied"), RuntimeError("boom")):
            with self.subTest(error=repr(error)):
                self.runtime.fail_start = error
                self.assertEqual(self.answer({"mode": "standalone"})["code"], "mpv_failed")

    def test_an_mpv_ext_path_that_names_a_file_is_mpv_failed(self):
        with tempfile.TemporaryDirectory() as folder:
            exe = os.path.join(folder, "mpv.exe")
            open(exe, "wb").close()
            with mock.patch.object(settings, "mpv_ext_path", exe):
                self.runtime.fail_start = spawn_missing()
                self.assertEqual(self.answer({"mode": "standalone"})["code"], "mpv_failed")
            # a path that names nothing is the same as none
            with mock.patch.object(settings, "mpv_ext_path", os.path.join(folder, "gone.exe")):
                self.runtime.fail_start = spawn_missing()
                self.assertEqual(self.answer({"mode": "standalone"})["code"], "mpv_not_found")

    def test_the_code_is_one_the_protocol_lists(self):
        self.assertIn("mpv_not_found", proto.ERROR_CODES)
        self.assertEqual(bridge_mod.MPV_NOT_FOUND, "mpv_not_found")


if __name__ == "__main__":
    unittest.main()
