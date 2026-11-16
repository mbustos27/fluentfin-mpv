"""Entry point of the frozen backend, fluentfin-mpv-backend.exe (tools/release/fluentfin-mpv-backend.spec).

The same as ``python -I -m jellyfin_mpv_shim.bridge``: FluentFin starts it with the same arguments
(docs/BRIDGE.md section 1), and argv and the environment carry no secret.
"""

from jellyfin_mpv_shim.bridge import main

if __name__ == "__main__":
    main()
