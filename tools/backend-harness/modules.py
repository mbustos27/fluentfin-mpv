import os, sys, threading, socket, time
cfg = os.path.join(os.environ.get("PROBE_OUT") or sys.exit("set PROBE_OUT to a scratch folder outside the repo"), "modules-config")
os.makedirs(cfg, exist_ok=True)
from jellyfin_mpv_shim import bridge
bridge.preseed_shim_args(cfg)
bridge.configure_logging(None)
rt = bridge.ShimRuntime(cfg)
rt.load_settings()
info = rt.start_player({"mode": "standalone", "hwnd": None})
time.sleep(1)
interesting = [m for m in sorted(sys.modules) if m.startswith("jellyfin_mpv_shim.") and any(k in m for k in
    ("tray", "single_instance", "clients", "mpvtk_browser", "cli_mgr", "mpv_shim", "update_check", "syncplay", "discord", "mpris", "gamepad", "sync.", "mirror", "cast", "ws_client", "websocket"))]
print("shim modules of interest:", interesting)
print("websocket libs:", [m for m in sys.modules if "websocket" in m.lower()][:5])
print("threads:", sorted(t.name for t in threading.enumerate()))
from jellyfin_mpv_shim.conf import settings
print("settings:", {k: getattr(settings, k) for k in ("enable_gui","check_updates","mpv_idle_quit","discord_presence","mpris","input_gamepad","health_check_interval","auto_download_enable","write_logs","shader_pack_enable")})
sys.stdout.flush()
rt.shutdown()
os._exit(0)
