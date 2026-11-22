"""Nixie 1.7 (stream T): the request handler's parts the Trakt and TMDb fakes use (headers in any case, the query with its
keys lower-cased, _send and _json), for outside.py's answer(method, path, query, headers, body) hook: the fakes answer
into it, and outside.py sends what they answered."""

import json
import urllib.parse


class Headers(dict):
    """A request's headers, read in any case (as http.server's are)."""

    def get(self, key, default=None):
        low = key.lower()
        for k, v in self.items():
            if k.lower() == low:
                return v
        return default


class Exchange:
    def __init__(self, headers, query):
        self.headers = Headers(headers or {})
        self.q = {k.lower(): v for k, v in urllib.parse.parse_qs(query or "", keep_blank_values=True).items()}
        self.route = None
        self.status, self.payload, self.out = 500, b"", {}

    def _send(self, code, body=b"", ctype=None, headers=None):
        self.status, self.payload, self.out = code, body or b"", dict(headers or {})
        if ctype:
            self.out["Content-Type"] = ctype

    def _json(self, code, obj):
        self._send(code, json.dumps(obj).encode("utf-8") if obj is not None else b"", "application/json; charset=utf-8")

    def result(self):
        """outside.py's (status, headers, payload)."""
        return self.status, self.out, self.payload


def answer(fake, method, path, query, headers):
    """Runs `fake.api` on one request and gives outside.py what it answered."""
    exchange = Exchange(headers, query)
    fake.api(exchange, method, path)
    return exchange.result()
