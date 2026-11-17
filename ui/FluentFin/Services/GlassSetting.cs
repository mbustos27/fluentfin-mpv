using FluentFin.Core.Settings;

namespace FluentFin.Services;

/// <summary>Seaglass: Settings > Appearance > Glass reaches <see cref="GlassMode"/> here, at start-up and on every change.</summary>
public static class GlassSetting
{
	/// <summary>The mode a choice forces; Automatic (and anything unknown) forces none.</summary>
	public static GlassModeKind? ToOverride(GlassChoice choice) => choice switch
	{
		GlassChoice.Full => GlassModeKind.Full,
		GlassChoice.Reduced => GlassModeKind.Reduced,
		GlassChoice.Solid => GlassModeKind.Solid,
		_ => null,
	};

	public static void Apply(GlassChoice choice) => GlassMode.Override = ToOverride(choice);

	public static void Apply(ISettings settings) => Apply(settings.Glass);
}
