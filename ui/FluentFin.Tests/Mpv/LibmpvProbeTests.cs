using System.Runtime.InteropServices;
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
	/// What the loader asks Windows for: the full path, and libmpv's dependencies looked for in its own folder and System32
	/// only (LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32), never FluentFin's folder, the working
	/// directory or PATH, where other builds' DLLs of the same names can be (another FFmpeg, an msys2 bin folder).
	/// </summary>
	[Fact]
	public void TheLoader_AsksForTheFullPath_AndLibmpvsOwnFolderAndSystem32Only()
	{
		using var dir = new TempDir();
		var dll = Path.Combine(dir.Path, "x.dll");
		File.WriteAllBytes(dll, [0]);
		var calls = new List<(string Path, uint Flags)>();

		var (handle, error) = Libmpv.Load(Path.Combine(dir.Path, ".", "x.dll"), (path, flags) =>
		{
			calls.Add((path, flags));
			return 0x5678;
		});

		Assert.Equal(0x5678, handle);
		Assert.Null(error);
		Assert.Equal([(dll, 0x100u | 0x800u)], calls);
		Assert.Equal(Path.Combine(AppContext.BaseDirectory, "libmpv", "libmpv-2.dll"), Libmpv.InstalledPath);
	}

	/// <summary>
	/// The load runs with Windows' critical-error boxes off for the process (a DLL missing an export would otherwise show
	/// "Entry Point Not Found" on the user's screen and block until dismissed; the thread's mode is not enough, the loader
	/// works on threads of its own), and the process's mode is put back after. Starts from mode 0, as a desktop app may.
	/// </summary>
	[Fact]
	public void TheLoader_ShowsNoErrorBox_AndRestoresTheProcessErrorMode()
	{
		using var dir = new TempDir();
		var dll = Path.Combine(dir.Path, "x.dll");
		File.WriteAllBytes(dll, [0]);
		var original = SetErrorMode(0);
		try
		{
			uint during = 0;

			var (_, error) = Libmpv.Load(dll, (_, _) =>
			{
				during = GetErrorMode();
				Marshal.SetLastPInvokeError(127);
				return 0;
			});

			Assert.Equal(0x0001u, during & 0x0001u);
			Assert.Equal(0u, GetErrorMode());
			Assert.Equal(new System.ComponentModel.Win32Exception(127).Message, error);
		}
		finally
		{
			SetErrorMode(original);
		}
	}

	/// <summary>
	/// libmpv is not loaded while an FFmpeg DLL of its folder's names is loaded from elsewhere (Flyleaf's): Windows would
	/// give libmpv that other FFmpeg. It says to restart FluentFin, and nothing is loaded or changed.
	/// </summary>
	[Fact]
	public void Libmpv_IsNotLoaded_WhileAnotherFFmpegOfTheSameNamesIsLoaded()
	{
		using var dir = new TempDir();
		File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(dir.Path, "libmpv-2.dll"));
		File.WriteAllBytes(Path.Combine(dir.Path, "avcodec-63.dll"), [0]);
		File.WriteAllBytes(Path.Combine(dir.Path, "avutil-61.dll"), [0]);
		var flyleaf = @"C:\Some\App\FFMpeg\avutil-61.dll";

		var (handle, error) = Libmpv.LoadLibmpv(Path.Combine(dir.Path, "libmpv-2.dll"), name => name == "avutil-61.dll" ? flyleaf : null);

		Assert.Equal(0, handle);
		Assert.Contains("avutil-61.dll", error);
		Assert.Contains(@"C:\Some\App\FFMpeg", error);
		Assert.Contains("restart Nixie to use its own controls", error);
		Assert.Null(NativeModules.LoadedPath(Path.Combine(dir.Path, "libmpv-2.dll")));
	}

	/// <summary>The clash check itself, on real modules: a name loaded from another folder is reported, one from the folder not.</summary>
	[Fact]
	public void TheClashCheck_ReportsANameLoadedFromAnotherFolder()
	{
		var kernel32 = NativeModules.LoadedPath("kernel32.dll");

		Assert.NotNull(kernel32);
		Assert.Equal([("kernel32.dll", kernel32!)], NativeModules.LoadedElsewhere(["kernel32.dll", "no-such-module-x.dll"], AppContext.BaseDirectory, NativeModules.LoadedPath));
		Assert.Empty(NativeModules.LoadedElsewhere(["kernel32.dll"], Path.GetDirectoryName(kernel32)!, NativeModules.LoadedPath));
	}

	[Fact]
	public void FFmpegsNames_AreFoundInAFolder()
	{
		using var dir = new TempDir();
		foreach (var name in new[] { "avcodec-63.dll", "swscale-10.dll", "libass-9.dll", "avcodec-63.txt", "libavcodec.dll" })
		{
			File.WriteAllBytes(Path.Combine(dir.Path, name), [0]);
		}

		Assert.Equal(["avcodec-63.dll", "swscale-10.dll"], NativeModules.FFmpegNames(dir.Path));
		Assert.Empty(NativeModules.FFmpegNames(Path.Combine(dir.Path, "missing")));
	}

	/// <summary>
	/// The libraries' configuration (GnuTLS's priority file, OpenSSL's config and providers, libxml2's catalog, libva's
	/// drivers) points into libmpv's folder, and p11-kit reads no user configuration.
	/// </summary>
	[Fact]
	public void TheLibraryEnvironment_PointsIntoLibmpvsFolder()
	{
		var folder = @"C:\App\libmpv";
		var env = Libmpv.LibraryEnvironment(folder).ToDictionary(x => x.Name, x => x.Value);

		Assert.Equal(["GNUTLS_SYSTEM_PRIORITY_FILE", "LIBVA_DRIVERS_PATH", "OPENSSL_CONF", "OPENSSL_CONF_INCLUDE", "OPENSSL_MODULES", "P11_KIT_NO_USER_CONFIG", "XML_CATALOG_FILES"], env.Keys.Order());
		Assert.Equal("1", env["P11_KIT_NO_USER_CONFIG"]);
		Assert.All(env.Where(x => x.Key != "P11_KIT_NO_USER_CONFIG"), x => Assert.StartsWith(folder + @"\", x.Value));
	}

	/// <summary>
	/// The environment reaches the C runtime's copy the MSYS2 libraries' getenv reads, even when the runtime made that copy
	/// before (setting Windows' environment alone does not change it), as well as Windows' own.
	/// </summary>
	[Fact]
	public void TheLibraryEnvironment_ReachesTheCRuntimesGetenv_AndWindows()
	{
		using var dir = new TempDir();
		_ = CGetenv("GNUTLS_SYSTEM_PRIORITY_FILE");  // the runtime's copy exists from here on

		Libmpv.ApplyLibraryEnvironment(dir.Path);

		foreach (var (name, value) in Libmpv.LibraryEnvironment(dir.Path))
		{
			Assert.Equal(value, CGetenv(name));
			Assert.Equal(value, Environment.GetEnvironmentVariable(name));
		}
	}

	/// <summary>
	/// The build puts the vendored libmpv and its DLLs in the libmpv folder next to the app (here: the tests), when the
	/// checkout has a build in vendor\libmpv (VENDORED.md).
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
	[VendoredLibmpvFact]
	public void TheRealProbe_OfTheVendoredLibmpv_Passes()
	{
		Assert.Null(Libmpv.ProbeFile(Libmpv.InstalledPath));
		Assert.True(Libmpv.IsAvailable(out var why), why);
	}

	/// <summary>
	/// Loading the vendored libmpv sets its libraries' environment first (they read it as they load) and points GnuTLS's
	/// translations into its folder (MSYS2's GnuTLS binds them to its build machine's D:/M/msys64/... when it loads).
	/// </summary>
	[VendoredLibmpvFact]
	public void LoadingTheVendoredLibmpv_KeepsItsLibrariesConfigurationInItsFolder()
	{
		var folder = Path.GetDirectoryName(Libmpv.InstalledPath)!;
		using (var other = new TempDir())
		{
			Libmpv.ApplyLibraryEnvironment(other.Path);
		}

		var (handle, error) = Libmpv.LoadLibmpv(Libmpv.InstalledPath);

		Assert.True(handle != 0, error);
		foreach (var (name, value) in Libmpv.LibraryEnvironment(folder))
		{
			Assert.Equal(value, CGetenv(name));
		}

		Assert.StartsWith(folder + @"\", Libmpv.GnutlsTranslations());
	}

	/// <summary>
	/// Once the probe has loaded it, libmpv-2.dll and every DLL of its folder that is loaded came from that folder: none was
	/// taken from FluentFin's folder, PATH or anywhere else with the same name.
	/// </summary>
	[VendoredLibmpvFact]
	public void TheVendoredLibmpv_AndItsDlls_AreLoadedFromItsFolder()
	{
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
	[VendoredLibmpvFact]
	public void TheVendoredLibmpv_ImportsOnlyItsOwnOrWindowsDlls_SoNoVulkanDriverIsNeeded()
	{
		var folder = Path.GetDirectoryName(Libmpv.InstalledPath)!;
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

	/// <summary>
	/// The folder's libdvdcss-2.dll is FluentFin MPV's stub (tools\release\libmpv\dvdcss-stub), not libdvdcss: it exports
	/// only the seven functions libdvdread imports, and opening anything through it fails (no DVD decryption is shipped).
	/// </summary>
	[VendoredLibmpvFact]
	public void TheVendoredLibdvdcss_IsTheStub_ThatOpensNothing()
	{
		Assert.True(Libmpv.IsAvailable(out var why), why);
		var stub = Path.Combine(Path.GetDirectoryName(Libmpv.InstalledPath)!, "libdvdcss-2.dll");

		Assert.Equal(stub, NativeModules.LoadedPath("libdvdcss-2.dll"), StringComparer.OrdinalIgnoreCase);
		Assert.Equal(["dvdcpxm_init", "dvdcpxm_read", "dvdcss_close", "dvdcss_open", "dvdcss_open_stream", "dvdcss_read", "dvdcss_seek"], PeImports.Exports(stub).Order());
		var open = Marshal.GetDelegateForFunctionPointer<DvdcssOpen>(NativeLibrary.GetExport(NativeLibrary.Load(stub), "dvdcss_open"));
		Assert.Equal(0, open(@"D:\"));
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate nint DvdcssOpen([MarshalAs(UnmanagedType.LPStr)] string target);

	/// <summary>
	/// A partial folder: a DLL whose dependency is missing fails to load with Windows' reason, through the same loader
	/// (a copy of one of the folder's DLLs, its import renamed to a DLL no process has).
	/// </summary>
	[VendoredLibmpvFact]
	public void ADllWhoseDependencyIsMissing_IsNotLoaded_AndTheProbeSaysWhy()
	{
		using var dir = new TempDir();
		var dll = Patched(dir.Path, "libbrotlicommon.dll", "libbrotlicommoX.dll");

		Assert.Equal($"{Path.GetFileName(dll)} could not be loaded: {new System.ComponentModel.Win32Exception(126).Message}", Libmpv.ProbeFile(dll));
	}

	/// <summary>
	/// A DLL that lacks a function another one imports (error 127) fails to load with Windows' reason and no "Entry Point
	/// Not Found" box (the test would hang on it).
	/// </summary>
	[VendoredLibmpvFact]
	public void ADllMissingAnImportedFunction_IsNotLoaded_WithoutAnErrorBox()
	{
		using var dir = new TempDir();
		File.Copy(Path.Combine(Path.GetDirectoryName(Libmpv.InstalledPath)!, "libbrotlicommon.dll"), Path.Combine(dir.Path, "libbrotlicommon.dll"));
		var dll = Patched(dir.Path, "BrotliDefaultAllocFunc", "BrotliDefaultAllocFunX");

		Assert.Equal($"{Path.GetFileName(dll)} could not be loaded: {new System.ComponentModel.Win32Exception(127).Message}", Libmpv.ProbeFile(dll));
	}

	/// <summary>A copy of the vendored libbrotlidec.dll, under a name of its own, with one import string changed.</summary>
	private static string Patched(string dir, string from, string to)
	{
		var bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Libmpv.InstalledPath)!, "libbrotlidec.dll"));
		var at = bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(from + "\0"));
		Assert.True(at >= 0, $"{from} is not in libbrotlidec.dll");
		System.Text.Encoding.ASCII.GetBytes(to).CopyTo(bytes, at);
		var path = Path.Combine(dir, $"fixture-{Guid.NewGuid():N}.dll");
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[DllImport("kernel32.dll")]
	private static extern uint GetErrorMode();

	[DllImport("kernel32.dll")]
	private static extern uint SetErrorMode(uint mode);

	/// <summary>The C runtime's (ucrtbase's) getenv, as the MSYS2 UCRT64 libraries call it.</summary>
	private static string? CGetenv(string name)
	{
		var p = UcrtGetenv(name);
		return p == 0 ? null : Marshal.PtrToStringAnsi(p);
	}

	[DllImport("ucrtbase.dll", EntryPoint = "getenv", CharSet = CharSet.Ansi, BestFitMapping = false)]
	private static extern nint UcrtGetenv(string name);

	private sealed class TempDir : IDisposable
	{
		public string Path { get; } = Directory.CreateTempSubdirectory("ffmpv-libmpv-").FullName;

		public void Dispose()
		{
			try
			{
				Directory.Delete(Path, recursive: true);
			}
			catch (IOException)
			{
				// a DLL a test loaded stays mapped until the process ends
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
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

	/// <summary>The names a DLL exports (its export directory's name table).</summary>
	public static List<string> Exports(string path)
	{
		using var stream = File.OpenRead(path);
		using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
		var directory = (pe.PEHeaders.PEHeader ?? throw new InvalidDataException($"{path} has no PE header")).ExportTableDirectory;
		var names = new List<string>();
		if (directory.Size == 0)
		{
			return names;
		}

		// IMAGE_EXPORT_DIRECTORY: ... NumberOfNames at 24, AddressOfNames at 32
		var reader = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
		reader.Offset = 24;
		var count = reader.ReadInt32();
		reader.Offset = 32;
		var table = pe.GetSectionData(reader.ReadInt32()).GetReader();
		for (var i = 0; i < count; i++)
		{
			names.Add(ReadString(pe, table.ReadInt32()));
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

/// <summary>
/// A test of the vendored libmpv (vendor\libmpv\VENDORED.md, copied into the libmpv folder next to the tests): skipped,
/// visibly, when the checkout has none; with FLUENTFIN_MPV_REQUIRE_LIBMPV=1 or CI set it runs anyway, and fails.
/// </summary>
public sealed class VendoredLibmpvFactAttribute : FactAttribute
{
	public VendoredLibmpvFactAttribute()
	{
		var required = Environment.GetEnvironmentVariable("FLUENTFIN_MPV_REQUIRE_LIBMPV") == "1" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
		if (!required && !File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv", "libmpv-2.dll")))
		{
			Skip = @"no vendored libmpv in libmpv\ next to the tests (vendor\libmpv\VENDORED.md); FLUENTFIN_MPV_REQUIRE_LIBMPV=1 makes this a failure";
		}
	}
}