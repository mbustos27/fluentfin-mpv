using Windows.UI.ViewManagement;

namespace FluentFin.Services;

public enum GlassModeKind
{
	/// <summary>Glass and motion: lift, auto-advance, crossfades.</summary>
	Full,
	/// <summary>Glass, but only fades: Windows' animation effects are off.</summary>
	Reduced,
	/// <summary>No translucency: opaque surfaces of the same colours. Transparency effects off, or high contrast.</summary>
	Solid,
}

/// <summary>
/// Seaglass (docs/design/SEAGLASS.md): how much glass and motion the app shows. Automatic from Windows' settings
/// (Transparency effects, Animation effects, high contrast); Settings can force a mode (<see cref="Override"/>).
/// <see cref="Changed"/> may be raised off the UI thread: marshal to it before touching XAML.
/// </summary>
public static class GlassMode
{
	private static readonly UISettings _ui = new();
	private static readonly AccessibilitySettings _accessibility = new();
	private static GlassModeKind? _override;

	static GlassMode()
	{
		// held in static fields: a collected UISettings stops raising its events. Subscribing can fail where Windows has
		// no such setting to watch (on a private desktop, AccessibilitySettings throws 0x80070490): then the mode is
		// read as it is at each call, and only the live update is lost. A static constructor that throws would take the
		// whole app down.
		try { _ui.AdvancedEffectsEnabledChanged += (_, _) => Changed?.Invoke(null, EventArgs.Empty); }
		catch (Exception) { }
		try { _accessibility.HighContrastChanged += (_, _) => Changed?.Invoke(null, EventArgs.Empty); }
		catch (Exception) { }
	}

	private static bool HighContrast
	{
		get
		{
			try { return _accessibility.HighContrast; }
			catch (Exception) { return false; }
		}
	}

	public static event EventHandler? Changed;

	/// <summary>The mode Settings forces, or null for automatic.</summary>
	public static GlassModeKind? Override
	{
		get => _override;
		set
		{
			if (_override == value)
			{
				return;
			}

			_override = value;
			Changed?.Invoke(null, EventArgs.Empty);
		}
	}

	public static GlassModeKind Automatic =>
		HighContrast || !_ui.AdvancedEffectsEnabled ? GlassModeKind.Solid
		: !_ui.AnimationsEnabled ? GlassModeKind.Reduced
		: GlassModeKind.Full;

	/// <summary>High contrast always wins over <see cref="Override"/>: it needs the system colours.</summary>
	public static GlassModeKind Current => HighContrast ? GlassModeKind.Solid : _override ?? Automatic;

	/// <summary>Motion beyond simple fades (lift, tilt, auto-advance): only in Full, never with Windows' animations off.</summary>
	public static bool MotionAllowed => Current == GlassModeKind.Full && _ui.AnimationsEnabled;
}
