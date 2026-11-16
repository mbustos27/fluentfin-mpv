using System.Reflection;
using System.Runtime.InteropServices;

namespace FluentFin.Mpv;

/// <summary>
/// fluentfin-mpv player controls: the few libmpv calls (client.h, API 2.x) the in-process core needs, against shinchiro's
/// <c>libmpv-2.dll</c> next to FluentFin.exe (vendor\libmpv\VENDORED.md). Only loaded when "Player controls: FluentFin" is
/// chosen; <see cref="IsAvailable"/> says whether it can be (and does composition output), without throwing.
/// </summary>
internal static unsafe partial class Libmpv
{
	public const string Dll = "libmpv-2.dll";

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

	/// <summary>Why the libmpv next to FluentFin cannot be used, null when it can (<see cref="IsAvailable"/>).</summary>
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

	/// <summary>The probe of the DLL at <paramref name="path"/> (tests: a file that is not, or not a usable, libmpv). Never throws.</summary>
	internal static string? ProbeFile(string path)
	{
		var name = Path.GetFileName(path);
		try
		{
			return NativeLibrary.TryLoad(path, out var library) ? ProbeLibrary(library, name) : $"{name} could not be loaded";
		}
		catch (Exception ex)
		{
			return $"{name} could not be loaded: {ex.Message}";
		}
	}

	/// <summary>The probe of the DLL the core will use: <see cref="Dll"/> next to FluentFin, as the imports below find it.</summary>
	private static string? ProbeInstalled()
	{
		try
		{
			if (!NativeLibrary.TryLoad(Dll, Assembly.GetExecutingAssembly(), DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32, out var library))
			{
				return $"{Dll} was not found next to FluentFin";
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
