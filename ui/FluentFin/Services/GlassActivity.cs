using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace FluentFin.Services;

/// <summary>
/// 1.1.4: the glass reacts to the window being active, as Windows' own materials do. While the main window is in the background
/// the light edge and the inner glow of every glass surface dim (<see cref="NixieTheme"/>), the pointer highlights go out and
/// the backdrop stops moving (no frames drawn for a window nobody is using). Raised on the UI thread.
/// </summary>
public static class GlassActivity
{
	private static Window? _window;
	private static ILogger? _log;

	/// <summary>Whether the main window is the active window (true until told otherwise).</summary>
	public static bool IsActive { get; private set; } = true;

	public static event EventHandler? Changed;

	/// <summary>Follow <paramref name="window"/>'s activation (logged on each change). Once; later calls are ignored.</summary>
	public static void Attach(Window window, ILogger? log = null)
	{
		if (_window is not null)
		{
			return;
		}

		_window = window;
		_log = log;
		if (!FollowsActivity(Environment.GetEnvironmentVariable(AlwaysActiveVariable), Core.AppVersion.IsReleasePackage))
		{
			// the off-screen harness: other processes' windows take activation on its private desktop, which a real desktop's
			// foreground lock prevents, so its shots and measurements would show the background state throughout
			_log?.LogInformation("[glass] window activity not followed ({Variable}=1): the glass stays in its active state", AlwaysActiveVariable);
			return;
		}

		window.Activated += (_, e) => Set(e.WindowActivationState != WindowActivationState.Deactivated);
	}

	/// <summary>Set to 1 (the off-screen harness only): the glass keeps its active state whatever the window's activation.</summary>
	public const string AlwaysActiveVariable = "FLUENTFIN_MPV_GLASS_ALWAYS_ACTIVE";

	/// <summary>
	/// Whether the window's activation is followed: always in a release package (the switch is for the harness's dev builds only),
	/// otherwise unless <see cref="AlwaysActiveVariable"/> is exactly 1.
	/// </summary>
	public static bool FollowsActivity(string? alwaysActive, bool releasePackage) => releasePackage || alwaysActive != "1";

	/// <summary>How much of the light edge and the glow a window keeps: all when active, about half in the background.</summary>
	public static double EdgeLevel(bool active) => active ? 1.0 : 0.45;

	internal static void Set(bool active)
	{
		if (active == IsActive)
		{
			return;
		}

		IsActive = active;
		_log?.LogInformation("[glass] window {State}: the light edge, the glow and the backdrop's motion follow", active ? "active" : "in the background");
		Changed?.Invoke(null, EventArgs.Empty);
	}
}
