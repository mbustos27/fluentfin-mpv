import json, sys, threading, time
import os as _os  # noqa: E402
_REPO = _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", ".."))  # the repository
sys.path.insert(0, _os.path.join(_REPO, "backend"))
sys.argv = sys.argv[:1]
from tests import test_bridge as tb
from jellyfin_mpv_shim import bridge as bridge_mod, bridge_protocol as proto
from unittest import mock

# What the apiclient would hand over for an item named from a file with an unpaired UTF-16 surrogate:
name = json.loads('{"Name": "Episode \ud83d broken"}')["Name"]
print("decoded name has lone surrogate:", "\ud83d" in name)

sink = tb.Sink()
rt = tb.FakeRuntime()
br = bridge_mod.Bridge(proto.ProtocolWriter(sink), rt, dev=False)
with mock.patch.object(proto, "is_window_handle", return_value=True):
    br.handle_line(json.dumps({"id": 1, "cmd": "connect", "args": dict(protocol=1, window={"mode": "standalone"}, **tb.SERVER)}).encode())
    br.execute(br._control_q.get_nowait())
br.start(stdin=None)                 # hello + control/play/monitor threads
time.sleep(0.3)
monitor = [t for t in br._threads if t.name == "bridge-monitor"][0]
video = tb.FakeVideo("a" * 32, name=name)
rt.pm._video = video                 # the shim's play() succeeded
br.poke(); time.sleep(1.0)
print("monitor alive after item event:", monitor.is_alive())
lines_before = len(bytes(sink.data).splitlines())
rt.pm._player.volume = 55.0; br.poke(); time.sleep(1.0)
lines_after = len(bytes(sink.data).splitlines())
print("events written after a later volume change:", lines_after - lines_before)
t0 = time.time(); br.handle_line(b'{"id": 9, "cmd": "get_state"}\n'); print("get_state took %.2fs" % (time.time() - t0))
