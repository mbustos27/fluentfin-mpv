using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Outside;
using Microsoft.Extensions.Logging;

namespace FluentFin.Services;

/// <summary>
/// 1.7 keyless: the app's <see cref="OutsideHub"/>. A dev build under the off-screen harness sends every outside source to the
/// fake server (<c>NIXIE_OUTSIDE_BASE</c>, set by tools\ui-harness\offscreen\drive.ps1, which alone makes it a harness run), or
/// to a closed loopback port when the harness keeps the glass active without it, never to a real service; a release package
/// always asks the real hosts (as the update check does).
/// </summary>
public static class OutsideHubFactory
{
	public static OutsideHub Create(KnownFolders folders, ILocalSettingsService settings, ILogger logger)
	{
		// a set NIXIE_OUTSIDE_BASE is a harness run too (drive.ps1 sets it with or without -GlassFollowsActivity)
		var routing = OutsideRouting.FromEnvironment(Environment.GetEnvironmentVariable, AppVersion.IsReleasePackage);
		var cache = OutsideCache.For(folders);
		var hub = new OutsideHub(OutsideHub.CreateHttpClient(), routing, AppVersion.Current, cache, settings);
		hub.Noted += note => logger.LogInformation("{Note}", note);
		if (routing.IsHarness)
		{
			logger.LogInformation("[outside] harness run: every outside source goes to {Origin}", routing.HarnessOrigin);
		}

		_ = Task.Run(() =>
		{
			try
			{
				var purged = cache.Purge();
				if (purged > 0)
				{
					logger.LogInformation("[outside] deleted {Count} cached list files past their sources' maximum age", purged);
				}
			}
			catch (Exception ex)
			{
				logger.LogDebug(ex, "[outside] cache purge failed");
			}
		});
		return hub;
	}
}
