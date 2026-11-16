using FluentFin.Mpv;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv player controls: whether the libmpv next to FluentFin can carry FluentFin's controls (Libmpv's probe).
/// A client API 2.x is not enough: an older libmpv that has no composition output loads fine and would leave the controls
/// over no video, so the probe makes a core handle and has it take the composition options, then destroys it.
/// </summary>
[Collection(AloneCollection.Name)]
public sealed class LibmpvProbeTests
{
	private sealed class FakeLibmpv(ulong version = 2UL << 16 | 5)
	{
		public List<string> Calls { get; } = [];

		/// <summary>The options it refuses, with mpv's error code.</summary>
		public Dictionary<string, int> Refuses { get; } = [];

		public bool CreateFails { get; set; }

		public Libmpv.ProbeApi Api => new(
			() => version,
			() =>
			{
				Calls.Add("create");
				return CreateFails ? 0 : 0x1234;
			},
			(ctx, name, value) =>
			{
				Calls.Add($"{name}={value}");
				return Refuses.TryGetValue(name, out var rc) ? rc : 0;
			},
			ctx => Calls.Add($"destroy {ctx:x}"),
			rc => rc == -7 ? "unsupported format for accessing property" : $"error {rc}");
	}

	[Fact]
	public void AUsableLibmpv_TakesTheCompositionOptions_AndItsHandleIsDestroyed()
	{
		var libmpv = new FakeLibmpv();

		Assert.Null(Libmpv.Probe(libmpv.Api));
		Assert.Equal(["create", "vo=gpu-next", "gpu-context=d3d11", "d3d11-output-mode=composition", "destroy 1234"], libmpv.Calls);
	}

	[Fact]
	public void AnOlderLibmpv_WithClientApi2_ButNoCompositionOutput_IsNotUsable()
	{
		var libmpv = new FakeLibmpv(version: 2UL << 16 | 1);
		libmpv.Refuses["d3d11-output-mode"] = -7;

		var why = Libmpv.Probe(libmpv.Api);

		Assert.NotNull(why);
		Assert.Contains("d3d11-output-mode=composition", why);
		Assert.Contains("unsupported format", why);
		Assert.Equal("destroy 1234", libmpv.Calls[^1]);
	}

	[Fact]
	public void AClientApiOtherThan2_IsNotUsable_AndNoCoreIsMade()
	{
		var libmpv = new FakeLibmpv(version: 1UL << 16 | 109);

		Assert.Equal("libmpv-2.dll has client API 1.109, 2.x is needed", Libmpv.Probe(libmpv.Api));
		Assert.Empty(libmpv.Calls);
	}

	[Fact]
	public void ALibmpvThatCannotMakeACore_IsNotUsable()
	{
		var libmpv = new FakeLibmpv { CreateFails = true };

		Assert.Contains("mpv_create failed", Libmpv.Probe(libmpv.Api));
		Assert.Equal(["create"], libmpv.Calls);
	}

	[Fact]
	public void TheRealProbe_OfADllThatIsNotLibmpv_SaysSo()
	{
		var why = Libmpv.ProbeFile(Path.Combine(Environment.SystemDirectory, "version.dll"));

		Assert.Equal("version.dll is not libmpv (it has no mpv_client_api_version)", why);
	}

	[Fact]
	public void TheRealProbe_OfAMissingFile_SaysItCouldNotBeLoaded()
	{
		var why = Libmpv.ProbeFile(Path.Combine(AppContext.BaseDirectory, "no-such-dir", "libmpv-2.dll"));

		Assert.Equal("libmpv-2.dll could not be loaded", why);
	}

	/// <summary>The vendored libmpv (copied next to the tests when the checkout has it, vendor\libmpv\VENDORED.md) passes.</summary>
	[Fact]
	public void TheRealProbe_OfTheVendoredLibmpv_PassesWhenItIsThere()
	{
		var path = Path.Combine(AppContext.BaseDirectory, Libmpv.Dll);
		if (!File.Exists(path))
		{
			return;
		}

		Assert.Null(Libmpv.ProbeFile(path));
		Assert.True(Libmpv.IsAvailable(out var why), why);
	}
}
