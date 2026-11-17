import sys as _sys

# A test run on the user's desktop stops here, before anything imports the
# player (which starts a real mpv window). See offscreen_guard.py.
if "unittest" in _sys.modules and not getattr(_sys, "frozen", False):
    from . import offscreen_guard as _offscreen_guard

    _offscreen_guard.check("Running the backend tests directly")
