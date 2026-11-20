using FluentFin.Core;
using Microsoft.Extensions.Logging;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.4.0: the one way a trailer opens the system browser, and only for the user's own click (the message's Watch on
/// YouTube or Open in browser, the no-theatre dialog's, or a link the user clicked inside YouTube's player). Nothing opens it
/// by itself: the Trailer button never does (<see cref="Trailers.Plan"/>). Each opening is logged with its host and why; under
/// the off-screen harness (a dev build) it is logged and not launched, so a test run never opens the user's browser.
/// </summary>
public static class TrailerBrowser
{
	/// <summary>The launcher (tests replace it to see that nothing is opened).</summary>
	internal static Func<Uri, Task<bool>> Launch { get; set; } = async uri => await Windows.System.Launcher.LaunchUriAsync(uri);

	/// <summary>Whether this run is the harness's (a dev build with the harness's variable): the browser is not launched.</summary>
	internal static Func<bool> HarnessOn { get; set; } = () =>
		!AppVersion.IsReleasePackage && Environment.GetEnvironmentVariable(GlassActivity.AlwaysActiveVariable) == "1";

	/// <summary>Opens <paramref name="uri"/> in the browser (a web address only), for <paramref name="why"/> (the user's click).</summary>
	public static async Task<bool> Open(Uri? uri, string why, ILogger? log)
	{
		if (!Trailers.OpensInBrowser(uri))
		{
			log?.LogInformation("[trailer] not a web address: the browser stays closed ({Why})", why);
			return false;
		}

		if (HarnessOn())
		{
			log?.LogInformation("[trailer] the browser would open {Host} ({Why}; the harness: not launched)", uri!.Host, why);
			return false;
		}

		log?.LogInformation("[trailer] the browser opens {Host} ({Why})", uri!.Host, why);
		try
		{
			return await Launch(uri);
		}
		catch (Exception ex)
		{
			log?.LogWarning(ex, "[trailer] the browser could not be opened");
			return false;
		}
	}
}
