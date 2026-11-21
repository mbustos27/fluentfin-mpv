"""fluentfin-mpv: the backend trusts the certificates Windows trusts (BH-140X-M1).

FluentFin's UI checks a server's certificate against the Windows store, and
libmpv reads the same store for the stream: its libcurl (the stream, and a
transcode's playlist and segments) asks for the system's store, and FFmpeg's
GnuTLS (only what libcurl does not open) reads it too. requests, left alone,
checks against certifi's copy of Mozilla's list instead, so a server whose
certificate comes from a home or company CA that Windows trusts signed in and
browsed but played nothing (``CERTIFICATE_VERIFY_FAILED`` on PlaybackInfo).

`use_system_trust` swaps the ``ssl`` module's context for truststore's, which
verifies with the operating system's own chain engine (on Windows,
CertGetCertificateChain): requests, urllib3 and urllib all follow. It is called
first thing in the bridge's ``main`` and in the frozen backend's entry point.
truststore is optional, as every dependency beyond the five required ones is
(STARTHERE.md): without it the backend keeps certifi and says so in its log.
"""

import logging
import ssl

log = logging.getLogger("bridge.trust")


def use_system_trust():
    """Verify TLS against the operating system's trust store from now on, in
    this whole process. True when truststore is in use; idempotent."""
    try:
        import truststore
    except ImportError:
        log.warning("truststore is not installed: certificates are checked "
                    "against certifi's list, not the Windows store")
        return False
    if ssl.SSLContext is truststore.SSLContext:
        return True
    try:
        truststore.inject_into_ssl()
    except Exception:
        log.warning("truststore could not be used: certificates are checked "
                    "against certifi's list", exc_info=True)
        return False
    log.debug("TLS certificates are checked against the system's store")
    return True
