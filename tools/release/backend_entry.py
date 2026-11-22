"""Entry point of the frozen backend, fluentfin-mpv-backend.exe (tools/release/fluentfin-mpv-backend.spec).

The same as ``python -I -m jellyfin_mpv_shim.bridge``: FluentFin starts it with the same arguments
(docs/BRIDGE.md section 1), and argv and the environment carry no secret.

The backend checks certificates against the Windows store, as the UI and libmpv do (BH-140X-M1): truststore is
imported here by name, so PyInstaller always bundles it, and used before anything else is imported. bridge.main uses it
again (a no-op then), for the unfrozen ``python -m`` run.

Nixie 1.6.1 (BH-160-M1): two switches run something else, before anything of the bridge is imported. The backend
starts itself with ``--nixie-skip-analyse ...`` for a skip rule's analysis (jellyfin_mpv_shim/skip_analyse.py: the
libmpv beside it, in a process of its own), and verify-package.ps1 runs ``--nixie-skip-check`` to prove the package
can snap a skip rule with what it ships (skip_snap.self_check). Neither touches the network, the data folder or stdout's
protocol (the check prints one JSON line; it is not a bridge run).
"""

import sys

if sys.argv[1:2] == ["--nixie-skip-analyse"]:
    from jellyfin_mpv_shim.skip_analyse import main as _skip_analyse

    sys.exit(_skip_analyse(sys.argv[2:]))
if sys.argv[1:] == ["--nixie-skip-check"]:
    from jellyfin_mpv_shim.skip_snap import self_check as _skip_check

    sys.exit(_skip_check())

import truststore  # noqa: E402

truststore.inject_into_ssl()

from jellyfin_mpv_shim.bridge import main  # noqa: E402

if __name__ == "__main__":
    main()
