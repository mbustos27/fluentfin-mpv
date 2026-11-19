using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.1: the window's own keyboard focus (Win32's, under XAML's). Keys typed go to the window that has it; XAML only
/// hears them while its content island has it. Moving XAML's focus takes it along, so a player whose keys stopped after a
/// click worked again once its bars hid (that moves XAML's focus); a click that left the window's focus elsewhere (another of
/// the app's own windows) is taken back here instead, only while the app is the active one.
/// </summary>
public static class KeyboardFocus
{
	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern nint GetActiveWindow();

	/// <summary>
	/// One of this UI thread's windows is the active window (the app is the one in front). Not only the main window's own
	/// handle: while it is active Windows names its content's input window too.
	/// </summary>
	public static bool AppIsActive() => GetActiveWindow() != 0;

	/// <summary>
	/// Makes sure <paramref name="root"/>'s content island has the window's keyboard focus (the window being active): taken
	/// back and logged when it did not, with <paramref name="after"/> saying after what. False when it could not be checked.
	/// </summary>
	public static bool EnsureWindowHasIt(XamlRoot root, string after)
	{
		try
		{
			if (root.ContentIsland is not { } island)
			{
				return false;
			}

			var controller = InputFocusController.GetForIsland(island);
			if (controller.HasFocus)
			{
				return true;
			}

			// never take the keyboard from another application: only while this app is the active one
			if (!AppIsActive())
			{
				return false;
			}

			var taken = controller.TrySetFocus();
			App.GetService<ILogger<MainWindow>>().LogInformation("[keys] the window's content did not have the keyboard after {After}: taken back ({Taken})", after, taken);
			return taken;
		}
		catch (Exception ex)
		{
			App.GetService<ILogger<MainWindow>>().LogDebug(ex, "[keys] the window's keyboard focus could not be checked after {After}", after);
			return false;
		}
	}
}
