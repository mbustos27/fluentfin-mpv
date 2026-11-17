using System.Runtime.InteropServices;

namespace FluentFin.Services;

/// <summary>
/// Hides a window from the screen without changing it (DWM's cloak: its surface stays, nothing is drawn). 1.1.2's way of
/// hiding the player's full-screen switch; since 1.1.3 only with <c>SEAGLASS_FULLSCREEN=cloak</c> (to compare: what is behind
/// the window shows meanwhile), the default being a black cover (docs/research/fullscreen-smoothness.md). Always paired:
/// whoever cloaks uncloaks, a thread-pool timer behind it.
/// </summary>
internal static class WindowCloak
{
	private const int DWMWA_CLOAK = 13;
	private const int DWMWA_CLOAKED = 14;

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

	/// <summary>Cloaks or uncloaks; false when DWM refused (the window stays as it is).</summary>
	public static bool Set(nint hwnd, bool cloak)
	{
		var value = cloak ? 1 : 0;
		return hwnd != 0 && DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int)) == 0;
	}

	public static bool IsCloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var value, sizeof(int)) == 0 && value != 0;
}
