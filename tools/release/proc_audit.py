"""Watches what a FluentFin.exe starts: every process in its tree, the image each runs, its command line and every
module (DLL) it loads, sampled until a stop file appears. Read-only: it opens processes to query them and never
ends, suspends or writes to anything.

    python -I proc_audit.py --root-exe <...\\FluentFin.exe> --out audit.json --stop-file <path> [--interval 0.25]

A process is in the tree when its image is --root-exe and it started after this script did, or its parent is in the
tree and it started after that parent (a reused parent pid is never followed). Stdlib + ctypes only.
"""

import argparse
import ctypes
import json
import os
import time
from ctypes import wintypes as wt

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
ntdll = ctypes.WinDLL("ntdll")

TH32CS_SNAPPROCESS = 0x2
PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_VM_READ = 0x0010
LIST_MODULES_ALL = 0x03
ProcessCommandLineInformation = 60
INVALID_HANDLE_VALUE = wt.HANDLE(-1).value


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD),
                ("szExeFile", wt.WCHAR * 260)]


class UNICODE_STRING(ctypes.Structure):
    _fields_ = [("Length", wt.USHORT), ("MaximumLength", wt.USHORT), ("Buffer", ctypes.c_void_p)]


k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
k32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
k32.Process32FirstW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
k32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
k32.OpenProcess.restype = wt.HANDLE
k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
k32.CloseHandle.argtypes = [wt.HANDLE]
k32.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
k32.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(wt.FILETIME)] * 4
psapi.EnumProcessModulesEx.argtypes = [wt.HANDLE, ctypes.POINTER(wt.HMODULE), wt.DWORD, ctypes.POINTER(wt.DWORD), wt.DWORD]
psapi.GetModuleFileNameExW.argtypes = [wt.HANDLE, wt.HMODULE, wt.LPWSTR, wt.DWORD]
ntdll.NtQueryInformationProcess.argtypes = [wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.ULONG, ctypes.POINTER(wt.ULONG)]


def snapshot():
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE_VALUE:
        return []
    out = []
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(entry)
        ok = k32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            out.append((entry.th32ProcessID, entry.th32ParentProcessID))
            ok = k32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        k32.CloseHandle(snap)
    return out


def open_process(pid, vm=False):
    access = PROCESS_QUERY_LIMITED_INFORMATION | ((PROCESS_QUERY_INFORMATION | PROCESS_VM_READ) if vm else 0)
    return k32.OpenProcess(access, False, pid)


def created(handle):
    times = [wt.FILETIME() for _ in range(4)]
    if not k32.GetProcessTimes(handle, *[ctypes.byref(t) for t in times]):
        return None
    return (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime


def image(handle):
    buf = ctypes.create_unicode_buffer(32768)
    size = wt.DWORD(len(buf))
    return buf.value if k32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)) else None


def command_line(handle):
    size = wt.ULONG(0)
    ntdll.NtQueryInformationProcess(handle, ProcessCommandLineInformation, None, 0, ctypes.byref(size))
    if not size.value:
        return None
    buf = ctypes.create_string_buffer(size.value)
    if ntdll.NtQueryInformationProcess(handle, ProcessCommandLineInformation, buf, size, ctypes.byref(size)) != 0:
        return None
    us = UNICODE_STRING.from_buffer(buf)
    return ctypes.wstring_at(us.Buffer, us.Length // 2) if us.Buffer else ""


def modules(handle):
    needed = wt.DWORD(0)
    arr = (wt.HMODULE * 2048)()
    if not psapi.EnumProcessModulesEx(handle, arr, ctypes.sizeof(arr), ctypes.byref(needed), LIST_MODULES_ALL):
        return []
    out = []
    buf = ctypes.create_unicode_buffer(32768)
    for i in range(min(needed.value // ctypes.sizeof(wt.HMODULE), 2048)):
        if arr[i] and psapi.GetModuleFileNameExW(handle, arr[i], buf, len(buf)):
            out.append(buf.value)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root-exe", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--stop-file", required=True)
    ap.add_argument("--interval", type=float, default=0.25)
    ap.add_argument("--max-seconds", type=float, default=1800)
    args = ap.parse_args()
    root = os.path.normcase(os.path.abspath(args.root_exe))
    start_ft = int((time.time() + 11644473600) * 10_000_000)   # now as a FILETIME
    tracked = {}   # (pid, created) -> record
    not_root = set()   # (pid, created) of processes looked at and not in the tree
    deadline = time.monotonic() + args.max_seconds

    def write():
        tmp = args.out + ".part"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump({"root": args.root_exe, "processes": [
                dict(r, modules=sorted(r["modules"])) for r in tracked.values()]}, fh, indent=1)
        os.replace(tmp, args.out)

    last_write = 0.0
    while not os.path.exists(args.stop_file) and time.monotonic() < deadline:
        live = {}
        for pid, ppid in snapshot():
            if pid == 0:
                continue
            h = open_process(pid)
            if not h:
                continue
            try:
                c = created(h)
                live[pid] = (ppid, c, h)
            except Exception:
                k32.CloseHandle(h)
        try:
            live_keys = {(pid, v[1]) for pid, v in live.items()}
            parents = {key[0]: key for key in tracked if key in live_keys}
            for pid, (ppid, c, h) in live.items():
                key = (pid, c)
                if key in tracked or c is None:
                    continue
                parent = parents.get(ppid) or next((k for k in tracked if k[0] == ppid), None)
                is_child = parent is not None and c >= parent[1]
                if not is_child and (key in not_root or c < start_ft):
                    continue
                path = image(h)
                if not path:
                    continue
                is_root = os.path.normcase(path) == root and c >= start_ft
                if not is_root and not is_child:
                    not_root.add(key)
                if is_root or is_child:
                    hv = open_process(pid, vm=True)
                    cmd = command_line(hv) if hv else None
                    tracked[key] = {"pid": pid, "parent": ppid if is_child else None, "image": path,
                                    "commandLine": cmd, "created": c, "modules": set()}
                    if hv:
                        k32.CloseHandle(hv)
                    parents[pid] = key
            for key, rec in tracked.items():
                if key in live_keys:
                    hv = open_process(key[0], vm=True)
                    if hv:
                        try:
                            rec["modules"].update(modules(hv))
                        finally:
                            k32.CloseHandle(hv)
        finally:
            for _ppid, _c, h in live.values():
                k32.CloseHandle(h)
        if time.monotonic() - last_write > 2:
            write()
            last_write = time.monotonic()
        time.sleep(args.interval)
    write()
    print(f"{args.out}: {len(tracked)} processes")


if __name__ == "__main__":
    main()
