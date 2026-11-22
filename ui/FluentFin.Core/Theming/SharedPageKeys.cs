namespace FluentFin.Core.Theming;

/// <summary>
/// 1.6 colpage: the optional keys a theme dictionary gives to dress the shared pages (the Collection page, the Libraries page,
/// a library's presets strip) without a view of its own. Each is asked for by name and has a fallback (today's look) when a
/// theme gives none, so they are contract keys like PageHeaderStyle, not one theme's own (no theme prefix).
/// </summary>
public static class SharedPageKeys
{
	public static IReadOnlyList<string> All { get; } =
	[
		// the Collection page (CollectionPage.xaml.cs): the header stage, its height, under the bar, Classic's soft backdrop, the standfirst
		"CollectionStageTemplate", "CollectionStageHeight", "CollectionStageUnderBar", "CollectionAmbientBackdrop", "CollectionStandfirstStyle",
		// the Collection and Libraries pages' title frame (Drive-in's marquee)
		"PageTitleFrameStyle", "PageTitleFrameForeground",
		// the Libraries page (LibrariesLandingPage.xaml.cs), and the group heading the collections stream's group takes
		"LibrariesStageTemplate", "LibrariesStageUnderBar", "LibrariesGroupHeadingStyle",
		// a library's presets strip (Controls\LibraryPresetStrip)
		"LibraryPresetChipStyle", "LibraryPresetUppercase",
	];
}
