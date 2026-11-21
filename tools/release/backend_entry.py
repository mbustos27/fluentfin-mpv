"""Entry point of the frozen backend, fluentfin-mpv-backend.exe (tools/release/fluentfin-mpv-backend.spec).

The same as ``python -I -m jellyfin_mpv_shim.bridge``: FluentFin starts it with the same arguments
(docs/BRIDGE.md section 1), and argv and the environment carry no secret.

The backend checks certificates against the Windows store, as the UI and libmpv do (BH-140X-M1): truststore is
imported here by name, so PyInstaller always bundles it, and used before anything else is imported. bridge.main uses it
again (a no-op then), for the unfrozen ``python -m`` run.
"""

import truststore

truststore.inject_into_ssl()

from jellyfin_mpv_shim.bridge import main  # noqa: E402

if __name__ == "__main__":
    main()
