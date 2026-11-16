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
	public Task<nint> GetHandleAsync() => window.GetHandleAsync();

	public async Task<BridgeWindow> GetWindowAsync()
	{
		if (UsesFluentFinControls(settings, out var why))
		{
			return await composition.GetWindowAsync();
		}

		if (why is not null)
		{
			logger.LogWarning("FluentFin's player controls were chosen, but {Why}: mpv's controls instead", why);
		}

		return BridgeWindow.Embedded(await window.GetHandleAsync());
	}

	/// <summary>
	/// FluentFin draws the controls: chosen in Settings and libmpv can be loaded. <paramref name="whyNot"/> says why not when
	/// they were chosen but cannot be had (null otherwise). The player page asks the same, so the two always agree.
	/// </summary>
	public static bool UsesFluentFinControls(ISettings settings, out string? whyNot)
	{
		whyNot = null;
		if (settings.MpvPlayerControls != MpvPlayerControls.FluentFin)
		{
			return false;
		}

		if (!MpvCompositionVideo.IsAvailable(out var why))
		{
			whyNot = why;
			return false;
		}

		return true;
	}
}
