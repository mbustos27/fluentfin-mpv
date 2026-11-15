import os, sys, time, subprocess
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE); sys.path.insert(0, r"C:\Users\moise\Dev\fluentfin-mpv\backend\tools")
from bridge_smoke import BridgeProcess
from probe import PY, OUT, MKV, UID, TA, X, FakeJF, connect_args, mpv_procs, kill_pids, wait_state
A = FakeJF("A", TA, MKV, UID)
b = BridgeProcess(PY, OUT, "ports", dev=True)
try:
    b.wait_event("hello", timeout=30)
    print("connect", b.request("connect", connect_args(A, TA), timeout=60).get("ok"))
    m = b.mark(); b.request("play", {"itemIds": [X]}); print("playing", bool(wait_state(b, "playing", m)))
    time.sleep(2)
    pids = [b.proc.pid] + mpv_procs(b.config_dir)
    ps = ("$root=%d; $kids=@(Get-CimInstance Win32_Process -Filter \"ParentProcessId=$root\" | ForEach-Object { $_.ProcessId }); "
          "$p=@($root)+$kids+@(%s); 'pids: ' + ($p -join ','); "
          "Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $p -contains $_.OwningProcess } | "
          "ForEach-Object { 'TCP {0}:{1} -> {2}:{3} {4} pid {5}' -f $_.LocalAddress,$_.LocalPort,$_.RemoteAddress,$_.RemotePort,$_.State,$_.OwningProcess };"
          "Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $p -contains $_.OwningProcess } | "
          "ForEach-Object { 'UDP {0}:{1} pid {2}' -f $_.LocalAddress,$_.LocalPort,$_.OwningProcess }") % (b.proc.pid, ",".join(str(p) for p in pids[1:]) or "0")
    print(subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=60).stdout)
    print("fake server port", A.port)
    b.request("shutdown", timeout=40); print("exit", b.finish(60))
finally:
    kill_pids(mpv_procs(b.config_dir)); b.kill()
