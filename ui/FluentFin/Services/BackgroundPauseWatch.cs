using FluentFin.Core.SmartCollections;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.System.Power;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.9 (stream text; design rule 5): background work pauses while nobody is there. Windows says when the PC goes to
/// standby or comes back (<see cref="PowerManager.SystemSuspendStatusChanged"/>), when the display goes off
/// (<see cref="PowerManager.DisplayStatusChanged"/>), and whether the window is minimised; each is a reason of
/// <see cref="PauseGate.App"/>, which the summaries' pass (<see cref="TextPass"/>) waits on before each request.
/// </summary>
internal static class BackgroundPauseWatch
{
	public const string Standby = "standby";
	public const string DisplayOff = "display off";
	public const string Minimised = "minimised";

	private static bool s_started;

	/// <summary>Follows Windows' power and the window's state (once; later calls are ignored).</summary>
	public static void Start(Window window, ILogger? log = null)
	{
		if (s_started)
		{
			return;
		}

		s_started = true;
		var gate = PauseGate.App;
		gate.Changed += paused => log?.LogInformation("[background] {State} ({Why})", paused ? "paused" : "going again", paused ? gate.Why : "nothing holds it");
		try
		{
			PowerManager.SystemSuspendStatusChanged += (_, _) => gate.Set(Standby, PowerManager.SystemSuspendStatus == SystemSuspendStatus.Entering);
			PowerManager.DisplayStatusChanged += (_, _) => gate.Set(DisplayOff, PowerManager.DisplayStatus == DisplayStatus.Off);
		}
		catch (Exception ex)
		{
			// a Windows that can't say: the window's own state still pauses it
			log?.LogInformation("[background] Windows' power notices aren't available: {Error}", ex.GetType().Name);
		}

		// the review of wip/190-text, item 8: Windows' own word (IsIconic, as the player page reads it) at any change of the
		// window, and at its activation's (a minimise always deactivates it): the change's kinds (presenter, size, visibility)
		// don't always say a minimise
		var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
		bool Iconic() => handle != 0 && IsIconic(handle);
		window.AppWindow.Changed += (_, _) => WindowChanged(gate, Iconic);
		window.Activated += (_, _) => WindowChanged(gate, Iconic);
		WindowChanged(gate, Iconic);
	}

	/// <summary>
	/// The window changed in any way: minimised is what Windows says of it now (<paramref name="iconic"/>), whatever the change
	/// was (the review's item 8: filtered on presenter, size or visibility changes, a minimise could pass unseen).
	/// </summary>
	internal static void WindowChanged(PauseGate gate, Func<bool> iconic) => gate.Set(Minimised, iconic());

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
	private static extern bool IsIconic(nint hwnd);
}
