using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace CompositionSpike;

/// <summary>
/// Raw COM calls (vtable slots from the Windows SDK headers) for the spike: hand mpv's composition swap chain to a XAML
/// SwapChainPanel, read that swap chain's description, and ask DXGI what each display is doing (HDR or not). Read-only
/// towards the system: nothing here changes a display.
/// </summary>
internal static unsafe partial class Dxgi
{
	private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
	private static readonly Guid IidSwapChain1 = new("790a45f7-0d42-4876-983a-0a55cfe6f4aa");
	private static readonly Guid IidSwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");
	private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
	private static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

	private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

	/// <summary>ISwapChainPanelNative::SetSwapChain on the UI thread. The panel takes its own reference.</summary>
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
	public struct SwapChainDesc1
	{
		public uint Width, Height, Format, Stereo, SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
	}

	/// <summary>IDXGISwapChain1::GetDesc1 (slot 18) of the swap chain mpv published, or null.</summary>
	public static SwapChainDesc1? Describe(nint swapChain)
	{
		var iid = IidSwapChain1;
		if (swapChain == 0 || Marshal.QueryInterface(swapChain, in iid, out var chain) < 0)
		{
			return null;
		}

		try
		{
			SwapChainDesc1 desc;
			var hr = ((delegate* unmanaged[Stdcall]<nint, SwapChainDesc1*, int>)Slot(chain, 18))(chain, &desc);
			return hr < 0 ? null : desc;
		}
		finally
		{
			Marshal.Release(chain);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Matrix3x2
	{
		public float M11, M12, M21, M22, DX, DY;
	}

	/// <summary>
	/// IDXGISwapChain2::SetMatrixTransform (slot 34): a composition swap chain sized in physical pixels shows 1:1 in a
	/// panel only with the inverse of the panel's composition scale (100 %: identity, nothing to do).
	/// </summary>
	public static int SetInverseScale(nint swapChain, float scaleX, float scaleY)
	{
		var iid = IidSwapChain2;
		if (swapChain == 0 || Marshal.QueryInterface(swapChain, in iid, out var chain) < 0)
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

	public static string FormatName(uint format) => format switch
	{
		10 => "R16G16B16A16_FLOAT",
		24 => "R10G10B10A2_UNORM",
		28 => "R8G8B8A8_UNORM",
		87 => "B8G8R8A8_UNORM",
		_ => $"DXGI_FORMAT {format}",
	};

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

	/// <summary>
	/// One line per display: name, desktop rectangle, bits per colour, colour space (12 = G2084/BT.2020: Windows HDR is on
	/// for that display), peak luminance, and whether hardware composition (overlay planes) is supported windowed and
	/// full screen (IDXGIOutput6::CheckHardwareCompositionSupport).
	/// </summary>
	public static List<string> Displays()
	{
		var lines = new List<string>();
		var iid = IidFactory1;
		if (CreateDXGIFactory1(in iid, out var factory) < 0)
		{
			lines.Add("CreateDXGIFactory1 failed");
			return lines;
		}

		try
		{
			for (uint a = 0; ; a++)
			{
				nint adapter;
				if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12))(factory, a, &adapter) < 0)
				{
					break;
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
								lines.Add($"adapter {a} output {o}: no IDXGIOutput6");
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
									uint flags = 0;
									((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(output6, 28))(output6, &flags);
									lines.Add($"adapter {a} output {o} {d.DeviceName.TrimEnd('\0')} at {d.Left},{d.Top}-{d.Right},{d.Bottom}: {d.BitsPerColor} bpc, colour space {d.ColorSpace}{(d.ColorSpace == 12 ? " (HDR on: G2084 P2020)" : d.ColorSpace == 0 ? " (SDR: G22 P709)" : "")}, peak {d.MaxLuminance:0} nits, full frame {d.MaxFullFrameLuminance:0}, hardware composition fullscreen={(flags & 1) != 0} windowed={(flags & 2) != 0}");
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

		return lines;
	}

	[LibraryImport("dxgi.dll")]
	private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);
}
