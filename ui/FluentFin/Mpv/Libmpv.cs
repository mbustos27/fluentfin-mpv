using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluentFin.Mpv;

/// <summary>
/// fluentfin-mpv player controls: the few libmpv calls (client.h, API 2.x) the in-process core needs, against the
/// <c>libmpv-2.dll</c> in the <see cref="Folder"/> folder next to FluentFin.exe, with the DLLs it loads beside it
/// (vendor\libmpv\VENDORED.md). Only loaded when "Player controls: FluentFin" is chosen; <see cref="IsAvailable"/> says
/// whether it can be (and does composition output), without throwing.
/// </summary>
internal static unsafe partial class Libmpv
{
	public const string Dll = "libmpv-2.dll";

	/// <summary>The folder next to FluentFin.exe that holds <see cref="Dll"/> and every non-Windows DLL it loads.</summary>
	public const string Folder = "libmpv";

	/// <summary>Where FluentFin's libmpv is: <see cref="Dll"/> in <see cref="Folder"/> next to FluentFin.</summary>
	public static string InstalledPath => Path.Combine(AppContext.BaseDirectory, Folder, Dll);

	/// <summary>
	/// <c>LoadLibraryEx</c> flags for libmpv: its dependencies are looked for in its own folder, then in System32, and
	/// nowhere else (not FluentFin's folder, the working directory or PATH, where another build's DLLs of the same names
	/// could be). <c>LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32</c>.
	/// </summary>
	internal const uint LoadFlags = 0x00000100 | 0x00000800;

	private static readonly Lazy<(nint Handle, string? Error)> s_installed = new(() => LoadLibmpv(InstalledPath));

	/// <summary>Why the imports could not be bound to the installed library (another resolver was set first), else null.</summary>
	private static readonly string? s_resolverError;

	static Libmpv()
	{
		// the imports below bind to the library the probe loaded from FluentFin's libmpv folder, never to one found by name
		try
		{
			NativeLibrary.SetDllImportResolver(typeof(Libmpv).Assembly, (name, _, _) => name == Dll ? Installed() : 0);
		}
		catch (InvalidOperationException ex)
		{
			s_resolverError = ex.Message;
		}
	}

	/// <summary>The installed libmpv's handle for the imports; throws when it cannot be loaded (no other is looked for).</summary>
	private static nint Installed()
	{
		var (handle, error) = s_installed.Value;
		return handle != 0 ? handle : throw new DllNotFoundException($"{Dll} could not be loaded from {Path.Combine(AppContext.BaseDirectory, Folder)}: {error}");
	}

	/// <summary>The loader call (tests: one that records what it is given). Sets the thread's last error on failure.</summary>
	internal delegate nint LoadLibraryCall(string fullPath, uint flags);

	/// <summary>
	/// Loads the DLL at <paramref name="path"/> by its full path with <see cref="LoadFlags"/>: (handle, null), or (0, why not).
	/// Windows' "Entry Point Not Found" / "System Error" boxes are off for the call (the process's error mode, put back after):
	/// a DLL that lacks an export fails here with its message instead of a dialog on the user's screen.
	/// </summary>
	internal static (nint Handle, string? Error) Load(string path, LoadLibraryCall? loader = null)
	{
		if (!File.Exists(path))
		{
			return (0, "the file is not there");
		}

		loader ??= static (fullPath, flags) => LoadLibraryExW(fullPath, 0, flags);
		// the process's mode, not the thread's: Windows resolves a DLL's imports on loader threads of its own, which the
		// calling thread's mode does not reach (a missing export still showed its box with SetThreadErrorMode)
		const uint SemFailCriticalErrors = 0x0001;
		var previous = GetErrorMode();
		SetErrorMode(previous | SemFailCriticalErrors);
		try
		{
			var handle = loader(Path.GetFullPath(path), LoadFlags);
			return handle != 0 ? (handle, null) : (0, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
		}
		finally
		{
			SetErrorMode(previous);
		}
	}

	/// <summary>
	/// Loads FluentFin's libmpv at <paramref name="path"/>: the libraries' environment first (<see cref="LibraryEnvironment"/>,
	/// they read it when they load), refused when one of its FFmpeg DLLs' names is already loaded from elsewhere (Windows would
	/// hand libmpv that other FFmpeg), then <see cref="Load"/>, then GnuTLS's translations pointed into the folder.
	/// </summary>
	internal static (nint Handle, string? Error) LoadLibmpv(string path, Func<string, string?>? loadedPath = null)
	{
		if (!File.Exists(path))
		{
			return (0, "the file is not there");
		}

		var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
		var elsewhere = NativeModules.LoadedElsewhere(NativeModules.FFmpegNames(folder), folder, loadedPath ?? NativeModules.LoadedPath);
		if (elsewhere.Count > 0)
		{
			var (name, from) = elsewhere[0];
			return (0, $"another FFmpeg's {name} is already loaded in Seaglass (from {Path.GetDirectoryName(from)}): restart Seaglass to use its own controls");
		}

		ApplyLibraryEnvironment(folder);
		var loaded = Load(path);
		if (loaded.Handle != 0)
		{
			BindTranslations(folder);
		}

		return loaded;
	}

	/// <summary>
	/// What the libraries libmpv loads (MSYS2's builds) read their configuration from, pointed into its own folder, where
	/// none of it is shipped, so they keep their defaults. Unset, they read files outside FluentFin's folder: GnuTLS its
	/// system priority file at MSYS2's build path (<c>D:/M/msys64/ucrt64/etc/gnutls/config</c>, a folder any user can make
	/// on a second drive: weaker TLS), libxml2 the catalog <c>\ucrt64\etc\xml\catalog</c> of the current drive, p11-kit the
	/// user's own PKCS#11 configuration; and OpenSSL its <c>openssl.cnf</c> and providers next to FluentFin.exe
	/// (<c>etc\ssl</c>, <c>lib\ossl-modules</c>: a config there loads any DLL as a provider), and libva its drivers.
	/// </summary>
	internal static (string Name, string Value)[] LibraryEnvironment(string folder) =>
	[
		("GNUTLS_SYSTEM_PRIORITY_FILE", Path.Combine(folder, "etc", "gnutls", "config")),
		("OPENSSL_CONF", Path.Combine(folder, "etc", "ssl", "openssl.cnf")),
		("OPENSSL_CONF_INCLUDE", Path.Combine(folder, "etc", "ssl")),
		("OPENSSL_MODULES", Path.Combine(folder, "lib", "ossl-modules")),
		("P11_KIT_NO_USER_CONFIG", "1"),
		("XML_CATALOG_FILES", Path.Combine(folder, "etc", "xml", "catalog")),
		("LIBVA_DRIVERS_PATH", Path.Combine(folder, "lib", "dri")),
	];

	/// <summary>
	/// Sets <see cref="LibraryEnvironment"/> in this process: Windows' environment (OpenSSL reads that) and the C runtime's
	/// copy the MSYS2 libraries' <c>getenv</c> reads (ucrtbase's <c>_wputenv_s</c> sets both; the runtime may have copied
	/// the environment before, so <see cref="Environment.SetEnvironmentVariable(string, string)"/> alone is not enough).
	/// The backend and its mpv inherit them, harmlessly: every path is inside FluentFin's folder.
	/// </summary>
	internal static void ApplyLibraryEnvironment(string folder)
	{
		foreach (var (name, value) in LibraryEnvironment(folder))
		{
			Environment.SetEnvironmentVariable(name, value);
			_ = WPutEnvS(name, value);
		}
	}

	/// <summary>
	/// GnuTLS binds its translations to MSYS2's build path (<c>D:/M/msys64/ucrt64/share/locale</c>) when it loads: bound
	/// again to the folder's <c>share\locale</c> (not shipped), so no message catalog outside FluentFin's folder is read.
	/// </summary>
	private static void BindTranslations(string folder)
	{
		if (NativeModules.LoadedPath("libintl-8.dll") is { } intl && Path.GetDirectoryName(intl) is { } dir
			&& string.Equals(dir, folder, StringComparison.OrdinalIgnoreCase)
			&& NativeLibrary.TryGetExport(GetModuleHandleW("libintl-8.dll"), "libintl_wbindtextdomain", out var bind))
		{
			var locale = Marshal.StringToHGlobalUni(Path.Combine(folder, "share", "locale"));
			try
			{
				fixed (byte* domain = "gnutls\0"u8)
				{
					((delegate* unmanaged<byte*, nint, nint>)bind)(domain, locale);
				}
			}
			finally
			{
				// libintl keeps its own copy of the directory name
				Marshal.FreeHGlobal(locale);
			}
		}
	}

	/// <summary>Where GnuTLS's translations are bound now (tests): null when libintl is not loaded.</summary>
	internal static string? GnutlsTranslations()
	{
		if (!NativeLibrary.TryGetExport(GetModuleHandleW("libintl-8.dll"), "libintl_wbindtextdomain", out var bind))
		{
			return null;
		}

		fixed (byte* domain = "gnutls\0"u8)
		{
			return Marshal.PtrToStringUni(((delegate* unmanaged<byte*, nint, nint>)bind)(domain, 0));
		}
	}

	[LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint LoadLibraryExW(string fileName, nint file, uint flags);

	[LibraryImport("kernel32.dll")]
	private static partial uint SetErrorMode(uint mode);

	[LibraryImport("kernel32.dll")]
	private static partial uint GetErrorMode();

	[LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint GetModuleHandleW(string name);

	[LibraryImport("ucrtbase.dll", EntryPoint = "_wputenv_s", StringMarshalling = StringMarshalling.Utf16)]
	private static partial int WPutEnvS(string name, string value);

	public const int FormatNone = 0, FormatString = 1, FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;
	public const int EventIdNone = 0, EventIdShutdown = 1, EventIdLogMessage = 2, EventIdPropertyChange = 22;

	[StructLayout(LayoutKind.Sequential)]
	public struct Event
	{
		public int EventId;
		public int Error;
		public ulong ReplyUserdata;
		public nint Data;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct EventProperty
	{
		public nint Name;
		public int Format;
		public nint Data;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct EventLogMessage
	{
		public nint Prefix;
		public nint Level;
		public nint Text;
		public int LogLevel;
	}

	private static readonly Lazy<string?> s_unavailable = new(ProbeInstalled);

	/// <summary>
	/// What the in-process core cannot do without (<see cref="MpvInProcessCore.Options"/>): rendering with gpu-next on d3d11
	/// into a composition swap chain the panel shows. A libmpv that refuses one of them before <c>mpv_initialize</c> is too old
	/// or built without it, and FluentFin's controls would draw over no video.
	/// </summary>
	public static readonly (string Name, string Value)[] Required = [("vo", "gpu-next"), ("gpu-context", "d3d11"), ("d3d11-output-mode", "composition")];

	/// <summary>
	/// The DLL loads, is new enough (client API 2.x) and can make a core that takes <see cref="Required"/> (the handle is
	/// destroyed again at once; nothing is initialized, so nothing renders or plays). <paramref name="why"/> says what is wrong
	/// otherwise. Probed once per process.
	/// </summary>
	public static bool IsAvailable(out string why)
	{
		why = s_unavailable.Value ?? "";
		return s_unavailable.Value is null;
	}

	/// <summary>Why FluentFin's libmpv cannot be used, null when it can (<see cref="IsAvailable"/>).</summary>
	public static string? Unavailable() => s_unavailable.Value;

	/// <summary>The calls the probe makes, so its decision can be checked without a libmpv of every age.</summary>
	internal sealed record ProbeApi(Func<ulong> ClientApiVersion, Func<nint> Create, Func<nint, string, string, int> SetOptionString, Action<nint> Destroy, Func<int, string> ErrorString);

	/// <summary>
	/// The probe's decision (pure over <paramref name="api"/>): client API 2.x, <c>mpv_create</c> works, and the new handle
	/// takes every <see cref="Required"/> option; the handle is destroyed whatever happens. Null when usable, else why not.
	/// </summary>
	internal static string? Probe(ProbeApi api, string name = Dll)
	{
		var version = api.ClientApiVersion();
		if ((version >> 16) != 2)
		{
			return $"{name} has client API {version >> 16}.{version & 0xffff}, 2.x is needed";
		}

		var ctx = api.Create();
		if (ctx == 0)
		{
			return $"{name} could not make an mpv core (mpv_create failed)";
		}

		try
		{
			foreach (var (option, value) in Required)
			{
				var rc = api.SetOptionString(ctx, option, value);
				if (rc < 0)
				{
					return $"{name} does not take {option}={value} ({api.ErrorString(rc)}): a newer libmpv is needed";
				}
			}

			return null;
		}
		finally
		{
			api.Destroy(ctx);
		}
	}

	/// <summary>
	/// The probe of the DLL at <paramref name="path"/>, loaded as the installed one is (<see cref="LoadLibmpv"/> for a
	/// libmpv-2.dll, else <see cref="Load"/>; tests: a file that is not, or not a usable, libmpv). Never throws.
	/// </summary>
	internal static string? ProbeFile(string path)
	{
		var name = Path.GetFileName(path);
		try
		{
			var (library, error) = string.Equals(name, Dll, StringComparison.OrdinalIgnoreCase) ? LoadLibmpv(path) : Load(path);
			return library != 0 ? ProbeLibrary(library, name) : $"{name} could not be loaded: {error}";
		}
		catch (Exception ex)
		{
			return $"{name} could not be loaded: {ex.Message}";
		}
	}

	/// <summary>The probe of the DLL the core will use: <see cref="InstalledPath"/>, the library the imports below bind to.</summary>
	private static string? ProbeInstalled()
	{
		try
		{
			if (s_resolverError is not null)
			{
				return $"{Dll} cannot be bound to Seaglass's {Folder} folder: {s_resolverError}";
			}

			var (library, error) = s_installed.Value;
			if (library == 0)
			{
				return !File.Exists(InstalledPath) ? $"{Dll} was not found in Seaglass's {Folder} folder"
					: error?.Contains("restart Seaglass", StringComparison.Ordinal) == true ? $"{Dll} cannot be used now: {error}"
					: $"{Dll} could not be loaded from Seaglass's {Folder} folder: {error}";
			}

			return ProbeLibrary(library, Dll);
		}
		catch (Exception ex)
		{
			return $"{Dll} could not be loaded: {ex.Message}";
		}
	}

	/// <summary>The probe through the library's own exports (not the imports below, which bind to <see cref="Dll"/> by name).</summary>
	private static string? ProbeLibrary(nint library, string name)
	{
		string[] exports = ["mpv_client_api_version", "mpv_create", "mpv_set_option_string", "mpv_terminate_destroy", "mpv_error_string"];
		var at = new nint[exports.Length];
		for (var i = 0; i < exports.Length; i++)
		{
			if (!NativeLibrary.TryGetExport(library, exports[i], out at[i]))
			{
				return $"{name} is not libmpv (it has no {exports[i]})";
			}
		}

		var (version, create, setOption, destroy, errorString) = (at[0], at[1], at[2], at[3], at[4]);
		return Probe(new ProbeApi(
			() => ((delegate* unmanaged<ulong>)version)(),
			() => ((delegate* unmanaged<nint>)create)(),
			(ctx, option, value) =>
			{
				var o = Marshal.StringToCoTaskMemUTF8(option);
				var v = Marshal.StringToCoTaskMemUTF8(value);
				try
				{
					return ((delegate* unmanaged<nint, nint, nint, int>)setOption)(ctx, o, v);
				}
				finally
				{
					Marshal.FreeCoTaskMem(o);
					Marshal.FreeCoTaskMem(v);
				}
			},
			ctx => ((delegate* unmanaged<nint, void>)destroy)(ctx),
			rc => Marshal.PtrToStringUTF8(((delegate* unmanaged<int, nint>)errorString)(rc)) ?? rc.ToString(System.Globalization.CultureInfo.InvariantCulture)), name);
	}

	[LibraryImport(Dll)]
	public static partial ulong mpv_client_api_version();

	[LibraryImport(Dll)]
	public static partial nint mpv_create();

	[LibraryImport(Dll)]
	public static partial int mpv_initialize(nint ctx);

	[LibraryImport(Dll)]
	public static partial void mpv_terminate_destroy(nint ctx);

	[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
	public static partial int mpv_set_option_string(nint ctx, string name, string value);

	[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
	public static partial int mpv_get_property(nint ctx, string name, int format, void* data);

	[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
	public static partial int mpv_set_property_async(nint ctx, ulong replyUserdata, string name, int format, void* data);

	[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
	public static partial int mpv_observe_property(nint ctx, ulong replyUserdata, string name, int format);

	[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
	public static partial int mpv_request_log_messages(nint ctx, string minLevel);

	[LibraryImport(Dll)]
	public static partial Event* mpv_wait_event(nint ctx, double timeout);

	[LibraryImport(Dll)]
	public static partial void mpv_wakeup(nint ctx);

	[LibraryImport(Dll)]
	public static partial void mpv_free(nint data);

	[LibraryImport(Dll)]
	public static partial nint mpv_error_string(int error);

	public static string Error(int code) => Marshal.PtrToStringUTF8(mpv_error_string(code)) ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture);

	public static string? Utf8(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);

	public static long? GetInt64(nint ctx, string name)
	{
		long value;
		return mpv_get_property(ctx, name, FormatInt64, &value) >= 0 ? value : null;
	}

	/// <summary>Sets a property without waiting for the core (the UI thread never blocks on it): the answer is an event.</summary>
	public static int SetStringAsync(nint ctx, string name, string value)
	{
		var utf8 = Marshal.StringToCoTaskMemUTF8(value);
		try
		{
			// libmpv copies the value before returning (client.h, mpv_set_property_async)
			return mpv_set_property_async(ctx, 0, name, FormatString, &utf8);
		}
		finally
		{
			Marshal.FreeCoTaskMem(utf8);
		}
	}
}

/// <summary>
/// fluentfin-mpv: which DLLs are loaded in this process, and from where. Windows hands a DLL that imports a name the
/// already-loaded module of that name, wherever it came from: FluentFin's libmpv and Flyleaf each bring an FFmpeg with the
/// same DLL names (avcodec-63.dll, ...), so the second to load would run on the first one's FFmpeg.
/// </summary>
internal static unsafe partial class NativeModules
{
	private static readonly System.Text.RegularExpressions.Regex s_ffmpeg = new(
		@"^(avcodec|avdevice|avfilter|avformat|avutil|postproc|swresample|swscale)-\d+\.dll$",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	/// <summary>The FFmpeg DLLs in <paramref name="folder"/> (their file names); none when it is not there.</summary>
	public static string[] FFmpegNames(string folder) =>
		Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*.dll").Select(f => System.IO.Path.GetFileName(f)).Where(n => s_ffmpeg.IsMatch(n!)).Select(n => n!).Order(StringComparer.OrdinalIgnoreCase)] : [];

	/// <summary>
	/// Those of <paramref name="names"/> that are loaded already, from a folder other than <paramref name="folder"/>:
	/// (name, full path). <paramref name="loadedPath"/> says where a loaded module of a name is from (null: not loaded).
	/// </summary>
	public static List<(string Name, string Path)> LoadedElsewhere(IEnumerable<string> names, string folder, Func<string, string?> loadedPath)
	{
		var full = System.IO.Path.GetFullPath(folder).TrimEnd('\\');
		var found = new List<(string, string)>();
		foreach (var name in names)
		{
			if (loadedPath(name) is { } path && !string.Equals(System.IO.Path.GetDirectoryName(path), full, StringComparison.OrdinalIgnoreCase))
			{
				found.Add((name, path));
			}
		}

		return found;
	}

	/// <summary>The full path of the loaded module named <paramref name="name"/>, null when none is loaded.</summary>
	public static string? LoadedPath(string name)
	{
		var module = GetModuleHandleW(name);
		if (module == 0)
		{
			return null;
		}

		var buffer = new char[32768];
		fixed (char* p = buffer)
		{
			var length = GetModuleFileNameW(module, p, (uint)buffer.Length);
			return length == 0 ? null : new string(p, 0, (int)length);
		}
	}

	[LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint GetModuleHandleW(string name);

	[LibraryImport("kernel32.dll")]
	private static partial uint GetModuleFileNameW(nint module, char* buffer, uint size);
}