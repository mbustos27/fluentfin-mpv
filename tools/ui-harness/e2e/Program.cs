// End-to-end check of the crash rule (docs/BRIDGE.md §3, §6) with the REAL backend, the job launcher and FluentFin.Core's
// MpvBackendHost / MpvPlaybackService / CloseSequence. The fake servers (tools/backend-harness/rr/fakejf2.py, fake token)
// run in the driver (drive.py), which counts the Stopped requests by sender: User-Agent "ui-harness-e2e" = sent by
// FluentFin's crash report. Started by drive.py, normally through run.ps1 (off-screen).
//
//   e2e <python> <outDir> <label> <scenario> <serverUrl> <userId> [<serverUrlB>]
//   environment: E2E_FAKE_TOKEN = the fake servers' token (never a real one); for user_switch_then_kill also
//                E2E_FAKE_TOKEN_2 and E2E_USER_2, a second user of server A. Both tokens are cleared from this process's
//                environment as soon as they are read: the backend inherits it (JobProcess passes no environment of its
//                own), and docs/BRIDGE.md §1 says the environment carries no secrets. ENVCHECK then reads the environment
//                blocks of the backend and everything it started (the venv launcher, the interpreter, mpv) and prints
//                whether a token is in any of them.
//
// Writes <outDir>\<label>-host.log, -service.log, -backend.log and a <label>-config folder; prints events and the result.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Mpv;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

static class P
{
	public static readonly Stopwatch Clock = Stopwatch.StartNew();
	static readonly object OutLock = new();
	public static void Out(string s) { lock (OutLock) Console.WriteLine($"[{Clock.Elapsed.TotalSeconds,7:0.000}] {s}"); }

	const string X = "11111111111111111111111111111111";
	const string Y = "22222222222222222222222222222222";

	static readonly ConcurrentQueue<(double T, BridgeEvent E)> Events = new();
	static int Interrupts;

	static async Task<bool> WaitUntil(Func<bool> f, double seconds)
	{
		var end = Clock.Elapsed.TotalSeconds + seconds;
		while (Clock.Elapsed.TotalSeconds < end)
		{
			if (f()) return true;
			await Task.Delay(20);
		}
		return f();
	}

	/// <summary>A server URL without credentials or query (this harness also builds against FluentFin.Core copies older than BridgeRedaction.ServerForLog).</summary>
	public static string SafeServer(string? url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? $"{u.Scheme}://{u.Authority}" : "(none)";

	static IEnumerable<BridgeEvent> All(string name) => Events.Select(x => x.E).Where(e => e.Name == name);
	static string? S(BridgeEvent e, string key) => (e.Data as JsonObject)?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

	static Task<bool> WaitState(string state, string item, double secs = 40) =>
		WaitUntil(() => All("state").Any(e => S(e, "state") == state && S(e, "itemId") == item), secs);

	static string? SessionOf(string item) =>
		All("state").Where(e => S(e, "itemId") == item && S(e, "playSessionId") is { Length: > 0 }).Select(e => S(e, "playSessionId")).LastOrDefault();

	static async Task<bool> WaitPositions(int n, double secs = 20)
	{
		var start = All("position").Count();
		return await WaitUntil(() => All("position").Count() >= start + n, secs);
	}

	static Task<bool> WaitReported(string? psid, double secs = 20) =>
		WaitUntil(() => All("reported").Any(e => S(e, "playSessionId") == psid), secs);

	/// <summary>
	/// docs/BRIDGE.md §1: the environment carries no secrets. Reads the environment block of every process in the backend's
	/// job (the venv launcher, the interpreter, mpv: exactly what the backend started, no unrelated process with a reused
	/// parent pid) and prints whether any holds a token; never prints a token.
	/// </summary>
	static void EnvCheck(int backendPid, string[] secrets)
	{
		var own = Environment.GetEnvironmentVariable("E2E_FAKE_TOKEN") is not null || Environment.GetEnvironmentVariable("E2E_FAKE_TOKEN_2") is not null
			? "; e2e.exe itself still has a token variable, which the backend inherits"
			: "";
		var read = new List<string>();
		var leaks = new List<string>();
		var unreadable = new List<string>();
		var job = BackendJobObject();
		foreach (var (pid, name) in BackendJob())
		{
			var env = job is null ? null : Win.EnvironmentOf(pid, job);
			if (env is null || !env.Contains("SystemRoot=", StringComparison.OrdinalIgnoreCase))
			{
				unreadable.Add($"{name}#{pid}");
				continue;
			}

			read.Add($"{name}#{pid}");
			if (secrets.Any(s => env.Contains(s, StringComparison.Ordinal)))
			{
				leaks.Add($"{name}#{pid}");
			}
		}

		Out(leaks.Count > 0 ? $"ENVCHECK FAIL: a token is in the environment of {string.Join(",", leaks)}{own}"
			: read.Count < 2 ? $"ENVCHECK FAIL: only {read.Count} environment(s) readable in the job of pid {backendPid} (unreadable: {string.Join(",", unreadable)}){own}"
			: own.Length > 0 ? $"ENVCHECK FAIL: no token found in {string.Join(",", read)}{own}"
			: $"ENVCHECK ok: no token in the environment of {string.Join(",", read)}{(unreadable.Count > 0 ? $" (unreadable: {string.Join(",", unreadable)})" : "")}");
	}

	/// <summary>The job the backend this started last runs in (kill-on-close: everything in it is this run's).</summary>
	static JobObject? BackendJobObject() => (Launcher.Last as WindowsMpvBackendLauncher.BackendProcess)?.Job;

	/// <summary>
	/// The processes in the backend's job: only what the backend started (a parent pid can be a reused one). A pid from
	/// this list is acted on only through a handle that proved it still a member (Win.Suspend, Win.Terminate).
	/// </summary>
	static List<(int Pid, string Name)> BackendJob() => BackendJobObject() is { } job ? Win.InJob(job.Handle) : [];

	/// <summary>The real interpreter behind the venv launcher: the python.exe in the backend's job that is not the launcher.</summary>
	static (int Pid, string Name) Interpreter(int launcherPid) =>
		BackendJob().First(k => k.Pid != launcherPid && k.Name.Equals("python.exe", StringComparison.OrdinalIgnoreCase));

	static string Summ(BridgeEvent e) => e.Name switch
	{
		"state" => $"{S(e, "state")} item={S(e, "itemId")?[..4]} ps={S(e, "playSessionId")}",
		"reported" => $"ps={S(e, "playSessionId")} outcome={(e.Data as JsonObject)?["outcome"]}",
		"ended" => $"item={S(e, "itemId")?[..4]} reason={S(e, "reason")}",
		"error" => $"code={S(e, "code")} fatal={(e.Data as JsonObject)?["fatal"]}",
		"position" => $"{(e.Data as JsonObject)?["positionTicks"]}",
		_ => "",
	};

	/// <summary>fluentfin-mpv (startup-perf): the scenarios of the pre-warmed backend, and the cold play it is timed against.</summary>
	static readonly string[] PrewarmScenarios = ["prewarm_idle", "prewarm_play", "prewarm_kill_idle", "prewarm_signout", "cold_play", "prewarm_play_wan", "cold_play_wan"];

	/// <summary>
	/// The backend pre-warmed as FluentFin does after sign-in (MpvPrewarm, found by reflection: a FluentFin.Core given with
	/// -UiDir may be older), idle 5 s with no window of mpv's inside ours, then: nothing (idle), a sign-out, a kill while idle
	/// then a play, or a play (timed: PLAYTIME). cold_play: the same play with nothing pre-warmed.
	/// </summary>
	static async Task<string> PrewarmRun(string scenario, MpvBackendHost host, MpvPlaybackService service, BridgeIdentity identity, nint hwnd,
										 string outDir, string label, string[] secrets, FileLogger<MpvBackendHost> hostLog)
	{
		object? prewarm = null;
		IDisposable? prewarmLog = null;
		try
		{
			if (!scenario.StartsWith("cold_play"))
			{
				var type = typeof(MpvBackendHost).Assembly.GetType("FluentFin.Core.Mpv.MpvPrewarm");
				if (type is null) return "this FluentFin.Core has no MpvPrewarm (before startup-perf)";
				var logType = typeof(FileLogger<>).MakeGenericType(type);
				prewarmLog = (IDisposable)Activator.CreateInstance(logType, Path.Combine(outDir, label + "-prewarm.log"))!;
				prewarm = Activator.CreateInstance(type, host, DispatchProxy.Create<FluentFin.Core.Settings.ISettings, SettingsProxy>(), prewarmLog)!;
				type.GetProperty("Delay")!.SetValue(prewarm, TimeSpan.Zero);
				type.GetProperty("Identity")!.SetValue(prewarm, new Func<BridgeIdentity?>(() => identity));
				var t0 = Clock.Elapsed.TotalSeconds;
				type.GetMethod("SignedIn")!.Invoke(prewarm, null);
				await (Task)type.GetProperty("Current")!.GetValue(prewarm)!;
				Out($"PREWARM done after {Clock.Elapsed.TotalSeconds - t0:0.000} s: status={host.Status.State} mpv={host.Status.MpvVersion}");
				if (host.Status.State != MpvBackendState.Running || host.Status.MpvVersion is null) return "the pre-warm did not connect";
				EnvCheck(hostLog.BackendPid, secrets);
				await Task.Delay(5000);
				var idle = Win.MpvChild(hwnd);
				Out($"IDLE mpv window inside ours: {(idle == 0 ? "none" : $"0x{idle:x}")}; backend job: {string.Join(",", BackendJob().Select(k => k.Name))}");
				// E2E_ALLOW_IDLE_WINDOW=1: timing a backend from before connect.prewarm (it ignores it and makes its window at connect)
				if (idle != 0 && Environment.GetEnvironmentVariable("E2E_ALLOW_IDLE_WINDOW") != "1") return "mpv made a window inside ours while idle";
			}

			switch (scenario)
			{
				case "prewarm_idle":
					return "ok";
				case "prewarm_signout":
					{
						var type = prewarm!.GetType();
						type.GetMethod("SignedOut")!.Invoke(prewarm, null);
						await (Task)type.GetProperty("Current")!.GetValue(prewarm)!;
						Out($"SIGNEDOUT status={host.Status.State}");
						return host.Status.State == MpvBackendState.Stopped ? "ok" : $"the backend is {host.Status.State} after the sign-out";
					}
				case "prewarm_kill_idle":
					host.Kill("e2e kill while idle (pre-warmed)");
					await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
					Out($"KILLED while idle: status={host.Status.State} interrupts={Volatile.Read(ref Interrupts)}");
					if (Volatile.Read(ref Interrupts) != 0) return "a crash while idle raised an interrupted playback";
					goto case "cold_play";
				case "prewarm_play":
				case "prewarm_play_wan":
				case "cold_play_wan":
				case "cold_play":
					{
						var gen = service.Begin();
						var t0 = Clock.Elapsed.TotalSeconds;
						await service.PlayAsync(gen, identity, new BridgePlayRequest([X]));
						var sent = Clock.Elapsed.TotalSeconds;
						if (scenario.StartsWith("cold_play")) EnvCheck(hostLog.BackendPid, secrets);
						if (!await WaitState("playing", X)) return "X never played";
						var playing = Clock.Elapsed.TotalSeconds;
						await WaitUntil(() => All("position").Any(e => (e.Data as JsonObject)?["positionTicks"] is JsonValue v && v.TryGetValue(out long ticks) && ticks > 0), 20);
						var position = Clock.Elapsed.TotalSeconds;
						Out($"PLAYTIME {scenario}: play sent {(sent - t0) * 1000:0} ms, playing {(playing - t0) * 1000:0} ms, first position {(position - t0) * 1000:0} ms after PlayAsync");
						await WaitUntil(() => Win.MpvChild(hwnd) != 0, 5);
						var child = Win.MpvChild(hwnd);
						Out($"PLAYING mpv window inside ours: {(child == 0 ? "none" : $"0x{child:x}")}");
						if (child == 0) return "no window of mpv's inside ours while playing";
						await WaitPositions(3);
						return "ok";
					}
				default:
					return "unknown scenario " + scenario;
			}
		}
		finally
		{
			prewarm?.GetType().GetMethod("Cancel")!.Invoke(prewarm, ["e2e run done"]);
			prewarmLog?.Dispose();
		}
	}

	static async Task<int> Main(string[] a)
	{
		if (a.Length < 6)
		{
			Console.Error.WriteLine("usage: e2e <python> <outDir> <label> <scenario> <serverUrl> <userId> [<serverUrlB>]  (E2E_FAKE_TOKEN in the environment)");
			return 2;
		}

		var (python, outDir, label, scenario, url, uid) = (a[0], a[1], a[2], a[3], a[4], a[5]);
		var urlB = a.Length > 6 ? a[6] : null;
		var token = Environment.GetEnvironmentVariable("E2E_FAKE_TOKEN");
		var token2 = Environment.GetEnvironmentVariable("E2E_FAKE_TOKEN_2");
		var uid2 = Environment.GetEnvironmentVariable("E2E_USER_2");
		// read once, then gone from this process's environment, which the backend inherits (ENVCHECK below)
		Environment.SetEnvironmentVariable("E2E_FAKE_TOKEN", null);
		Environment.SetEnvironmentVariable("E2E_FAKE_TOKEN_2", null);
		if (string.IsNullOrEmpty(token))
		{
			Console.Error.WriteLine("E2E_FAKE_TOKEN is not set");
			return 2;
		}

		string[] secrets = string.IsNullOrEmpty(token2) ? [token] : [token, token2];

		Directory.CreateDirectory(outDir);
		var configDir = Path.Combine(outDir, label + "-config");
		Directory.CreateDirectory(configDir);
		// no sound from a test run: mpv reads mpv.conf from the backend's config dir (this run's own)
		File.WriteAllText(Path.Combine(configDir, "mpv.conf"), "ao=null" + Environment.NewLine);
		var logFile = Path.Combine(outDir, label + "-backend.log");
		var hostLog = new FileLogger<MpvBackendHost>(Path.Combine(outDir, label + "-host.log"));
		if (scenario.StartsWith("held_kill")) hostLog.Gate = "Killing the mpv backend";
		var svcLog = new FileLogger<MpvPlaybackService>(Path.Combine(outDir, label + "-service.log"));

		// FluentFin's client: on server A as user uid until the scenario logs in elsewhere
		JfProxy.Url = url;
		JfProxy.Token = token;
		JfProxy.User = Guid.Parse(uid);
		var jf = DispatchProxy.Create<IJellyfinClient, JfProxy>();
		var hwnd = Win.MakeWindow();
		Out($"window 0x{hwnd:x}");

		var launcher = new Launcher(python, configDir, logFile);
		var host = new MpvBackendHost(launcher, new Surface(hwnd), hostLog);
		var service = new MpvPlaybackService(host, jf, svcLog);
		host.Events.Subscribe(e => { Events.Enqueue((Clock.Elapsed.TotalSeconds, e)); if (e.Name != "position" || All("position").Count() <= 2) Out($"event {e.Name} {Summ(e)}"); });
		host.PlaybackInterrupted.Subscribe(i => { Interlocked.Increment(ref Interrupts); Out($"INTERRUPTED item={i.ItemId[..4]} ps={i.PlaySessionId} pos={i.PositionTicks} server={SafeServer(i.GetType().GetProperty("ServerUrl")?.GetValue(i) as string)} reason={i.Reason}"); });
		var identity = new BridgeIdentity(url, "sA", "A", uid, "u", token, "dev-e2e", "E2E", "FluentFin", "0.0.1");

		var result = "ok";
		try
		{
			if (PrewarmScenarios.Contains(scenario))
			{
				// fluentfin-mpv (startup-perf): connect.prewarm (docs/BRIDGE.md §3, §4), then the close below
				result = await PrewarmRun(scenario, host, service, identity, hwnd, outDir, label, secrets, hostLog);
				if (result != "ok") throw new Exception(result);
			}
			else
			{
			var gen = service.Begin();
			var items = scenario.StartsWith("next") ? new[] { X, Y } : new[] { X };
			await service.PlayAsync(gen, identity, new BridgePlayRequest(items));
			Out("play sent");

			if (scenario == "kill_opening")
			{
				// the driver delays X's stream: X sits in `opening` with its play session
				var opened = await WaitUntil(() => All("state").Any(e => S(e, "state") == "opening" && S(e, "playSessionId") is { Length: > 0 }), 30);
				Out($"opening with a session: {opened}");
				EnvCheck(hostLog.BackendPid, secrets);
				await Task.Delay(300);
				host.Kill("e2e kill while opening");
				await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
			}
			else
			{
				if (!await WaitState("playing", X)) { result = "X never played"; throw new Exception(result); }
				await WaitPositions(scenario.StartsWith("next") || scenario.StartsWith("replace") ? 9 : 3);
				var psX = SessionOf(X);
				Out($"X playing, ps={psX}");
				EnvCheck(hostLog.BackendPid, secrets);

				switch (scenario)
				{
					case "signout_playing":
					case "signout_hung_stopped":
						{
							// what Log out / Switch user do (TitleBarViewModel): the page's playback stops and the backend's Stopped
							// report is waited for (bounded, ~2 s), then the login page, then the token is revoked. A FluentFin.Core
							// from before round 6 has no StopForSignOutAsync: it went to the login page and revoked at once, and the
							// page it left kept playing
							var t0 = Clock.Elapsed.TotalSeconds;
							var signOut = service.GetType().GetMethod("StopForSignOutAsync");
							if (signOut is null)
							{
								Out("SIGNOUT this FluentFin.Core has no StopForSignOutAsync (before round 6): revoking at once");
							}
							else
							{
								await (Task)signOut.Invoke(service, [TimeSpan.FromSeconds(2)])!;
							}
							Out($"SIGNOUT stop and report took {Clock.Elapsed.TotalSeconds - t0:0.000} s; reported X before the token was revoked: {All("reported").Any(e => S(e, "playSessionId") == psX)}");
							await jf.Logout();
							Out("SIGNOUT token revoked");
							await Task.Delay(1500);
							break;
						}
					case "reported_then_kill":
						await host.SendAsync(Bridge.Commands.Stop);
						Out($"reported X: {await WaitReported(psX)}");
						await Task.Delay(500);
						host.Kill("e2e kill after reported");
						await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
						break;
					case "kill_playing":
						host.Kill("e2e kill while playing");
						await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
						break;
					case "crash_interp":
						{
							// a crash that is not a kill: the real interpreter behind the venv launcher dies; the launcher and an
							// external mpv are left to notice
							var pid = hostLog.BackendPid;
							var kids = BackendJob();
							Out($"launcher {pid}, in its job {string.Join(",", kids.Select(k => k.Pid + ":" + k.Name))}");
							var interp = Interpreter(pid);
							if (!Win.Terminate(interp.Pid, 3, BackendJobObject()))
							{
								result = $"could not end the interpreter {interp.Pid} as a member of the backend's job";
								throw new Exception(result);
							}

							await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
							break;
						}
					case "close_playing":
					case "close_hung_stopped":
						break;
					case "close_suspended":
						{
							// a backend that cannot answer at all (its interpreter suspended): the close must kill it 10 s after `shutdown`
							var pid = hostLog.BackendPid;
							var interp = Interpreter(pid);
							if (!Win.Suspend(interp.Pid, BackendJobObject()))
							{
								result = $"could not suspend the interpreter {interp.Pid} as a member of the backend's job";
								throw new Exception(result);
							}

							Out($"suspended interpreter {interp.Pid}");
							break;
						}
					case "stop_then_close":
						await host.SendAsync(Bridge.Commands.Stop);
						break;
					case "stop_then_kill":
						await host.SendAsync(Bridge.Commands.Stop);
						host.Kill("e2e kill right after stop");
						await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
						break;
					case "replace_then_kill":
						{
							await service.PlayAsync(gen, identity, new BridgePlayRequest([Y]));
							Out($"Y playing: {await WaitState("playing", Y)}");
							await WaitPositions(3);
							Out($"reported X: {await WaitReported(psX)}");
							host.Kill("e2e kill after replace");
							await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
							break;
						}
					case "next_then_kill":
						{
							await host.SendAsync(Bridge.Commands.Next);
							Out($"Y playing: {await WaitState("playing", Y)}");
							await WaitPositions(3);
							Out($"reported X: {await WaitReported(psX)}");
							host.Kill("e2e kill after next");
							await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
							break;
						}
					case "switch_then_kill":
						{
							// FluentFin logs in to server B (its client moves there), then the next play connects the backend to B while
							// A's Stopped hangs: X's `reported` comes only after the connect answer, and the backend dies first
							if (urlB is null) { result = "switch_then_kill needs <serverUrlB>"; throw new Exception(result); }
							var identityB = new BridgeIdentity(urlB, "sB", "B", uid, "u", token, "dev-e2e", "E2E", "FluentFin", "0.0.1");
							JfProxy.Url = urlB;
							var t0 = Clock.Elapsed.TotalSeconds;
							await host.ConnectAsync(identityB);
							Out($"connect to B answered after {Clock.Elapsed.TotalSeconds - t0:0.000} s; reported X yet: {All("reported").Any(e => S(e, "playSessionId") == psX)}");
							host.Kill("e2e kill right after the switch");
							await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
							break;
						}
					case "user_switch_then_kill":
						{
							// FluentFin logs in as another user of the same server (its client switches user), then the next play
							// connects the backend as that user while the first user's Stopped hangs: X's `reported` comes only after
							// the connect answer, and the backend dies first
							if (string.IsNullOrEmpty(token2) || string.IsNullOrEmpty(uid2)) { result = "user_switch_then_kill needs E2E_FAKE_TOKEN_2 and E2E_USER_2"; throw new Exception(result); }
							var identity2 = new BridgeIdentity(url, "sA", "A", uid2, "u2", token2, "dev-e2e", "E2E", "FluentFin", "0.0.1");
							JfProxy.Token = token2;
							JfProxy.User = Guid.Parse(uid2);
							var t0 = Clock.Elapsed.TotalSeconds;
							await host.ConnectAsync(identity2);
							Out($"connect as user 2 answered after {Clock.Elapsed.TotalSeconds - t0:0.000} s; reported X yet: {All("reported").Any(e => S(e, "playSessionId") == psX)}");
							host.Kill("e2e kill right after the user switch");
							await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
							break;
						}
					case "replay_opening_kill":
						{
							// X stopped and reported; X played again (the driver now delays its stream): crash while the new session opens
							await host.SendAsync(Bridge.Commands.Stop);
							Out($"reported X: {await WaitReported(psX)}");
							await service.PlayAsync(gen, identity, new BridgePlayRequest([X]));
							var opened = await WaitUntil(() => All("state").Any(e => S(e, "state") == "opening" && S(e, "playSessionId") is { Length: > 0 } p && p != psX), 30);
							Out($"X opening again with a new session: {opened} ({SessionOf(X)})");
							await Task.Delay(300);
							host.Kill("e2e kill while the replay opens");
							await WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10);
							break;
						}
					case "kill_then_close":
						host.Kill("e2e kill, then close at once");
						break;
					case "held_kill_close":
						// the watchdog has decided to kill and is held in its log line (until the close returns): the close finishes the kill
						_ = Task.Run(() => host.Kill("e2e watchdog held in its log line"));
						Out($"watchdog in its log line: {hostLog.Entered.Wait(5000)}");
						break;
					default:
						result = "unknown scenario " + scenario;
						throw new Exception(result);
				}
			}

			}

			// what closing FluentFin does (App.AppWindow_Closing)
			var closeStart = Clock.Elapsed.TotalSeconds;
			await CloseSequence.RunAsync(jf, service, svcLog);
			Out($"CLOSE returned after {Clock.Elapsed.TotalSeconds - closeStart:0.000} s: interrupts={Volatile.Read(ref Interrupts)} uiStopCalls={Volatile.Read(ref JfProxy.StopCalls)} uiStopDone={Volatile.Read(ref JfProxy.StopDone)}");
			hostLog.Release.Set();
			await Task.Delay(2500);
		}
		catch (Exception ex)
		{
			result = "harness error: " + ex.GetType().Name + ": " + ex.Message;
			Out(result);
		}
		finally
		{
			// everything this started: the backend's job (and its mpv) ends here at the latest
			hostLog.Release.Set();
			host.Dispose();
			hostLog.Dispose();
			svcLog.Dispose();
		}

		Out($"RESULT {scenario}: {result}; interrupts={Volatile.Read(ref Interrupts)} uiStopCalls={Volatile.Read(ref JfProxy.StopCalls)} uiStopDone={Volatile.Read(ref JfProxy.StopDone)} status={host.Status.State}");
		Out("reported events: " + string.Join("; ", All("reported").Select(Summ)));
		return 0;
	}
}

sealed class Launcher(string python, string configDir, string logFile) : IMpvBackendLauncher
{
	/// <summary>
	/// E2E_BACKEND_DIR (run.ps1 -BackendDir): the backend folder to run, with python -E -s and that folder as the working
	/// directory, instead of -I, which imports the installed package (an editable install: the checkout it was made from; a
	/// git worktree shares the main checkout's venv).
	/// </summary>
	static readonly string? BackendDir = Environment.GetEnvironmentVariable("E2E_BACKEND_DIR") is { Length: > 0 } dir ? dir : null;

	public MpvBackendCommand? Resolve(bool dev, out string searched)
	{
		searched = python;
		var args = new List<string> { "-m", MpvBackendCommand.BridgeModule, "--config-dir", configDir, "--log-file", logFile };
		args.InsertRange(0, BackendDir is null ? ["-I"] : ["-E", "-s"]);
		if (dev) args.Add("--dev");
		return new MpvBackendCommand(python, args, "ui-harness-e2e");
	}

	/// <summary>The backend this started last (ENVCHECK reads its job's processes).</summary>
	public static IMpvBackendProcess? Last;

	public IMpvBackendProcess Start(MpvBackendCommand command) => Last = WindowsMpvBackendLauncher.StartInJob(command.FileName, command.Arguments, BackendDir ?? configDir);
}

sealed class Surface(nint hwnd) : IMpvVideoSurface
{
	public Task<nint> GetHandleAsync() => Task.FromResult(hwnd);
}

/// <summary>Writes every message to a file; with <see cref="Gate"/>, holds the first thread that logs it until <see cref="Release"/>.</summary>
sealed class FileLogger<T> : ILogger<T>, IDisposable
{
	readonly StreamWriter _w;
	readonly object _lock = new();
	int _once;
	public string? Gate;
	public ManualResetEventSlim Entered { get; } = new();
	public ManualResetEventSlim Release { get; } = new();
	public int BackendPid;

	public FileLogger(string path) => _w = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
	public bool IsEnabled(LogLevel level) => true;

	public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f)
	{
		var text = f(state, ex);
		lock (_lock)
		{
			try { _w.WriteLine($"[{P.Clock.Elapsed.TotalSeconds,7:0.000}] {level}: {text}{(ex is null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}"); }
			catch (ObjectDisposedException) { }
		}
		if (!text.StartsWith("[mpv backend]")) P.Out($"{typeof(T).Name} {level}: {text}");
		var m = System.Text.RegularExpressions.Regex.Match(text, @"is running: pid (\d+)");
		if (m.Success) BackendPid = int.Parse(m.Groups[1].Value);
		if (Gate is { } g && text.Contains(g, StringComparison.Ordinal) && Interlocked.Exchange(ref _once, 1) == 0)
		{
			Entered.Set();
			Release.Wait(TimeSpan.FromSeconds(15));
		}
	}

	public void Dispose()
	{
		lock (_lock) _w.Dispose();
	}
}

/// <summary>
/// FluentFin's IJellyfinClient as far as the crash report uses it: <c>BaseUrl</c> and <c>UserId</c> (where and as whom it
/// is logged in) and <c>Stop(info)</c>, sent there with that user's token.
/// </summary>
/// <summary>fluentfin-mpv (startup-perf): FluentFin's settings as the pre-warm reads them: the mpv player selected.</summary>
public class SettingsProxy : DispatchProxy
{
	protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
	{
		"get_MediaPlayer" => MediaPlayerType.Mpv,
		"get_Servers" => new System.Collections.ObjectModel.ObservableCollection<FluentFin.Core.Settings.SavedServer>(),
		"get_MpvBackendCommand" => "",
		_ => method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null,
	};
}

public class JfProxy : DispatchProxy
{
	public static volatile string Url = "";
	public static volatile string Token = "";
	public static Guid User;
	static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
	public static int StopCalls, StopDone;

	protected override object? Invoke(MethodInfo? method, object?[]? args)
	{
		if (method!.Name == "get_BaseUrl") return Url;
		if (method.Name == "get_UserId") return User;
		if (method.Name == "Stop" && args is [PlaybackStopInfo info]) return SendStop(info);
		if (method.Name == "Logout") return SendLogout();
		var rt = method.ReturnType;
		if (rt == typeof(Task)) return Task.CompletedTask;
		if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
		{
			var t = rt.GetGenericArguments()[0];
			return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, [t.IsValueType ? Activator.CreateInstance(t) : null]);
		}
		return rt != typeof(void) && rt.IsValueType ? Activator.CreateInstance(rt) : null;
	}

	/// <summary>FluentFin's sign-out: the server revokes the token (drive.py's fake server stops accepting it).</summary>
	static async Task SendLogout()
	{
		var to = Url;
		using var req = new HttpRequestMessage(HttpMethod.Post, to + "/Sessions/Logout");
		req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Client=\"FluentFin\", Device=\"E2E\", DeviceId=\"dev-e2e\", Version=\"0.0.1\", Token=\"{Token}\"");
		req.Headers.TryAddWithoutValidation("User-Agent", "ui-harness-e2e");
		using var resp = await Http.SendAsync(req);
		P.Out($"UI Logout sent to {P.SafeServer(to)} -> {(int)resp.StatusCode}");
	}

	static async Task SendStop(PlaybackStopInfo info)
	{
		Interlocked.Increment(ref StopCalls);
		var body = new JsonObject
		{
			["ItemId"] = info.ItemId?.ToString("N"),
			["MediaSourceId"] = info.MediaSourceId,
			["PlaySessionId"] = info.PlaySessionId,
			["PositionTicks"] = info.PositionTicks,
			["Failed"] = info.Failed,
		};
		var to = Url;
		using var req = new HttpRequestMessage(HttpMethod.Post, to + "/Sessions/Playing/Stopped")
		{
			Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Client=\"FluentFin\", Device=\"E2E\", DeviceId=\"dev-e2e\", Version=\"0.0.1\", Token=\"{Token}\"");
		req.Headers.TryAddWithoutValidation("User-Agent", "ui-harness-e2e");
		using var resp = await Http.SendAsync(req);
		Interlocked.Increment(ref StopDone);
		P.Out($"UI Stopped sent to {P.SafeServer(to)}: {body.ToJsonString()} -> {(int)resp.StatusCode}");
	}
}

static unsafe class Win
{
	[StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int x, y; public uint priv; }
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
	[DllImport("user32.dll")] static extern int GetMessageW(out MSG m, nint h, uint a, uint b);
	[DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
	[DllImport("user32.dll")] static extern nint DispatchMessageW(ref MSG m);
	const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;

	[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindowExW(nint parent, nint after, string? cls, string? title);

	/// <summary>fluentfin-mpv (startup-perf): mpv's own window inside ours (class "mpv"), 0 when there is none.</summary>
	public static nint MpvChild(nint host) => FindWindowExW(host, 0, "mpv", null);

	/// <summary>The window mpv draws into (on run.ps1's private desktop: nobody sees it). Its thread ends with the process.</summary>
	public static nint MakeWindow()
	{
		var ready = new TaskCompletionSource<nint>();
		var t = new Thread(() =>
		{
			var h = CreateWindowExW(0, "STATIC", "ui-harness-e2e", WS_OVERLAPPEDWINDOW | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, 640, 360, 0, 0, 0, 0);
			ready.SetResult(h);
			while (GetMessageW(out var m, 0, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
		}) { IsBackground = true };
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
		return ready.Task.Result;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct PROCESSENTRY32W { public uint dwSize, cntUsage, th32ProcessID; public nint th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
	[DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(nint snap, ref PROCESSENTRY32W e);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(nint snap, ref PROCESSENTRY32W e);
	[DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
	[DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, int pid);

	[DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(SafeHandle job, int cls, byte[] info, int length, out int returned);

	/// <summary>The processes in a job (JobObjectBasicProcessIdList), with their names: exactly what the backend started.</summary>
	public static List<(int Pid, string Name)> InJob(SafeHandle job)
	{
		var buffer = new byte[16 + 8 * 256];
		if (!QueryInformationJobObject(job, 3, buffer, buffer.Length, out _)) return [];
		var count = BitConverter.ToInt32(buffer, 4);   // NumberOfProcessIdsInList
		var names = new Dictionary<int, string>();
		var snap = CreateToolhelp32Snapshot(2, 0);
		var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
		for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e)) names[(int)e.th32ProcessID] = e.szExeFile;
		CloseHandle(snap);
		var list = new List<(int, string)>();
		for (var i = 0; i < count; i++)
		{
			var pid = (int)BitConverter.ToInt64(buffer, 8 + 8 * i);
			list.Add((pid, names.TryGetValue(pid, out var name) ? name : "?"));
		}
		return list;
	}

	[StructLayout(LayoutKind.Sequential)] struct PROCESS_BASIC_INFORMATION { public nint ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }
	[DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint h, int cls, out PROCESS_BASIC_INFORMATION pbi, int len, out int returned);
	[DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(nint h, nint address, byte[] buffer, nint size, out nint read);

	static nint ReadPointer(nint h, nint address)
	{
		var b = new byte[8];
		return ReadProcessMemory(h, address, b, 8, out var n) && n == 8 ? (nint)BitConverter.ToInt64(b) : 0;
	}

	/// <summary>
	/// A 64-bit process's environment block as text (PEB -> ProcessParameters -> Environment, EnvironmentSize), null when it
	/// cannot be read or the process behind the handle opened for it is not a member of <paramref name="job"/>. For the
	/// ENVCHECK only: the text stays in this process.
	/// </summary>
	public static string? EnvironmentOf(int pid, JobObject job)
	{
		using var handle = new SafeProcessHandle(OpenProcess(0x0400 | 0x0010, false, pid), ownsHandle: true);   // QUERY_INFORMATION | VM_READ
		if (handle.IsInvalid || !job.Contains(handle)) return null;
		var h = handle.DangerousGetHandle();   // valid until the using above disposes the handle
		if (NtQueryInformationProcess(h, 0, out var pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0 || pbi.PebBaseAddress == 0) return null;
		var parameters = ReadPointer(h, pbi.PebBaseAddress + 0x20);
		if (parameters == 0) return null;
		var env = ReadPointer(h, parameters + 0x80);
		var size = (long)ReadPointer(h, parameters + 0x3F0);
		if (env == 0) return null;
		if (size <= 0 || size > 4 << 20) size = 64 * 1024;
		var buffer = new byte[size];
		ReadProcessMemory(h, env, buffer, (nint)size, out var got);
		return got > 0 ? Encoding.Unicode.GetString(buffer, 0, (int)got & ~1) : null;
	}

	[DllImport("ntdll.dll")] static extern int NtSuspendProcess(SafeProcessHandle h);
	[DllImport("kernel32.dll")] static extern bool TerminateProcess(SafeProcessHandle h, uint code);

	/// <summary>
	/// Opens <paramref name="pid"/> once with <paramref name="access"/> (plus QUERY_LIMITED_INFORMATION, for IsProcessInJob)
	/// and returns the handle only if the process behind it is a member of <paramref name="job"/>, this run's backend job:
	/// a pid from the job's list that exited and was reused since is refused. The caller acts through this handle only.
	/// </summary>
	static SafeProcessHandle? OpenJobMember(int pid, uint access, JobObject? job, string what)
	{
		var handle = new SafeProcessHandle(OpenProcess(access | 0x1000, false, pid), ownsHandle: true);
		if (!handle.IsInvalid && job is not null && job.Contains(handle)) return handle;
		handle.Dispose();
		P.Out($"refused to {what} pid {pid}: not a member of the backend's job (or gone)");
		return null;
	}

	/// <summary>NtSuspendProcess through a handle that proved the process a member of the backend's job.</summary>
	public static bool Suspend(int pid, JobObject? job)
	{
		using var h = OpenJobMember(pid, 0x0800, job, "suspend");   // SUSPEND_RESUME
		return h is not null && NtSuspendProcess(h) == 0;
	}

	/// <summary>TerminateProcess through a handle that proved the process a member of the backend's job.</summary>
	public static bool Terminate(int pid, uint code, JobObject? job)
	{
		using var h = OpenJobMember(pid, 0x0001, job, "terminate");   // TERMINATE
		return h is not null && TerminateProcess(h, code);
	}
}
