"""Keep one local QA Jellyfin server running, hidden, inside a kill-on-close job (started by qa.ps1).

    pythonw -I -B serve.py --root %USERPROFILE%\\Dev\\jellyfin-qa --version 10.11 [--max-minutes 240]

- Refuses to start unless <root>\\<version>\\config\\network.xml binds 127.0.0.1 only, on the version's port, with
  auto-discovery, UPnP, IPv6 and HTTPS off (a server listening beyond loopback would raise the firewall's prompt).
- jellyfin.exe runs on a private desktop (CreateDesktopW) inside a job with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, the
  pattern of tools\\backend-bench\\hidden_run.py: when this process ends, however it ends, the server and every ffmpeg it
  started end with it. Nothing else is ever touched.
- <root>\\<version>\\run\\state.json names this process and the server (pid and creation time, so qa.ps1 can prove they
  are the ones it started before it acts on them). The run ends when the server exits, when qa.ps1 writes
  <root>\\<version>\\run\\stop, or after --max-minutes (servers run only while a check needs them).
- The server's console output goes to <root>\\<version>\\log\\console.log; this script's own lines to serve.log there.

Stdlib + ctypes only.
"""

import argparse
import ctypes
import json
import msvcrt
import os
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from ctypes import wintypes as wt

PORTS = {"10.11": 8196, "12": 8296}

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
u32 = ctypes.WinDLL("user32", use_last_error=True)

GENERIC_ALL = 0x10000000
CREATE_SUSPENDED = 0x00000004
CREATE_UNICODE_ENVIRONMENT = 0x00000400
CREATE_NO_WINDOW = 0x08000000
STARTF_USESTDHANDLES = 0x00000100
WAIT_TIMEOUT = 0x102
JobObjectExtendedLimitInformation = 9
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("lpReserved", wt.LPWSTR),
                ("lpDesktop", wt.LPWSTR), ("lpTitle", wt.LPWSTR),
                ("dwX", wt.DWORD), ("dwY", wt.DWORD), ("dwXSize", wt.DWORD),
                ("dwYSize", wt.DWORD), ("dwXCountChars", wt.DWORD),
                ("dwYCountChars", wt.DWORD), ("dwFillAttribute", wt.DWORD),
                ("dwFlags", wt.DWORD), ("wShowWindow", wt.WORD),
                ("cbReserved2", wt.WORD), ("lpReserved2", ctypes.c_void_p),
                ("hStdInput", wt.HANDLE), ("hStdOutput", wt.HANDLE),
                ("hStdError", wt.HANDLE)]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE),
                ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD)]


class IO_COUNTERS(ctypes.Structure):
    _fields_ = [(n, ctypes.c_ulonglong) for n in (
        "ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
        "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]


class JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [("PerProcessUserTimeLimit", ctypes.c_longlong),
                ("PerJobUserTimeLimit", ctypes.c_longlong),
                ("LimitFlags", wt.DWORD),
                ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", wt.DWORD),
                ("Affinity", ctypes.c_size_t),
                ("PriorityClass", wt.DWORD),
                ("SchedulingClass", wt.DWORD)]


class JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [("BasicLimitInformation", JOBOBJECT_BASIC_LIMIT_INFORMATION),
                ("IoInfo", IO_COUNTERS),
                ("ProcessMemoryLimit", ctypes.c_size_t),
                ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t),
                ("PeakJobMemoryUsed", ctypes.c_size_t)]


u32.CreateDesktopW.restype = wt.HANDLE
u32.CreateDesktopW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, ctypes.c_void_p, wt.DWORD, wt.DWORD, ctypes.c_void_p]
u32.CloseDesktop.argtypes = [wt.HANDLE]
k32.CreateJobObjectW.restype = wt.HANDLE
k32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wt.LPCWSTR]
k32.SetInformationJobObject.argtypes = [wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD]
k32.AssignProcessToJobObject.argtypes = [wt.HANDLE, wt.HANDLE]
k32.CreateProcessW.argtypes = [wt.LPCWSTR, wt.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, wt.BOOL, wt.DWORD,
                               ctypes.c_void_p, wt.LPCWSTR, ctypes.POINTER(STARTUPINFOW),
                               ctypes.POINTER(PROCESS_INFORMATION)]
k32.ResumeThread.argtypes = [wt.HANDLE]
k32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
k32.WaitForSingleObject.restype = wt.DWORD
k32.GetExitCodeProcess.argtypes = [wt.HANDLE, ctypes.POINTER(wt.DWORD)]
k32.CloseHandle.argtypes = [wt.HANDLE]
k32.TerminateJobObject.argtypes = [wt.HANDLE, wt.UINT]
k32.GetCurrentProcess.restype = wt.HANDLE
k32.TerminateProcess.argtypes = [wt.HANDLE, wt.UINT]
k32.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(wt.FILETIME)] * 4


def _check(ok, what):
    if not ok:
        raise ctypes.WinError(ctypes.get_last_error(), what)
    return ok


def created(handle):
    """The process's creation time as a FILETIME integer (100 ns since 1601, UTC), as .NET's ToFileTimeUtc gives it."""
    c, e, k, u = wt.FILETIME(), wt.FILETIME(), wt.FILETIME(), wt.FILETIME()
    _check(k32.GetProcessTimes(handle, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u)),
           "GetProcessTimes")
    return (c.dwHighDateTime << 32) | c.dwLowDateTime


def check_network(config_dir, port):
    """Problems with network.xml, as a list of strings (empty: loopback only, nothing announced)."""
    path = os.path.join(config_dir, "network.xml")
    if not os.path.exists(path):
        return ["no network.xml (written by seed.ps1 before the first start)"]
    root = ET.parse(path).getroot()
    if root.tag != "NetworkConfiguration":
        return ["network.xml's root is %r, not NetworkConfiguration" % root.tag]

    def text(name):
        el = root.find(name)
        return (el.text or "").strip() if el is not None else None

    def strings(name):
        el = root.find(name)
        return [(s.text or "").strip() for s in el.findall("string")] if el is not None else []

    problems = []
    if strings("LocalNetworkAddresses") != ["127.0.0.1"]:
        problems.append("LocalNetworkAddresses must be exactly 127.0.0.1 (is %r)" % strings("LocalNetworkAddresses"))
    # (name, wanted, whether a missing element is fine because the server's default is the wanted value: 10.11 drops
    # the obsolete EnableUPnP when it saves the file; AutoDiscovery defaults to on, the ports to 8096)
    for name, want, may_miss in (("AutoDiscovery", "false", False), ("EnableUPnP", "false", True),
                                 ("EnableIPv6", "false", True), ("EnableIPv4", "true", True),
                                 ("EnableHttps", "false", True), ("RequireHttps", "false", True),
                                 ("InternalHttpPort", str(port), False), ("PublicHttpPort", str(port), False)):
        if text(name) is None and may_miss:
            continue
        if (text(name) or "").lower() != want:
            problems.append("%s must be %s (is %r)" % (name, want, text(name)))
    return problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--version", required=True, choices=sorted(PORTS))
    ap.add_argument("--max-minutes", type=float, default=240)
    a = ap.parse_args()

    base = os.path.join(os.path.abspath(a.root), a.version)
    port = PORTS[a.version]
    dirs = {k: os.path.join(base, k) for k in ("data", "config", "cache", "log", "run")}
    for d in dirs.values():
        os.makedirs(d, exist_ok=True)
    own_log = open(os.path.join(dirs["log"], "serve.log"), "a", encoding="utf-8", buffering=1)

    def say(text):
        own_log.write("%s serve.py[%d] %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), os.getpid(), text))

    state_file = os.path.join(dirs["run"], "state.json")
    stop_file = os.path.join(dirs["run"], "stop")
    exe = os.path.join(base, "server", "jellyfin.exe")
    if not os.path.exists(exe):
        say("no server at %s" % exe)
        return 2
    problems = check_network(dirs["config"], port)
    if problems:
        say("refused to start: " + "; ".join(problems))
        return 3
    if os.path.exists(stop_file):
        os.remove(stop_file)

    desktop = "nixie-qa-%s-%d" % (a.version.replace(".", ""), os.getpid())
    hdesk = _check(u32.CreateDesktopW(desktop, None, None, 0, GENERIC_ALL, None), "CreateDesktopW")
    job = _check(k32.CreateJobObjectW(None, None), "CreateJobObjectW")
    info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    _check(k32.SetInformationJobObject(job, JobObjectExtendedLimitInformation, ctypes.byref(info),
                                       ctypes.sizeof(info)), "SetInformationJobObject")

    console = open(os.path.join(dirs["log"], "console.log"), "ab", buffering=0)
    h = msvcrt.get_osfhandle(console.fileno())
    os.set_handle_inheritable(h, True)
    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    si.lpDesktop = "WinSta0\\" + desktop
    si.dwFlags = STARTF_USESTDHANDLES
    si.hStdOutput = h
    si.hStdError = h
    si.hStdInput = None
    argv = [exe, "--datadir", dirs["data"], "--configdir", dirs["config"], "--cachedir", dirs["cache"],
            "--logdir", dirs["log"], "--ffmpeg", os.path.join(base, "server", "ffmpeg.exe"),
            "--webdir", os.path.join(base, "server", "jellyfin-web")]
    env = dict(os.environ)
    for k in [k for k in env if k.upper().startswith("JELLYFIN_")]:
        del env[k]          # the command line decides every folder
    block = "".join("%s=%s\0" % kv for kv in sorted(env.items())) + "\0"
    block = ctypes.create_unicode_buffer(block, len(block))
    pi = PROCESS_INFORMATION()
    cmdline = ctypes.create_unicode_buffer(subprocess.list2cmdline(argv))
    _check(k32.CreateProcessW(None, cmdline, None, None, True,
                              CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                              block, os.path.join(base, "server"), ctypes.byref(si), ctypes.byref(pi)),
           "CreateProcessW")
    try:
        _check(k32.AssignProcessToJobObject(job, pi.hProcess), "AssignProcessToJobObject")
    except OSError:
        # never let it run outside the job
        k32.TerminateProcess(pi.hProcess, 1)
        raise
    k32.ResumeThread(pi.hThread)
    started = time.time()
    deadline = started + a.max_minutes * 60
    state = {"version": a.version, "port": port, "url": "http://127.0.0.1:%d" % port,
             "supervisor": {"pid": os.getpid(), "created": created(k32.GetCurrentProcess())},
             "server": {"pid": pi.dwProcessId, "created": created(pi.hProcess)},
             "desktop": desktop, "started": started, "deadline": deadline}
    tmp = state_file + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f)
    os.replace(tmp, state_file)
    say("started jellyfin.exe pid %d on desktop %s, port %d, at most %.0f min" % (
        pi.dwProcessId, desktop, port, a.max_minutes))

    why = "exited"
    while True:
        if k32.WaitForSingleObject(pi.hProcess, 500) != WAIT_TIMEOUT:
            break
        if os.path.exists(stop_file):
            why = "stop requested"
            break
        if time.time() > deadline:
            why = "--max-minutes reached"
            break
    code = wt.DWORD()
    if why != "exited":
        k32.TerminateJobObject(job, 0)
        k32.WaitForSingleObject(pi.hProcess, 15000)
    k32.GetExitCodeProcess(pi.hProcess, ctypes.byref(code))
    # whatever the server left (an ffmpeg) ends with the job
    k32.TerminateJobObject(job, 0)
    say("server %s (exit %d) after %.0f s" % (why, code.value, time.time() - started))
    try:
        with open(state_file, encoding="utf-8") as f:
            if json.load(f).get("supervisor", {}).get("pid") == os.getpid():
                os.remove(state_file)
    except (OSError, ValueError):
        pass
    if os.path.exists(stop_file):
        try:
            os.remove(stop_file)
        except OSError:
            pass
    for hh in (pi.hThread, pi.hProcess, job):
        k32.CloseHandle(hh)
    u32.CloseDesktop(hdesk)
    console.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
