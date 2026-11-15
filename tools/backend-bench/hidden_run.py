"""Run a command on a private, invisible Windows desktop, inside a kill-on-close job.

    python hidden_run.py [--log FILE] [--env K=V ...] [--timeout SECS] -- cmd arg...

Why: on Windows there is no xvfb, and importing jellyfin_mpv_shim.player starts a
real mpv window (STARTHERE.md "Always xvfb-run"). A process started on a desktop
created with CreateDesktopW draws its windows there, so nothing appears on (or
steals focus from) the user's desktop. Children (mpv.exe spawned by
python-mpv-jsonipc) inherit the desktop because subprocess passes lpDesktop=NULL.

The job object has JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so every process the
command starts -- including any mpv a test leaves behind -- is killed when this
launcher exits. Only processes we started are ever touched.

Stdlib + ctypes only.
"""

import argparse
import ctypes
import msvcrt
import os
import subprocess
import sys
import time
from ctypes import wintypes as wt

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
u32 = ctypes.WinDLL("user32", use_last_error=True)

GENERIC_ALL = 0x10000000
CREATE_SUSPENDED = 0x00000004
CREATE_UNICODE_ENVIRONMENT = 0x00000400
CREATE_NO_WINDOW = 0x08000000
STARTF_USESTDHANDLES = 0x00000100
INFINITE = 0xFFFFFFFF
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
u32.CreateDesktopW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, ctypes.c_void_p,
                               wt.DWORD, wt.DWORD, ctypes.c_void_p]
k32.CreateJobObjectW.restype = wt.HANDLE
k32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wt.LPCWSTR]
k32.SetInformationJobObject.argtypes = [wt.HANDLE, ctypes.c_int,
                                        ctypes.c_void_p, wt.DWORD]
k32.AssignProcessToJobObject.argtypes = [wt.HANDLE, wt.HANDLE]
k32.CreateProcessW.argtypes = [wt.LPCWSTR, wt.LPWSTR, ctypes.c_void_p,
                               ctypes.c_void_p, wt.BOOL, wt.DWORD,
                               ctypes.c_void_p, wt.LPCWSTR,
                               ctypes.POINTER(STARTUPINFOW),
                               ctypes.POINTER(PROCESS_INFORMATION)]
k32.ResumeThread.argtypes = [wt.HANDLE]
k32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
k32.WaitForSingleObject.restype = wt.DWORD
k32.GetExitCodeProcess.argtypes = [wt.HANDLE, ctypes.POINTER(wt.DWORD)]
k32.CloseHandle.argtypes = [wt.HANDLE]
k32.TerminateJobObject.argtypes = [wt.HANDLE, wt.UINT]


def _check(ok, what):
    if not ok:
        raise ctypes.WinError(ctypes.get_last_error(), what)
    return ok


def run_hidden(argv, log=None, env=None, timeout=None, desktop="jms-perf-hidden",
               cwd=None):
    hdesk = _check(u32.CreateDesktopW(desktop, None, None, 0, GENERIC_ALL, None),
                   "CreateDesktopW")
    job = _check(k32.CreateJobObjectW(None, None), "CreateJobObjectW")
    info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    _check(k32.SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                                       ctypes.byref(info), ctypes.sizeof(info)),
           "SetInformationJobObject")

    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    si.lpDesktop = "WinSta0\\" + desktop
    inherit = False
    logf = None
    if log:
        logf = open(log, "ab", buffering=0)
        h = msvcrt.get_osfhandle(logf.fileno())
        os.set_handle_inheritable(h, True)
        si.dwFlags |= STARTF_USESTDHANDLES
        si.hStdOutput = h
        si.hStdError = h
        si.hStdInput = None
        inherit = True

    block = None
    if env is not None:
        s = "".join("%s=%s\0" % kv for kv in sorted(env.items())) + "\0"
        block = ctypes.create_unicode_buffer(s, len(s))
    pi = PROCESS_INFORMATION()
    cmdline = ctypes.create_unicode_buffer(subprocess.list2cmdline(argv))
    _check(k32.CreateProcessW(None, cmdline, None, None, inherit,
                              CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT
                              | CREATE_NO_WINDOW,
                              block, cwd, ctypes.byref(si), ctypes.byref(pi)),
           "CreateProcessW")
    _check(k32.AssignProcessToJobObject(job, pi.hProcess), "AssignProcessToJobObject")
    k32.ResumeThread(pi.hThread)
    t0 = time.time()
    ms = INFINITE if timeout is None else int(timeout * 1000)
    rc_wait = k32.WaitForSingleObject(pi.hProcess, ms)
    code = wt.DWORD()
    if rc_wait == WAIT_TIMEOUT:
        k32.TerminateJobObject(job, 124)
        k32.WaitForSingleObject(pi.hProcess, 10000)
        code.value = 124
    else:
        k32.GetExitCodeProcess(pi.hProcess, ctypes.byref(code))
    elapsed = time.time() - t0
    # Closing the job kills anything the command left running (stray mpv).
    k32.TerminateJobObject(job, 0)
    for h in (pi.hThread, pi.hProcess, job):
        k32.CloseHandle(h)
    u32.CloseDesktop(wt.HANDLE(hdesk))
    if logf:
        logf.close()
    return code.value, elapsed


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log")
    ap.add_argument("--env", action="append", default=[],
                    help="K=V added to (or replacing in) the inherited environment")
    ap.add_argument("--timeout", type=float)
    ap.add_argument("--cwd")
    ap.add_argument("--desktop", default="jms-perf-hidden")
    ap.add_argument("cmd", nargs=argparse.REMAINDER)
    a = ap.parse_args()
    cmd = a.cmd[1:] if a.cmd and a.cmd[0] == "--" else a.cmd
    env = dict(os.environ)
    for kv in a.env:
        k, _, v = kv.partition("=")
        env[k] = v
    rc, secs = run_hidden(cmd, log=a.log, env=env, timeout=a.timeout,
                          desktop=a.desktop, cwd=a.cwd)
    print("hidden_run: exit %d after %.1fs" % (rc, secs))
    return rc


if __name__ == "__main__":
    sys.exit(main())
