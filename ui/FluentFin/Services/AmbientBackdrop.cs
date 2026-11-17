namespace FluentFin.Services;

/// <summary>
/// The glass look (docs/design/SEAGLASS.md): the soft, dimmed artwork behind the whole window, which the glass panels frost
/// (with none, the dusk scene: AmbientBackdropView). A page sets the image of what it shows (Home: the hero item's backdrop; an item page: the item's) and
/// clears it (null) when it has none; the shell draws it and fades between images. UI thread only.
/// </summary>
public static class AmbientBackdrop
{
	public static Uri? Current { get; private set; }

	/// <summary>The BlurHash of the artwork now behind the window (null: none known): the title's palette comes from it (<see cref="SeaglassTheme"/>).</summary>
	public static string? BlurHash { get; private set; }

	public static event EventHandler<Uri?>? Changed;

	public static void Set(Uri? image, string? blurHash = null)
	{
		if (image == Current && blurHash == BlurHash)
		{
			return;
		}

		var sameImage = image == Current;
		BlurHash = blurHash;
		Current = image;
		if (sameImage)
		{
			// only the palette's source changed: the image stays, the palette follows
			HashChanged?.Invoke(null, EventArgs.Empty);
			return;
		}

		Changed?.Invoke(null, image);
		HashChanged?.Invoke(null, EventArgs.Empty);
	}

	/// <summary>The artwork's BlurHash changed (with a new image, or alone).</summary>
	public static event EventHandler? HashChanged;

	private static int _fullScene;

	/// <summary>
	/// 1.1.4: whether a page asks for the whole dusk scene behind it although it is inside the shell (an empty state: an empty
	/// library, no results, the server offline). Sign-in and the server page get it without asking. Each request is undone once.
	/// </summary>
	public static bool FullSceneRequested => _fullScene > 0;

	public static event EventHandler? FullSceneChanged;

	/// <summary>Ask for (true) or give back (false) the whole dusk scene. Pair every true with one false (an empty state that goes away).</summary>
	public static void RequestFullScene(bool on)
	{
		var before = FullSceneRequested;
		_fullScene = Math.Max(0, _fullScene + (on ? 1 : -1));
		if (before != FullSceneRequested)
		{
			FullSceneChanged?.Invoke(null, EventArgs.Empty);
		}
	}
}
