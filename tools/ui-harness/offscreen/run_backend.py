"""Starts the mpv backend's bridge from a given backend checkout, for the off-screen UI harness:

    <venv python.exe> -I -X pycache_prefix=<dir> run_backend.py <backend checkout> --config-dir ... --log-file ...

FluentFin runs this as its backend command (the MpvBackendCommand setting in the run's own data folder) and appends
--config-dir/--log-file. The checkout goes first on sys.path, so a worktree's backend runs even when the venv's editable
install points at another checkout. -I keeps the environment and the user site out; pycache_prefix keeps .pyc files out
of the checkout. argv carries no secret (docs/BRIDGE.md section 1).
"""
import os
import runpy
import sys

if len(sys.argv) < 2 or not os.path.isfile(os.path.join(sys.argv[1], "jellyfin_mpv_shim", "bridge.py")):
    sys.stderr.write("run_backend.py: the first argument must be a backend checkout (with jellyfin_mpv_shim/bridge.py)\n")
    sys.exit(2)
checkout = os.path.abspath(sys.argv.pop(1))
sys.path.insert(0, checkout)
runpy.run_module("jellyfin_mpv_shim.bridge", run_name="__main__", alter_sys=True)
