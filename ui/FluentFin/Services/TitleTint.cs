using FluentFin.Core.Theming;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: the hue of the title being shown (its backdrop's BlurHash, or, with none, the artwork sampled to a few pixels). It is
/// used for exactly one thing: a faint tint (at most <see cref="MaxOpacity"/>) over the ambient backdrop. Never a panel or a button.
/// UI thread only.
/// </summary>
public static class TitleTint
{
	/// <summary>The most the title's colour covers the backdrop.</summary>
	public const double MaxOpacity = 0.10;

	public static double Hue { get; private set; } = NixiePalette.DefaultHue;

	public static event EventHandler? Changed;

	private static Uri? _sampledUri;

	public static void Attach() => AmbientBackdrop.HashChanged += (_, _) => Follow();

	private static void Follow()
	{
		var hash = AmbientBackdrop.BlurHash;
		var uri = AmbientBackdrop.Current;
		if (hash is not null)
		{
			Set(NixiePalette.HueFromBlurHash(hash));
		}
		else if (uri is not null && _sampledUri != uri)
		{
			// no BlurHash: the default hue now, the thumbnail's meanwhile
			_sampledUri = uri;
			Set(NixiePalette.DefaultHue);
			_ = SampleAsync(uri);
		}
		else if (uri is null)
		{
			Set(NixiePalette.DefaultHue);
		}
	}

	private static async Task SampleAsync(Uri uri)
	{
		var cells = await ArtworkSampler.SampleAsync(uri);
		if (cells is not null && AmbientBackdrop.Current == uri && AmbientBackdrop.BlurHash is null)
		{
			Set(NixiePalette.HueFromCells(cells));
		}
	}

	private static void Set(double hue)
	{
		if (Math.Abs(hue - Hue) < 0.5)
		{
			return;
		}

		Hue = hue;
		Changed?.Invoke(null, EventArgs.Empty);
	}
}
