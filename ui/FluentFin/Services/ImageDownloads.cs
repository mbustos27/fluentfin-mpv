using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.5 (season speed): how many pictures come from the server at once. WinUI downloads every picture an Image shows
/// (a <c>BitmapImage</c>'s <c>UriSource</c>) through WinINet, which gives a process 2 connections to an HTTP/1.1 server unless
/// told otherwise (Internet Explorer raised its own to 6; other programs keep 2). So a season's 20 episode stills came 2 at a
/// time, 0.4 to 0.9 s after the rows on a server answering a picture in 80 ms (then-season-switch.ps1). Six, as browsers
/// use, and as jellyfin-web gets in one. An HTTP/2 server (over TLS) already multiplexes them on one connection.
/// </summary>
internal static partial class ImageDownloads
{
	/// <summary>Connections to one server at once, for WinINet's HTTP/1.1 and HTTP/1.0 servers alike.</summary>
	public const uint AtOnce = 6;

	// wininet.h: INTERNET_OPTION_MAX_CONNS_PER_SERVER, INTERNET_OPTION_MAX_CONNS_PER_1_0_SERVER
	internal const int MaxConnsPerServer = 73, MaxConnsPer10Server = 74;

	[LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool InternetSetOption(IntPtr handle, int option, ref uint buffer, int length);

	[LibraryImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool InternetQueryOption(IntPtr handle, int option, ref uint buffer, ref int length);

	/// <summary>Once, at start-up, before the first picture: the process's default (a null handle) for every later connection.</summary>
	public static void AllowMoreAtOnce(ILogger logger)
	{
		try
		{
			foreach (var option in new[] { MaxConnsPerServer, MaxConnsPer10Server })
			{
				var value = AtOnce;
				if (!InternetSetOption(IntPtr.Zero, option, ref value, sizeof(uint)))
				{
					logger.LogWarning("Pictures: WinINet refused {AtOnce} connections per server (option {Option}, error {Error})", AtOnce, option, Marshal.GetLastPInvokeError());
				}
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			// no WinINet (never on Windows 10 or 11): pictures come 2 at a time, as before
			logger.LogWarning(ex, "Pictures: WinINet's connections per server could not be set");
		}
	}

	/// <summary>WinINet's connections per HTTP/1.1 server in this process now (tests, the log); 0 when it cannot say.</summary>
	public static uint Current()
	{
		var value = 0u;
		var length = sizeof(uint);
		try
		{
			return InternetQueryOption(IntPtr.Zero, MaxConnsPerServer, ref value, ref length) ? value : 0;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return 0;
		}
	}
}
