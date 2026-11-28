using CommunityToolkit.WinUI;
using DevWinUI;

namespace FluentFin.Controls;

public partial class LoadingImage : ImageEx
{
	[GeneratedDependencyProperty(DefaultValue = "")]
	public partial string Glyph { get; set; }

	public LoadingImage()
	{
		DefaultStyleKey = typeof(LoadingImage);
		Services.HarnessFrames.Track(this); // Nixie 1.9 (the scroll stream): counted by the harness's frame clock (dev builds only)
	}
}
