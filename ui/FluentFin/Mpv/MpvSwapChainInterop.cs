using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Mpv;

/// <summary>
/// fluentfin-mpv player controls: the raw COM and Win32 calls the composition video needs (vtable slots from the Windows
/// SDK headers, as measured in tools\ui-harness\composition-spike): hand mpv's swap chain to a SwapChainPanel, scale it to
/// the panel's DPI, and read what the window's display is doing (HDR on or not, refresh rate). Read-only towards the
/// system: nothing here changes a display.
/// </summary>
internal static unsafe partial class MpvSwapChainInterop
{
	private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
	private static readonly Guid IidSwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");
	private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
	private static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

	/// <summary>DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020: Windows HDR is on for that display.</summary>
	private const int ColorSpaceHdr10 = 12;

	private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

	/// <summary>ISwapChainPanelNative::SetSwapChain (UI thread). 0 unbinds. The panel keeps its own reference.</summary>
	public static int Bind(SwapChainPanel panel, nint swapChain)
	{
		var unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
		var iid = IidSwapChainPanelNative;
		var hr = Marshal.QueryInterface(unknown, in iid, out var native);
		if (hr < 0)
		{
			return hr;
		}

		try
		{
			return ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(native, 3))(native, swapChain);
		}
		finally
		{
			Marshal.Release(native);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Matrix3x2
	{
		public float M11, M12, M21, M22, DX, DY;
	}

	/// <summary>
	/// IDXGISwapChain2::SetMatrixTransform: a composition swap chain rendered at the panel's physical size shows 1:1 only
	/// with the inverse of the panel's composition scale (identity at 100 %).
	/// </summary>
	public static int SetInverseScale(nint swapChain, float scaleX, float scaleY)
	{
		var iid = IidSwapChain2;
		if (swapChain == 0 || scaleX <= 0 || scaleY <= 0 || Marshal.QueryInterface(swapChain, in iid, out var chain) < 0)
		{
			return -1;
		}

		try
		{
			var m = new Matrix3x2 { M11 = 1 / scaleX, M22 = 1 / scaleY };
			return ((delegate* unmanaged[Stdcall]<nint, Matrix3x2*, int>)Slot(chain, 34))(chain, &m);
		}
		finally
		{
			Marshal.Release(chain);
		}
	}

	/// <summary>What the display a window is on does: HDR on, its peak luminance, its refresh rate (0 when unknown).</summary>
	public readonly record struct DisplayState(bool Hdr, double PeakNits, double RefreshHz);

	/// <summary>The display <paramref name="hwnd"/> is mostly on. Never throws: an unknown display is SDR at an unknown rate.</summary>
	public static DisplayState DisplayOf(nint hwnd)
	{
		try
		{
			var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
			if (monitor == 0)
			{
				return default;
			}

			var refresh = RefreshOf(monitor);
			var (hdr, peak) = HdrOf(monitor);
			return new DisplayState(hdr, peak, refresh);
		}
		catch (Exception)
		{
			return default;
		}
	}

	private static double RefreshOf(nint monitor)
	{
		var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
		if (!GetMonitorInfoW(monitor, &info))
		{
			return 0;
		}

		var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
		return EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 0;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct OutputDesc1
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string DeviceName;
		public int Left, Top, Right, Bottom;
		public int AttachedToDesktop;
		public int Rotation;
		public nint Monitor;
		public uint BitsPerColor;
		public int ColorSpace;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
		public float[] Primaries;
		public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
	}

	/// <summary>IDXGIOutput6::GetDesc1 of the output showing <paramref name="monitor"/>: its colour space and peak.</summary>
	private static (bool Hdr, double Peak) HdrOf(nint monitor)
	{
		var iid = IidFactory1;
		if (CreateDXGIFactory1(in iid, out var factory) < 0)
		{
			return (false, 0);
		}

		try
		{
			for (uint a = 0; ; a++)
			{
				nint adapter;
				if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12))(factory, a, &adapter) < 0)
				{
					return (false, 0);
				}

				try
				{
					for (uint o = 0; ; o++)
					{
						nint output;
						if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, 7))(adapter, o, &output) < 0)
						{
							break;
						}

						try
						{
							var iid6 = IidOutput6;
							if (Marshal.QueryInterface(output, in iid6, out var output6) < 0)
							{
								continue;
							}

							try
							{
								var buffer = Marshal.AllocHGlobal(512);
								try
								{
									if (((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(output6, 27))(output6, buffer) < 0)
									{
										continue;
									}

									var d = Marshal.PtrToStructure<OutputDesc1>(buffer);
									if (d.Monitor == monitor)
									{
										return (d.ColorSpace == ColorSpaceHdr10, d.MaxLuminance);
									}
								}
								finally
								{
									Marshal.FreeHGlobal(buffer);
								}
							}
							finally
							{
								Marshal.Release(output6);
							}
						}
						finally
						{
							Marshal.Release(output);
						}
					}
				}
				finally
				{
					Marshal.Release(adapter);
				}
			}
		}
		finally
		{
			Marshal.Release(factory);
		}
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MONITORINFOEXW
	{
		public uint cbSize;
		public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
		public int WorkLeft, WorkTop, WorkRight, WorkBottom;
		public uint dwFlags;
		public fixed char szDevice[32];
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DEVMODEW
	{
		public fixed char dmDeviceName[32];
		public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
		public uint dmFields;
		public int dmPositionX, dmPositionY;
		public uint dmDisplayOrientation, dmDisplayFixedOutput;
		public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
		public fixed char dmFormName[32];
		public ushort dmLogPixels;
		public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
		public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
	}

	private const uint MONITOR_DEFAULTTONEAREST = 2;
	private const int ENUM_CURRENT_SETTINGS = -1;

	[LibraryImport("dxgi.dll")]
	private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

	[LibraryImport("user32.dll")]
	private static partial nint MonitorFromWindow(nint hwnd, uint flags);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetMonitorInfoW(nint monitor, MONITORINFOEXW* info);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool EnumDisplaySettingsW(char* deviceName, int modeNum, DEVMODEW* mode);
}
