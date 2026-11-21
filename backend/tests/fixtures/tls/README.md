# Test-only TLS fixtures (fluentfin-mpv)

- `ca.pem`: a private CA made for the tests ("Nixie tests only private CA"). Its key was thrown away after signing.
- `server.pem` and `server.key`: a server certificate for `127.0.0.1` and `localhost`, signed by that CA, and its key.

They stand in for a home or company certificate (BH-140X-M1). The tests trust `ca.pem` for one request or one
context only: it is never added to a Windows certificate store, and nothing outside the tests may use these files.
Remade with OpenSSL (P-256, 100 years) if they ever need to change.
