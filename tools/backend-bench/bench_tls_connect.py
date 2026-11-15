"""What a NEW HTTPS connection costs the shim's HTTP stack on this machine.

    python bench_tls_connect.py --cert cert.pem --key key.pem

A local TLS server with a self-signed cert. requests (as the apiclient and
utils.is_local_domain use it) verifies against certifi by default, and urllib3
2.x builds an SSLContext and parses that whole bundle for EVERY new connection
(util/ssl_.py ssl_wrap_socket: create_urllib3_context + load_verify_locations).
Each case opens a fresh connection; the verifying cases end in a certificate
error (self-signed), which happens after the bundle load and the handshake --
so the time is an honest lower bound for a real first request.
"""
import argparse, socket, ssl, statistics, threading, time, warnings
import requests, certifi
ap = argparse.ArgumentParser(); ap.add_argument("--cert"); ap.add_argument("--key")
ap.add_argument("-n", type=int, default=10); a = ap.parse_args()
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain(a.cert, a.key)
srv = socket.socket(); srv.bind(("127.0.0.1", 0)); srv.listen(64); port = srv.getsockname()[1]
def serve():
    while True:
        c, _ = srv.accept()
        def h(c=c):
            try:
                s = ctx.wrap_socket(c, server_side=True)
                s.recv(65536)
                s.sendall(b"HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                s.close()
            except Exception:
                c.close()
        threading.Thread(target=h, daemon=True).start()
threading.Thread(target=serve, daemon=True).start()
url = "https://127.0.0.1:%d/" % port
warnings.filterwarnings("ignore")
def case(label, fn):
    v = []
    for _ in range(a.n):
        t = time.perf_counter()
        try: fn()
        except requests.exceptions.SSLError: pass
        v.append((time.perf_counter() - t) * 1000)
    print("%-62s median %7.1f ms  min %7.1f  max %7.1f" % (label, statistics.median(v), min(v), max(v)))
case("requests.get, verify=True (certifi), new connection each", lambda: requests.get(url, timeout=5))
case("requests.get, verify=False, new connection each", lambda: requests.get(url, timeout=5, verify=False))
pre = ssl.create_default_context(cafile=certifi.where())
class Pre(requests.adapters.HTTPAdapter):
    # Both halves are needed: hand urllib3 the context, AND stop requests'
    # cert_verify from setting conn.ca_certs, or urllib3 re-parses the bundle
    # into the shared context on every connection anyway.
    def init_poolmanager(self, *aa, **kw):
        kw["ssl_context"] = pre; return super().init_poolmanager(*aa, **kw)
    def cert_verify(self, conn, url, verify, cert):
        super().cert_verify(conn, url, verify, cert)
        if verify is True:
            conn.ca_certs = None
def with_pre():
    s = requests.Session(); s.mount("https://", Pre())
    s.get(url, timeout=5)
case("Session + adapter with a PRE-LOADED SSLContext, new conn each", with_pre)
print("one-time cost of building that context: see bench docstring (~same as one bundle load)")
