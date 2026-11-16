using FluentFin.Services;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass home: GlassMode, but never an exception. GlassMode's static constructor subscribes to Windows' settings events,
/// which can fail (it did on the harness's private desktop: COMException 0x80070490); then every later use throws
/// TypeInitializationException. A card or the hero must still draw, only without motion.
/// </summary>
internal static class SafeMotion
{
	public static bool Allowed
	{
		get
		{
			try
			{
				return GlassMode.MotionAllowed;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}

	public static bool Subscribe(EventHandler handler)
	{
		try
		{
			GlassMode.Changed += handler;
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static void Unsubscribe(EventHandler handler)
	{
		try
		{
			GlassMode.Changed -= handler;
		}
		catch (Exception)
		{
		}
	}
}
