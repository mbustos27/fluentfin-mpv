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

		Assert.Equal("libmpv-2.dll could not be loaded: the file is not there", why);
	}

	/// <summary>
	/// The loader looks for libmpv's DLLs in libmpv's own folder and System32 only: not FluentFin's folder, the working
	/// directory or PATH, where other builds' DLLs of the same names can be (another FFmpeg, an msys2 bin folder).
	/// </summary>
	[Fact]
	public void TheLoader_SearchesLibmpvsOwnFolderAndSystem32_AndNothingElse()
	{
		const uint LoadLibrarySearchDllLoadDir = 0x100, LoadLibrarySearchSystem32 = 0x800;

		Assert.Equal(LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32, Libmpv.LoadFlags);
		Assert.Equal(Path.Combine(AppContext.BaseDirectory, "libmpv", "libmpv-2.dll"), Libmpv.InstalledPath);
	}

	/// <summary>
	/// The build puts the vendored libmpv and its DLLs in the libmpv folder next to the app (here: the tests), when the
	/// checkout has a build in vendor\libmpv (VENDORED.md); otherwise the tests below have nothing to check.
	/// </summary>
	[Fact]
	public void TheBuild_CopiesTheVendoredLibmpvIntoItsFolder_WhenTheCheckoutHasOne()
	{
		var vendor = VendorLibmpvDir();
		if (vendor is null || !Directory.EnumerateFiles(vendor, Libmpv.Dll, SearchOption.AllDirectories).Any())
		{
			return;
		}

		Assert.True(File.Exists(Libmpv.InstalledPath), $"{Libmpv.InstalledPath} is missing although {vendor} has a build");
		Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, Libmpv.Dll)), "libmpv-2.dll belongs in the libmpv folder, not next to the app");
	}

	/// <summary>The vendored libmpv (vendor\libmpv\VENDORED.md), in the libmpv folder next to the tests, passes.</summary>
	[Fact]
	public void TheRealProbe_OfTheVendoredLibmpv_PassesWhenItIsThere()
	{
		if (!File.Exists(Libmpv.InstalledPath))
		{
			return;
		}

		Assert.Null(Libmpv.ProbeFile(Libmpv.InstalledPath));
		Assert.True(Libmpv.IsAvailable(out var why), why);
	}

	/// <summary>
	/// Once the probe has loaded it, libmpv-2.dll and every DLL of its folder that is loaded came from that folder: none was
	/// taken from FluentFin's folder, PATH or anywhere else with the same name.
	/// </summary>
	[Fact]
	public void TheVendoredLibmpv_AndItsDlls_AreLoadedFromItsFolder()
	{
		if (!File.Exists(Libmpv.InstalledPath))
		{
			return;
		}

		Assert.True(Libmpv.IsAvailable(out var why), why);

		var folder = Path.GetDirectoryName(Libmpv.InstalledPath)!;
		var ours = Directory.GetFiles(folder, "*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var loaded = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
			.Where(m => ours.Contains(Path.GetFileName(m.FileName)))
			.ToList();

		Assert.Contains(loaded, m => string.Equals(m.FileName, Libmpv.InstalledPath, StringComparison.OrdinalIgnoreCase));
		Assert.All(loaded, m => Assert.Equal(folder, Path.GetDirectoryName(m.FileName), StringComparer.OrdinalIgnoreCase));
		Assert.True(loaded.Count > 1, "libmpv's own DLLs should be loaded with it");
	}

	/// <summary>
	/// Every DLL any DLL of the vendored libmpv imports (directly or delay-loaded) is either in the folder or Windows' own:
	/// vulkan-1.dll (MSYS2 libplacebo and mpv import it) and any other DLL only a GPU driver installs must be in the
	/// folder, so libmpv loads on a PC without a Vulkan driver.
	/// </summary>
	[Fact]
	public void TheVendoredLibmpv_ImportsOnlyItsOwnOrWindowsDlls_SoNoVulkanDriverIsNeeded()
	{
		var folder = Path.GetDirectoryName(Libmpv.InstalledPath)!;
		if (!File.Exists(Libmpv.InstalledPath))
		{
			return;
		}

		var ours = Directory.GetFiles(folder, "*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var problems = new List<string>();
		foreach (var dll in Directory.GetFiles(folder, "*.dll"))
		{
			foreach (var import in PeImports.Read(dll))
			{
				if (!ours.Contains(import) && !PeImports.IsWindowsDll(import))
				{
					problems.Add($"{Path.GetFileName(dll)} imports {import}, which is neither in the folder nor Windows' own");
				}
			}
		}

		Assert.Empty(problems);
	}

	/// <summary>The import reader itself, on a Windows DLL whose imports are known.</summary>
	[Fact]
	public void TheImportReader_ReadsADllsImports()
	{
		var imports = PeImports.Read(Path.Combine(Environment.SystemDirectory, "dxgi.dll"));

		Assert.NotEmpty(imports);
		Assert.All(imports, x => Assert.EndsWith(".dll", x, StringComparison.OrdinalIgnoreCase));
		Assert.True(PeImports.IsWindowsDll("d3d11.dll"));
		Assert.True(PeImports.IsWindowsDll("api-ms-win-crt-runtime-l1-1-0.dll"));
		Assert.False(PeImports.IsWindowsDll("vulkan-1.dll"));
		Assert.False(PeImports.IsWindowsDll("libass-9.dll"));
	}

	/// <summary>The checkout's vendor\libmpv folder, found above the tests' folder; null when there is none.</summary>
	private static string? VendorLibmpvDir()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine(dir.FullName, "vendor", "libmpv");
			if (File.Exists(Path.Combine(candidate, "VENDORED.md")))
			{
				return candidate;
			}
		}

		return null;
	}
}

/// <summary>A DLL's imported DLL names (import and delay-import tables), read with System.Reflection.PortableExecutable.</summary>
internal static class PeImports
{
	/// <summary>DLLs only a GPU driver puts into System32: present on this PC is no proof that every PC has them.</summary>
	private static readonly HashSet<string> s_driverDlls = new(StringComparer.OrdinalIgnoreCase)
	{
		"vulkan-1.dll", "OpenCL.dll", "nvcuda.dll", "nvapi64.dll", "nvEncodeAPI64.dll", "nvcuvid.dll", "amfrt64.dll", "amdihk64.dll",
		"libva.dll", "libva_win32.dll", "libmfxhw64.dll", "libvpl.dll", "intel_gfx_api-x64.dll",
	};

	public static List<string> Read(string path)
	{
		using var stream = File.OpenRead(path);
		using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
		var header = pe.PEHeaders.PEHeader ?? throw new InvalidDataException($"{path} has no PE header");
		var names = new List<string>();

		if (header.ImportTableDirectory.Size > 0)
		{
			// IMAGE_IMPORT_DESCRIPTOR: OriginalFirstThunk, TimeDateStamp, ForwarderChain, Name, FirstThunk; all zero at the end
			var reader = pe.GetSectionData(header.ImportTableDirectory.RelativeVirtualAddress).GetReader();
			while (true)
			{
				int lookup = reader.ReadInt32(), _ = reader.ReadInt32(), __ = reader.ReadInt32(), name = reader.ReadInt32(), first = reader.ReadInt32();
				if (lookup == 0 && name == 0 && first == 0)
				{
					break;
				}

				names.Add(ReadString(pe, name));
			}
		}

		if (header.DelayImportTableDirectory.Size > 0)
		{
			// IMAGE_DELAYLOAD_DESCRIPTOR: Attributes, DllNameRVA, then six more 32-bit fields; all zero at the end
			var reader = pe.GetSectionData(header.DelayImportTableDirectory.RelativeVirtualAddress).GetReader();
			while (true)
			{
				_ = reader.ReadInt32();
				var name = reader.ReadInt32();
				reader.Offset += 24;
				if (name == 0)
				{
					break;
				}

				names.Add(ReadString(pe, name));
			}
		}

		return names;
	}

	/// <summary>An API set, or a DLL in System32 that is Windows' own (not one a GPU driver installs there).</summary>
	public static bool IsWindowsDll(string name) =>
		name.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase)
		|| (!s_driverDlls.Contains(name) && File.Exists(Path.Combine(Environment.SystemDirectory, name)));

	private static string ReadString(System.Reflection.PortableExecutable.PEReader pe, int rva)
	{
		var reader = pe.GetSectionData(rva).GetReader();
		var text = new System.Text.StringBuilder();
		for (var b = reader.ReadByte(); b != 0; b = reader.ReadByte())
		{
			text.Append((char)b);
		}

		return text.ToString();
	}
}
