using System.Reflection;
using System.Runtime.InteropServices;

namespace FluentFin.Mpv;

/// <summary>
/// fluentfin-mpv player controls: the few libmpv calls (client.h, API 2.x) the in-process core needs, against shinchiro's
/// <c>libmpv-2.dll</c> next to FluentFin.exe (vendor\libmpv\VENDORED.md). Only loaded when "Player controls: FluentFin" is
/// chosen; <see cref="IsAvailable"/> says whether it can be, without throwing.
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

	private static readonly Lazy<string?> s_unavailable = new(Probe);

	/// <summary>The DLL loads and is new enough (client API 2.x). <paramref name="why"/> says what is wrong otherwise.</summary>
	public static bool IsAvailable(out string why)
	{
		why = s_unavailable.Value ?? "";
		return s_unavailable.Value is null;
	}

	private static string? Probe()
	{
		try
		{
			if (!NativeLibrary.TryLoad(Dll, Assembly.GetExecutingAssembly(), DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32, out _))
			{
				return $"{Dll} was not found next to FluentFin";
			}

			var version = mpv_client_api_version();
			return (version >> 16) == 2 ? null : $"{Dll} has client API {version >> 16}.{version & 0xffff}, 2.x is needed";
		}
		catch (Exception ex)
		{
			return $"{Dll} could not be loaded: {ex.Message}";
		}
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
