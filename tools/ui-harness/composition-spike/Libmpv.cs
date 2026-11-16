using System.Runtime.InteropServices;
using System.Text;

namespace CompositionSpike;

/// <summary>
/// The few libmpv calls the spike needs (client.h, API 2.6), against shinchiro's libmpv-2.dll (vendor\libmpv\VENDORED.md).
/// One core per process here; every call off the UI thread except the cheap property reads the UI makes on purpose.
/// </summary>
internal static unsafe partial class Libmpv
{
	private const string Dll = "libmpv-2.dll";

	public const int FormatNone = 0, FormatString = 1, FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;
	public const int EventNone = 0, EventShutdown = 1, EventIdLogMessage = 2, EventStartFile = 6, EventEndFile = 7,
		EventFileLoaded = 8, EventVideoReconfig = 17, EventPropertyChange = 22;

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
	public static partial int mpv_set_property_string(nint ctx, string name, string value);

	[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
	public static partial int mpv_get_property(nint ctx, string name, int format, void* data);

	[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
	public static partial int mpv_observe_property(nint ctx, ulong replyUserdata, string name, int format);

	[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
	public static partial int mpv_request_log_messages(nint ctx, string minLevel);

	[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
	public static partial int mpv_command_string(nint ctx, string args);

	[LibraryImport(Dll)]
	public static partial int mpv_command(nint ctx, nint* args);

	[LibraryImport(Dll)]
	public static partial Event* mpv_wait_event(nint ctx, double timeout);

	[LibraryImport(Dll)]
	public static partial void mpv_wakeup(nint ctx);

	[LibraryImport(Dll)]
	public static partial void mpv_free(nint data);

	[LibraryImport(Dll)]
	public static partial nint mpv_error_string(int error);

	public static string Error(int code) => Marshal.PtrToStringUTF8(mpv_error_string(code)) ?? code.ToString();

	public static string? Utf8(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);

	/// <summary>A command with its arguments as separate strings (no quoting rules to get wrong).</summary>
	public static int Command(nint ctx, params string[] args)
	{
		var pointers = new nint[args.Length + 1];
		try
		{
			for (var i = 0; i < args.Length; i++)
			{
				pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
			}

			fixed (nint* p = pointers)
			{
				return mpv_command(ctx, p);
			}
		}
		finally
		{
			foreach (var p in pointers)
			{
				if (p != 0)
				{
					Marshal.FreeCoTaskMem(p);
				}
			}
		}
	}

	public static long? GetInt64(nint ctx, string name)
	{
		long value;
		return mpv_get_property(ctx, name, FormatInt64, &value) >= 0 ? value : null;
	}

	public static double? GetDouble(nint ctx, string name)
	{
		double value;
		return mpv_get_property(ctx, name, FormatDouble, &value) >= 0 ? value : null;
	}

	public static string? GetString(nint ctx, string name)
	{
		nint value;
		if (mpv_get_property(ctx, name, FormatString, &value) < 0)
		{
			return null;
		}

		try
		{
			return Utf8(value);
		}
		finally
		{
			mpv_free(value);
		}
	}
}
