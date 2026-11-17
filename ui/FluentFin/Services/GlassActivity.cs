using Microsoft.UI.Xaml;

namespace FluentFin.Services;

/// <summary>
/// 1.1.4: the glass reacts to the window being active, as Windows' own materials do. While the main window is in the background
/// the light edge and the inner glow of every glass surface dim (<see cref="SeaglassTheme"/>), the pointer highlights go out and
/// the backdrop stops moving (no frames drawn for a window nobody is using). Raised on the UI thread.
/// </summary>
public static class GlassActivity
{
	private static Window? _window;

	/// <summary>Whether the main window is the active window (true until told otherwise).</summary>
	public static bool IsActive { get; private set; } = true;

	public static event EventHandler? Changed;

	/// <summary>Follow <paramref name="window"/>'s activation. Once; later calls are ignored.</summary>
	public static void Attach(Window window)
	{
		if (_window is not null)
		{
			return;
		}

		_window = window;
		window.Activated += (_, e) => Set(e.WindowActivationState != WindowActivationState.Deactivated);
	}

	/// <summary>How much of the light edge and the glow a window keeps: all when active, about half in the background.</summary>
	public static double EdgeLevel(bool active) => active ? 1.0 : 0.45;

	internal static void Set(bool active)
	{
		if (active == IsActive)
		{
			return;
		}

		IsActive = active;
		Changed?.Invoke(null, EventArgs.Empty);
	}
}
