"""Process safety for the backend probes: a probe ends, suspends or resumes
only a process it can prove is its own, and acts on it only through the
handle it checked.

A process is this run's when, read through one handle opened for it, it was
created at or after the run began and its command line carries the run's
marker (its scratch folder: a bridge's config dir is under it and is passed to
mpv and to the backend). A pid found by ``find`` also carries the creation time
it was found with, and an action on it requires exactly that time, so a pid
Windows gave to another process since is refused. The pid is never opened
again after the check: suspend, resume and terminate go through that handle.

    find(name, marker, since)             this run's processes named ``name``
    terminate(pids, marker, since)        TerminateProcess, each checked first
    suspend(pid, marker, since, resume)   NtSuspendProcess / NtResumeProcess

``since`` is ``time.time()`` when the run began. Refusals are written to
stderr. Stdlib + ctypes only; Windows.
"""
import ctypes
import os
import sys
from ctypes import wintypes as wt

_EPOCH_AS_FILETIME = 116444736000000000   # 1970-01-01 in 100 ns since 1601

PROCESS_TERMINATE = 0x0001
PROCESS_SUSPEND_RESUME = 0x0800
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
_TH32CS_SNAPPROCESS = 0x2
_INVALID_HANDLE = ctypes.c_void_p(-1).value

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_nt = ctypes.WinDLL("ntdll")
_k32.OpenProcess.restype = wt.HANDLE
_k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
_k32.CloseHandle.argtypes = [wt.HANDLE]
_k32.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(wt.FILETIME)] * 4
_k32.TerminateProcess.argtypes = [wt.HANDLE, wt.UINT]
_k32.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR,
                                            ctypes.POINTER(wt.DWORD)]
_k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
_k32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
_nt.NtQueryInformationProcess.restype = ctypes.c_long
_nt.NtQueryInformationProcess.argtypes = [wt.HANDLE, ctypes.c_int,
                                          ctypes.c_void_p, wt.ULONG,
                                          ctypes.POINTER(wt.ULONG)]
_nt.NtSuspendProcess.restype = ctypes.c_long
_nt.NtSuspendProcess.argtypes = [wt.HANDLE]
_nt.NtResumeProcess.restype = ctypes.c_long
_nt.NtResumeProcess.argtypes = [wt.HANDLE]


class _ENTRY(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD),
                ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_size_t),
                ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD),
                ("szExeFile", ctypes.c_wchar * 260)]


_k32.Process32FirstW.argtypes = [wt.HANDLE, ctypes.POINTER(_ENTRY)]
_k32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(_ENTRY)]


class Pid(int):
    """A pid found by find(): it prints and compares as the pid, and also
    carries the creation time (FILETIME UTC), marker and image name it was
    found with, which every later action requires again."""

    def __new__(cls, pid, created, marker, name):
        self = super().__new__(cls, pid)
        self.created, self.marker, self.name = created, marker, name
        return self


def filetime(unix_seconds):
    return int(unix_seconds * 10_000_000) + _EPOCH_AS_FILETIME


def _note(text):
    print("ownproc: " + text, file=sys.stderr, flush=True)


def _norm(path):
    return (path or "").replace("/", "\\").lower()


def _snapshot():
    """(pid, exe name) of every process; names only pick candidates."""
    snap = _k32.CreateToolhelp32Snapshot(_TH32CS_SNAPPROCESS, 0)
    if not snap or snap == _INVALID_HANDLE:
        return []
    out = []
    try:
        entry = _ENTRY()
        entry.dwSize = ctypes.sizeof(_ENTRY)
        ok = _k32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            out.append((int(entry.th32ProcessID), entry.szExeFile))
            ok = _k32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        _k32.CloseHandle(snap)
    return out


def _created(handle):
    c, e, k, u = wt.FILETIME(), wt.FILETIME(), wt.FILETIME(), wt.FILETIME()
    if not _k32.GetProcessTimes(handle, ctypes.byref(c), ctypes.byref(e),
                                ctypes.byref(k), ctypes.byref(u)):
        return 0
    return (c.dwHighDateTime << 32) | c.dwLowDateTime


def _image(handle):
    buf = ctypes.create_unicode_buffer(1024)
    size = wt.DWORD(len(buf))
    if not _k32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
        return ""
    return buf.value


def _command_line(handle):
    """ProcessCommandLineInformation (60): a UNICODE_STRING, then the text."""
    need = wt.ULONG(0)
    _nt.NtQueryInformationProcess(handle, 60, None, 0, ctypes.byref(need))
    if not 0 < need.value <= 1 << 20:
        return ""
    buf = ctypes.create_string_buffer(need.value)
    if _nt.NtQueryInformationProcess(handle, 60, buf, len(buf), ctypes.byref(need)) != 0:
        return ""
    length = ctypes.c_ushort.from_buffer(buf, 0).value
    text = ctypes.c_void_p.from_buffer(buf, ctypes.sizeof(ctypes.c_void_p)).value
    return ctypes.wstring_at(text, length // 2) if text else ""


def _why_not(handle, marker, since_ft, created=None, name=None):
    """None when the process behind ``handle`` is provably this run's, else
    why it is not."""
    at = _created(handle)
    if not at:
        return "no creation time"
    if at < since_ft:
        return "started before this run began"
    if created is not None and at != created:
        return "not the process found earlier (its pid was reused)"
    if name and os.path.basename(_image(handle)).lower() != name.lower():
        return "its image is not " + name
    if not marker or _norm(marker) not in _norm(_command_line(handle)):
        return "this run's marker is not in its command line"
    return None


def find(name, marker, since):
    """This run's processes named ``name`` (e.g. mpv.exe) with ``marker`` in
    their command line, as Pid objects. One with the marker but started before
    the run began is left out (and noted)."""
    since_ft = filetime(since)
    found = []
    for pid, exe in _snapshot():
        if exe.lower() != name.lower():
            continue
        handle = _k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            continue
        try:
            why = _why_not(handle, marker, since_ft, name=name)
            if why is None:
                found.append(Pid(pid, _created(handle), marker, name))
            elif why == "started before this run began" and \
                    _norm(marker) in _norm(_command_line(handle)):
                _note("%s#%d has the marker but %s; not this run's, left alone"
                      % (name, pid, why))
        finally:
            _k32.CloseHandle(handle)
    return found


def _act(pid, access, marker, since, what, action):
    created = getattr(pid, "created", None)
    marker = getattr(pid, "marker", None) or marker
    name = getattr(pid, "name", None)
    handle = _k32.OpenProcess(access | PROCESS_QUERY_LIMITED_INFORMATION,
                              False, int(pid))
    if not handle:
        return False                       # gone
    try:
        why = _why_not(handle, marker, filetime(since), created, name)
        if why is not None:
            _note("refused to %s pid %d: %s" % (what, int(pid), why))
            return False
        return bool(action(handle))
    finally:
        _k32.CloseHandle(handle)


def terminate(pids, marker, since, code=1):
    """Ends each pid that proves this run's (a Pid from find(): with the
    creation time, marker and name it was found with); one bool per pid."""
    return [_act(p, PROCESS_TERMINATE, marker, since, "terminate",
                 lambda h: _k32.TerminateProcess(h, code)) for p in pids]


def suspend(pid, marker, since, resume=False):
    """NtSuspendProcess (NtResumeProcess with ``resume``) on a pid that proves
    this run's; True when it was done."""
    fn = _nt.NtResumeProcess if resume else _nt.NtSuspendProcess
    return _act(pid, PROCESS_SUSPEND_RESUME, marker, since,
                "resume" if resume else "suspend", lambda h: fn(h) == 0)
