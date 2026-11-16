using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using Microsoft.Extensions.Logging;

namespace FluentFin.Mpv;

/// <summary>
/// fluentfin-mpv player controls: where the backend's mpv shows its video, by the "Player controls" setting
/// (docs/research/player-controls.md). mpv's controls: the backend's own mpv in <see cref="MpvVideoHost"/>'s child window
/// (embedded). FluentFin's: the backend attaches to <see cref="MpvCompositionVideo"/>'s in-process mpv. Read at each
/// connect, so a changed setting takes effect at the next playback (the window differs, so the backend restarts).
/// </summary>
public sealed class MpvVideoSurfaceChoice(MpvVideoHost window, MpvCompositionVideo composition, ISettings settings, ILogger<MpvVideoSurfaceChoice> logger) : IMpvVideoSurface
{
	/// <summary>Whether FluentFin's controls are used now, and why not when chosen (tests replace it).</summary>
	internal Func<(bool Use, string? WhyNot)> Choice { get; set; } = () => (UsesFluentFinControls(settings, out var whyNot), whyNot);

	/// <summary>The in-process mpv's window, a new core first when there is none (tests replace it).</summary>
	internal Func<Task<BridgeWindow>> AttachedWindow { get; set; } = composition.GetWindowAsync;

	public Task<nint> GetHandleAsync() => window.GetHandleAsync();

	public async Task<BridgeWindow> GetWindowAsync()
	{
		var (use, why) = Choice();
		if (use)
		{
			// making the core (mpv_create, mpv_initialize: its GPU device and swap chain) takes a while: on the thread pool,
			// never on the UI thread a connect may come from
			return await Task.Run(AttachedWindow);
		}

		if (why is not null)
		{
			logger.LogWarning("FluentFin's player controls were chosen, but {Why}: mpv's controls instead", why);
		}

		return BridgeWindow.Embedded(await window.GetHandleAsync());
	}

	/// <summary>The backend that had this window ended: FluentFin's mpv it was attached to ends with it (docs/BRIDGE.md §4).</summary>
	public void OnBackendExited(BridgeWindow window) => composition.OnBackendExited(window);

	/// <summary>
	/// FluentFin draws the controls: chosen in Settings and libmpv can be loaded and does composition output.
	/// <paramref name="whyNot"/> says why not when they were chosen but cannot be had (null otherwise). The player page asks
	/// the same, so the two always agree.
	/// </summary>
	public static bool UsesFluentFinControls(ISettings settings, out string? whyNot) => UsesFluentFinControls(settings, Libmpv.Unavailable, out whyNot);

	/// <summary>As above, with why libmpv cannot be used (null: it can) from <paramref name="unavailable"/>: the real probe, of another file in tests.</summary>
	internal static bool UsesFluentFinControls(ISettings settings, Func<string?> unavailable, out string? whyNot)
	{
		whyNot = null;
		if (settings.MpvPlayerControls != MpvPlayerControls.FluentFin)
		{
			return false;
		}

		if (unavailable() is { } why)
		{
			whyNot = why;
			return false;
		}

		return true;
	}
}
