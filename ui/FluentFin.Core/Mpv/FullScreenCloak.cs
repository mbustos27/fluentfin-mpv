namespace FluentFin.Core.Mpv;

/// <summary>
/// Seaglass: the window is cloaked for the length of a full-screen switch. The cloak is remembered the moment it is made, and
/// anything that goes wrong afterwards uncloaks it: the window must never be left invisible.
/// </summary>
public static class FullScreenCloak
{
	/// <param name="cloak">Hides the window; true when it did.</param>
	/// <param name="uncloak">Shows it again.</param>
	/// <param name="remember">Records whether the window is cloaked now (the page's <c>_cloakedHwnd</c>).</param>
	/// <param name="body">The rest of the switch.</param>
	public static void Run(Func<bool> cloak, Action uncloak, Action<bool> remember, Action body)
	{
		var cloaked = cloak();
		remember(cloaked);
		try
		{
			body();
		}
		catch
		{
			if (cloaked)
			{
				remember(false);
				uncloak();
			}

			throw;
		}
	}
}
