using System.Runtime.InteropServices;

namespace FluentFin.Services;

/// <summary>
/// Hides a window from the screen without changing it (DWM's cloak: its surface stays, nothing is drawn). The player
/// cloaks its window while the presenter goes full screen and back, so the few frames in which the window already has its
/// new size and the XAML layout does not yet never show (docs/research/fullscreen-smoothness.md).
/// <c>FLUENTFIN_NO_CLOAK=1</c> turns it off (to compare). Always paired: whoever cloaks uncloaks, a timer behind it.
/// </summary>
internal static class WindowCloak
{
	private const int DWMWA_CLOAK = 13;
	private const int DWMWA_CLOAKED = 14;

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

	public static bool Enabled { get; } = Environment.GetEnvironmentVariable("FLUENTFIN_NO_CLOAK") != "1";

	/// <summary>Cloaks or uncloaks; false when DWM refused (the window stays as it is).</summary>
	public static bool Set(nint hwnd, bool cloak)
	{
		var value = cloak ? 1 : 0;
		return hwnd != 0 && DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int)) == 0;
	}

	public static bool IsCloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var value, sizeof(int)) == 0 && value != 0;
}
