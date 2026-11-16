namespace FluentFin.Services;

/// <summary>
/// Seaglass's glass look (docs/design/GLASS.md): the soft, dimmed artwork behind the whole window, which the glass
/// panels frost. A page sets the image of what it shows (Home: the hero item's backdrop; an item page: the item's) and
/// clears it (null) when it has none; the shell draws it and fades between images. UI thread only.
/// </summary>
public static class AmbientBackdrop
{
	public static Uri? Current { get; private set; }

	public static event EventHandler<Uri?>? Changed;

	public static void Set(Uri? image)
	{
		if (image == Current)
		{
			return;
		}

		Current = image;
		Changed?.Invoke(null, image);
	}
}
