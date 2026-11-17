using FluentFin.Core.Theming;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass step 0: the generated grain has no seams when tiled and no net lightening.</summary>
public sealed class GrainTests
{
	private static double Correlation(Func<int, (double, double)> pairs, int count)
	{
		double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
		for (var i = 0; i < count; i++)
		{
			var (a, b) = pairs(i);
			sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
		}

		var n = (double)count;
		return (sab / n - sa / n * sb / n) / Math.Sqrt((saa / n - sa * sa / n / n) * (sbb / n - sb * sb / n / n));
	}

	[Fact]
	public void The_grain_is_deterministic_zero_mean_and_not_empty()
	{
		var a = Grain.Generate();
		Assert.Equal(a, Grain.Generate());
		Assert.Equal(Grain.Size * Grain.Size * 4, a.Length);
		var mean = Enumerable.Range(0, Grain.Size * Grain.Size).Average(i => Grain.Signed(a, Grain.Size, i % Grain.Size, i / Grain.Size));
		Assert.InRange(mean, -0.02, 0.02);
		Assert.Contains(a, b => b > 200);
	}

	[Fact]
	public void Neighbours_across_the_wrap_edge_correlate_no_more_than_neighbours_inside_the_tile()
	{
		const int s = Grain.Size;
		var p = Grain.Generate();
		var seamX = Correlation(y => (Grain.Signed(p, s, s - 1, y), Grain.Signed(p, s, 0, y)), s);
		var seamY = Correlation(x => (Grain.Signed(p, s, x, s - 1), Grain.Signed(p, s, x, 0)), s);
		var insideX = Correlation(y => (Grain.Signed(p, s, 100, y), Grain.Signed(p, s, 101, y)), s);
		// 256 samples: the noise floor of a correlation is about 3/sqrt(256) = 0.19
		Assert.InRange(seamX, -0.19, 0.19);
		Assert.InRange(seamY, -0.19, 0.19);
		Assert.InRange(insideX, -0.19, 0.19);
	}
}
