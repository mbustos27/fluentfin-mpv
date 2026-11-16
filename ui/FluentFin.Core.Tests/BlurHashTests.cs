using FluentFin.Core.Imaging;

namespace FluentFin.Core.Tests;

public class BlurHashTests
{
	// a 1x1 hash is its average colour: "00" + four base-83 digits of 0xRRGGBB (worked out by hand)
	[Theory]
	[InlineData("000000", 0, 0, 0)]
	[InlineData("00TSUA", 255, 255, 255)]
	[InlineData("00TI:j", 255, 0, 0)]
	public void AOneComponentHashIsItsColour(string hash, int r, int g, int b)
	{
		var pixels = BlurHash.Decode(hash, 3, 2)!;

		Assert.Equal(3 * 2 * 4, pixels.Length);
		for (var p = 0; p < 6; p++)
		{
			Assert.InRange(pixels[p * 4 + 2], Math.Max(0, r - 1), Math.Min(255, r + 1));
			Assert.InRange(pixels[p * 4 + 1], Math.Max(0, g - 1), Math.Min(255, g + 1));
			Assert.InRange(pixels[p * 4], Math.Max(0, b - 1), Math.Min(255, b + 1));
			Assert.Equal(255, pixels[p * 4 + 3]);
		}
	}

	// the sample hash of the BlurHash site: 4x3 components, a dark top and a lighter bottom
	private const string Sample = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";

	[Fact]
	public void TheSampleHashHasFourByThreeComponents()
	{
		Assert.Equal((4, 3), BlurHash.Components(Sample));
	}

	[Fact]
	public void TheSampleDecodesToItsAverageColourAndVaries()
	{
		var pixels = BlurHash.Decode(Sample, 32, 32)!;

		// DC "HV6n": the average colour is the first component, which the picture's mean follows closely
		double r = 0, g = 0, b = 0;
		for (var p = 0; p < 32 * 32; p++)
		{
			r += pixels[p * 4 + 2];
			g += pixels[p * 4 + 1];
			b += pixels[p * 4];
		}
		var dc = DcColour(Sample);
		Assert.InRange(r / 1024, dc.R - 12, dc.R + 12);
		Assert.InRange(g / 1024, dc.G - 12, dc.G + 12);
		Assert.InRange(b / 1024, dc.B - 12, dc.B + 12);
		Assert.NotEqual(pixels[2], pixels[(31 * 32 + 31) * 4 + 2]); // corners differ: it is not flat
	}

	[Fact]
	public void TheSameHashDecodesToTheSamePicture()
	{
		Assert.Equal(BlurHash.Decode(Sample, 20, 12), BlurHash.Decode(Sample, 20, 12));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("0000")]
	[InlineData("LEHV6nWB2yk8pyo0adR*.7kCMdn")] // one character short
	[InlineData("LEHV6nWB2yk8pyo0adR*.7kCMdnjj")] // one too many
	[InlineData("00éééé")] // not in the alphabet
	[InlineData("00 SUA")]
	public void AnInvalidHashDecodesToNothing(string? hash)
	{
		Assert.Null(BlurHash.Decode(hash, 4, 4));
		Assert.Null(BlurHash.Components(hash));
	}

	[Theory]
	[InlineData(0, 4)]
	[InlineData(4, 0)]
	public void AnEmptyPictureIsNothing(int w, int h) => Assert.Null(BlurHash.Decode("000000", w, h));

	private static (double R, double G, double B) DcColour(string hash)
	{
		const string a = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";
		var v = 0;
		foreach (var c in hash.Substring(2, 4))
		{
			v = v * 83 + a.IndexOf(c);
		}
		static double Lin(int s) { var x = s / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
		static double Srgb(double l) => (l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055) * 255;
		return (Srgb(Lin(v >> 16)), Srgb(Lin((v >> 8) & 255)), Srgb(Lin(v & 255)));
	}
}

public class ImageSizesTests
{
	[Theory]
	[InlineData(Jellyfin.Sdk.Generated.Models.ImageType.Primary, 302, 202)]
	[InlineData(Jellyfin.Sdk.Generated.Models.ImageType.Thumb, 192, 342)]
	[InlineData(Jellyfin.Sdk.Generated.Models.ImageType.Backdrop, 1080, 1920)]
	[InlineData(Jellyfin.Sdk.Generated.Models.ImageType.Logo, 100, 0)]
	[InlineData(Jellyfin.Sdk.Generated.Models.ImageType.Primary, 0, 0)]
	public void DecodeWidthFollowsTheShapeOfTheArtwork(Jellyfin.Sdk.Generated.Models.ImageType type, double height, int width) =>
		Assert.Equal(width, FluentFin.Core.Imaging.ImageSizes.DecodeWidth(type, height));
}
