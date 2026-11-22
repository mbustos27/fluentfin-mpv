using Microsoft.UI.Xaml.Controls;

namespace FluentFin;

/// <summary>1.6 skip: the Settings list's icons: a show's line, and the lines of its rules and learned intros under it.</summary>
public static class SkipRuleLook
{
	public static IconElement Icon(bool isShow) => new FontIcon { Glyph = isShow ? "" : "" };
}
