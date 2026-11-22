using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// 1.6 colpage (theme polish): a page header's strip of the lot at dusk (the Library's sky: <see cref="DriveInLayout.Strip"/>),
/// sized to wherever it is put: the Libraries page's stage behind its marquee. Decorative: Narrator and the pointer never see it.
/// </summary>
public sealed partial class SkyStrip : Grid
{
	private readonly DriveInScene _scene = new() { Layer = SceneLayer.Back };

	public SkyStrip()
	{
		IsHitTestVisible = false;
		var canvas = new Canvas();
		canvas.Children.Add(_scene);
		Children.Add(canvas);
		SizeChanged += (_, e) =>
		{
			if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
			{
				_scene.Layout = DriveInLayout.Strip(e.NewSize.Width, e.NewSize.Height);
			}
		};
	}
}
