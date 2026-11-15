"""The bridge's guard around one Jellyfin client's requests.

Two jobs, both bridge mode only (docs/bridge.md section 3):

1. **A retried POST stays a POST.** ``jellyfin_apiclient_python``'s
   ``HTTP.request`` pops the method out of the request dict *inside* its
   retry loop (``data.pop('type', "GET")``, ``http.py:149`` in 1.21.0), so
   the first attempt takes it and every retry goes out as a ``GET`` with the
   body still attached. A progress or Stopped report that timed out once was
   retried as ``GET /Sessions/Playing/Progress``: a 405 on a real server, a
   lost report either way. The guard remembers the method per call and
   hands it to every attempt (`ClientGuard._requests`). The apiclient is
   not edited (site-packages); upstream is the place to fix it for good.

2. **A switched-away client cannot hold anybody up** (`ClientGuard.retire`).
   The player reports under one lock (``_tl_lock``) and one FIFO worker
   (``SessionReporter``), shared by every client, and holds ``_lock``
   across PlaybackInfo. A server switch therefore queued the new server's
   reports behind the old server's hung one -- measured 31 s for the first
   ``Playing`` -- and a switch that stopped playback inline waited as long.
   So every request runs on a helper thread while its caller waits; once
   the client is retired, a caller waits at most `RETIRED_WAIT` more and
   gets ``HTTPException("ServerUnreachable")``, the request's later retries
   are refused, and new requests get one attempt bounded by
   `RETIRED_TIMEOUT`. A healthy old server still gets its Stopped report; a
   hung one may lose it.

3. **How each Stopped report ended** (`watch_stops`, BRIDGE.md section 5's
   ``reported``). The guard keeps the HTTP status of the last attempt of
   each call (`ClientGuard.last_status`, per calling thread), because the
   apiclient answers None for a 204 *and* for a 500 -- "no exception" is
   not "accepted". A report the guard gave up on (a retired client) raised,
   and is ``failed``.

4. **A PlaybackInfo answer that is not one raises** (`check_play_info`).
   The apiclient answers None for a 500, an empty or a non-JSON body, and
   `Video.get_playback_url` stored that and dereferenced it: a TypeError,
   which the bridge could only call ``internal`` ("'NoneType' object is not
   subscriptable"). BRIDGE.md section 5 makes it ``server_unreachable``, so
   the None is turned into the apiclient's own ``HTTPException`` where it
   comes back, before anything stores it.

No shim imports: the apiclient and the standard library only.
"""

import logging
import threading
import time

log = logging.getLogger("bridge")

#: How long anything waits on a request of a retired client -- one already
#: in flight when it was retired, or one made after -- before giving up on
#: it. The request itself may go on in the background, within its timeout.
RETIRED_WAIT = 2.0

#: The socket timeout of a request a retired client makes, with no retry.
RETIRED_TIMEOUT = 5.0

#: How often a waiting caller looks at the retirement flag.
_POLL = 0.05


def _unreachable(why):
    from jellyfin_apiclient_python.exceptions import HTTPException

    return HTTPException("ServerUnreachable", why)


class ClientGuard:
    """Wraps ``client.http.request`` and ``client.http._requests`` on the
    instance. See the module docstring."""

    def __init__(self, client):
        self.client = client
        http = client.http
        #: The apiclient's own, bound to its HTTP object.
        self._request = http.request
        self._send = http._requests
        self._retired = threading.Event()
        self._retired_at = None
        self._grace = RETIRED_WAIT
        #: Per helper thread: the method of the call it is making, how many
        #: attempts it has sent, and the status the last one got.
        self._local = threading.local()
        #: Per calling thread: the status of its last call (`last_status`).
        self._caller = threading.local()
        http.request = self.request
        http._requests = self._requests

    @property
    def retired(self):
        return self._retired.is_set()

    def retire(self, grace=None):
        """From now on nothing waits on this client for long: requests in
        flight get ``grace`` seconds (default `RETIRED_WAIT`) more, later
        ones one bounded attempt."""
        if self._retired.is_set():
            return
        self._grace = RETIRED_WAIT if grace is None else grace
        self._retired_at = time.monotonic()
        self._retired.set()

    # -- the apiclient's two entry points --------------------------------

    def last_status(self):
        """The HTTP status the last attempt of this thread's last call got,
        or None: no answer (it raised before one, or was given up on)."""
        return getattr(self._caller, "status", None)

    def request(self, data, session=None, dest_file=None):
        """``HTTP.request``, on a helper thread, waited for until it ends --
        or, once the client is retired, until the grace runs out."""
        self._caller.status = None
        method = "GET"
        if isinstance(data, dict):
            method = data.get("type") or "GET"
            if self._retired.is_set():
                data["retry"] = 0
                data["timeout"] = min(data.get("timeout") or RETIRED_TIMEOUT,
                                      RETIRED_TIMEOUT)
        started = time.monotonic()
        box = {}
        done = threading.Event()

        def work():
            self._local.method = method
            self._local.attempts = 0
            self._local.status = None
            try:
                box["result"] = self._request(data, session, dest_file)
            except BaseException as error:      # handed to the caller
                box["error"] = error
            finally:
                box["status"] = self._local.status
                done.set()

        threading.Thread(target=work, name="bridge-http", daemon=True).start()
        while not done.wait(_POLL):
            if not self._retired.is_set():
                continue
            limit = max(started, self._retired_at) + self._grace
            if time.monotonic() >= limit and not done.is_set():
                log.info("Not waiting for a %s to a server the backend has "
                         "switched away from.", method)
                raise _unreachable("the client was switched away from")
        self._caller.status = box.get("status")
        if "error" in box:
            raise box["error"]
        return box.get("result")

    def _requests(self, session, action, **kwargs):
        """One attempt. ``action`` is what the apiclient popped -- "GET" on
        every retry -- so the call's own method is used instead."""
        local = self._local
        method = getattr(local, "method", None)
        if method is None:
            # Not a call `request` made (nothing else should reach here).
            return self._send(session, action, **kwargs)
        attempt = local.attempts
        local.attempts = attempt + 1
        local.status = None
        if attempt and self._retired.is_set():
            # A retry of a request that was in flight when the client was
            # retired: its caller has moved on.
            raise _unreachable("no retries after a switch")
        response = self._send(session, method, **kwargs)
        local.status = getattr(response, "status_code", None)
        return response


def guard(client):
    """Install a `ClientGuard` on ``client`` (once) and return it."""
    existing = getattr(client, "bridge_guard", None)
    if existing is not None:
        return existing
    client.bridge_guard = ClientGuard(client)
    return client.bridge_guard


def retire(client, grace=None):
    """Retire ``client``'s guard, if it has one."""
    existing = getattr(client, "bridge_guard", None)
    if existing is not None:
        existing.retire(grace)


def watch_stops(client, on_outcome):
    """Wrap ``client.jellyfin.session_stop`` so that every Stopped report
    calls ``on_outcome(item_id, play_session_id, outcome)`` once it has
    returned or raised: ``"sent"`` only on a 2xx answer (`last_status`),
    ``"failed"`` on anything else -- a 500 the apiclient swallowed, an
    error, a timeout, a retired client giving up. ``on_outcome`` runs on
    the reporting thread (the report worker, or the caller once the worker
    is stopped) and must only hand the outcome on. Install after `guard`."""
    api = client.jellyfin
    original = api.session_stop
    guard_ = getattr(client, "bridge_guard", None)

    def session_stop(data):
        outcome = "failed"
        try:
            result = original(data)
            status = guard_.last_status() if guard_ is not None else None
            if status is not None and 200 <= status < 300:
                outcome = "sent"
            return result
        finally:
            body = data if isinstance(data, dict) else {}
            try:
                on_outcome(body.get("ItemId"), body.get("PlaySessionId"),
                           outcome)
            except Exception:
                log.debug("could not hand on a Stopped outcome",
                          exc_info=True)

    api.session_stop = session_stop
    return session_stop


def check_play_info(client):
    """Wrap ``client.jellyfin.get_play_info`` so that an answer that is not
    a usable PlaybackInfo -- a 500 or an empty or non-JSON body, which the
    apiclient returns as None, or JSON with no ``MediaSources`` list --
    raises ``HTTPException("ServerUnreachable")`` with a readable message,
    instead of reaching `Video.get_playback_url` as None (BRIDGE.md section
    5: ``server_unreachable``, never ``internal``). ``MediaSources: []`` is
    an answer -- an item with nothing to play -- and passes. Install after
    `guard`: the status in the message is the guard's, for this thread."""
    api = client.jellyfin
    original = api.get_play_info
    guard_ = getattr(client, "bridge_guard", None)

    def get_play_info(*args, **kwargs):
        info = original(*args, **kwargs)
        if isinstance(info, dict) and isinstance(info.get("MediaSources"),
                                                 list):
            return info
        status = guard_.last_status() if guard_ is not None else None
        if status is not None and not 200 <= status < 300:
            why = "the server answered PlaybackInfo with HTTP %d" % status
        elif info is None:
            why = "the server's PlaybackInfo answer was empty or not JSON"
        else:
            why = "the server's PlaybackInfo answer lists no media sources"
        log.warning("%s", why)
        raise _unreachable(why)

    api.get_play_info = get_play_info
    return get_play_info
