namespace FluentFin.Services;

/// <summary>
/// The glass look (docs/design/DESIGN.md): the soft, dimmed artwork behind the whole window, which the glass panels frost
/// (with none, the dusk scene: AmbientBackdropView). A page sets the image of what it shows (Home: the hero item's backdrop; an item page: the item's) and
/// clears it (null) when it has none; the shell draws it and fades between images. UI thread only.
/// </summary>
public static class AmbientBackdrop
{
	public static Uri? Current { get; private set; }

	/// <summary>The BlurHash of the artwork now behind the window (null: none known): the title's palette comes from it (<see cref="NixieTheme"/>).</summary>
	public static string? BlurHash { get; private set; }

	public static event EventHandler<Uri?>? Changed;

	public static void Set(Uri? image, string? blurHash = null)
	{
		// 1.7.4 integration (1.7.3's close crash class): nothing changes the backdrop once the app is closing. A page's Unloaded
		// in the window's teardown (Home's hero, an item page's hero or stage) cleared it, the window read its SystemBackdrop
		// there (MainWindow.UpdateBackdrop) and the app ended in an access violation (0xc0000005) at the close
		if (App.IsClosing)
		{
			return;
		}

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

	// who holds the full scene now: one entry an owner, however often it asks (a count would stay up after a missed release)
	private static readonly HashSet<object> _fullSceneOwners = new(ReferenceEqualityComparer.Instance);

	/// <summary>
	/// 1.2.0: whether something inside the shell holds the whole dusk scene behind the page (an empty state's
	/// <see cref="Controls.SceneCard"/>, which puts its text on glass). Sign-in and the server page get the scene without asking.
	/// </summary>
	public static bool FullSceneRequested => _fullSceneOwners.Count > 0;

	public static event EventHandler? FullSceneChanged;

	/// <summary>
	/// <paramref name="owner"/> holds the full scene (again: no effect) until <see cref="ReleaseFullScene"/>. Use
	/// <see cref="Controls.SceneCard"/>, which holds it while it is loaded and gives its text a panel; text straight on the
	/// scene's sky bands is not readable (3:1 in places).
	/// </summary>
	internal static void RequestFullScene(object owner) => Change(() => _fullSceneOwners.Add(owner));

	/// <summary><paramref name="owner"/> gives the full scene back (one that holds nothing: no effect).</summary>
	internal static void ReleaseFullScene(object owner) => Change(() => _fullSceneOwners.Remove(owner));

	private static void Change(Func<bool> change)
	{
		var before = FullSceneRequested;
		change();
		if (before != FullSceneRequested)
		{
			FullSceneChanged?.Invoke(null, EventArgs.Empty);
		}
	}
}
