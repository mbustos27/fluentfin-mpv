using System.Runtime.InteropServices;
using FluentFin.Mpv;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// 1.5 (BH-140X-M1): which certificates the stream itself trusts. The UI signs in against the Windows store, the backend
/// does since 1.5 (truststore), and libmpv must too, or a server with a private certificate Windows trusts would browse
/// and then play nothing. mpv 0.42 opens http(s) streams through libcurl (stream_curl.c), asks it for the system's store
/// (<c>CURLSSLOPT_NATIVE_CA</c>, set whenever libcurl has TLS) and verifies by default (<c>tls-verify</c> is yes). The
/// vendored libcurl is OpenSSL's, whose native-CA option reads the Windows ROOT store; MSYS2's relocated CA bundle
/// (<c>..\etc\ssl\certs\ca-bundle.crt</c>) is not shipped, so the Windows store is the only list it has (checked once,
/// 2026-11-20: libmpv opened a public HTTPS site with no bundle present, and refused the tests' private CA).
/// A transcode is the same: mpv 0.42 opens an HLS stream's nested requests (its playlists, segments and keys) through
/// libcurl first (demux_lavf.c, nested_io_open) and leaves to FFmpeg's own I/O, whose TLS is GnuTLS (avformat-63.dll), only
/// what libcurl does not open; that GnuTLS's system trust is the Windows ROOT and CA stores as well (the review of 1.5, m3).
/// These pin what that depends on: verification on, nothing in Nixie's own options overriding it, a libcurl able to read
/// the Windows store, and a GnuTLS whose system trust is that store.
/// </summary>
[Collection(AloneCollection.Name)]
public sealed class LibmpvTlsTests
{
	[VendoredLibmpvFact]
	public void TheVendoredLibmpv_VerifiesTheStreamsCertificate_ByDefault()
	{
		Assert.True(Libmpv.IsAvailable(out var why), why);
		var ctx = Libmpv.mpv_create();
		Assert.NotEqual(0, ctx);
		try
		{
			foreach (var (name, value) in new[] { ("vo", "null"), ("ao", "null"), ("idle", "yes"), ("config", "no"), ("terminal", "no") })
			{
				Assert.Equal(0, Libmpv.mpv_set_option_string(ctx, name, value));
			}

			Assert.Equal(0, Libmpv.mpv_initialize(ctx));
			Assert.Equal("yes", GetString(ctx, "tls-verify"));
			Assert.Equal("", GetString(ctx, "tls-ca-file"));
		}
		finally
		{
			Libmpv.mpv_terminate_destroy(ctx);
		}
	}

	[Fact]
	public void NixiesOwnCoreOptions_LeaveTheStreamsTlsToMpvsDefaults()
	{
		var options = MpvInProcessCore.Options("fluentfin-mpv-test", @"C:\config");

		Assert.DoesNotContain(options, o => o.Name.StartsWith("tls-", StringComparison.Ordinal));
	}

	[VendoredLibmpvFact]
	public void TheVendoredLibmpv_StreamsAndTranscodesThroughALibcurl_ThatCanReadTheWindowsStore()
	{
		Assert.True(Libmpv.IsAvailable(out var why), why);
		var folder = Path.GetDirectoryName(Libmpv.InstalledPath)!;
		Assert.Contains("libcurl-4.dll", PeImports.Read(Libmpv.InstalledPath), StringComparer.OrdinalIgnoreCase);

		var curl = NativeLibrary.Load(Path.Combine(folder, "libcurl-4.dll"));
		var versionInfo = Marshal.GetDelegateForFunctionPointer<CurlVersionInfoCall>(NativeLibrary.GetExport(curl, "curl_version_info"));
		var info = Marshal.PtrToStructure<CurlVersionInfo>(versionInfo(11));
		var ssl = Marshal.PtrToStringUTF8(info.SslVersion) ?? "";

		// CURLSSLOPT_NATIVE_CA reads the Windows store with OpenSSL from 7.71.0 (and Schannel always uses it)
		Assert.True(info.VersionNum >= 0x074700, $"libcurl {Marshal.PtrToStringUTF8(info.Version)} is older than 7.71.0");
		Assert.True(ssl.StartsWith("OpenSSL/", StringComparison.Ordinal) || ssl.Contains("Schannel", StringComparison.Ordinal),
			$"libcurl's TLS is {ssl}: its native-CA option may not read the Windows store");
		Assert.False(File.Exists(Path.Combine(folder, "..", "etc", "ssl", "certs", "ca-bundle.crt")),
			"MSYS2's CA bundle is next to libmpv: the stream would trust Mozilla's list as well as Windows'");
	}

	/// <summary>
	/// The review of 1.5 (m3): FFmpeg's TLS in the vendored libmpv is GnuTLS (avformat-63.dll imports it), and GnuTLS's
	/// system trust on Windows is the current user's ROOT and CA stores (CertOpenStore): the certificates it adds are exactly
	/// theirs (read only; nothing is added to a store).
	/// </summary>
	[VendoredLibmpvFact]
	public void FFmpegsGnuTls_TrustsTheWindowsStores()
	{
		Assert.True(Libmpv.IsAvailable(out var why), why);
		var folder = Path.GetDirectoryName(Libmpv.InstalledPath)!;
		Assert.Contains("libgnutls-30.dll", PeImports.Read(Path.Combine(folder, "avformat-63.dll")), StringComparer.OrdinalIgnoreCase);

		var gnutls = NativeLibrary.Load(Path.Combine(folder, "libgnutls-30.dll"));
		var init = Marshal.GetDelegateForFunctionPointer<TrustListInitCall>(NativeLibrary.GetExport(gnutls, "gnutls_x509_trust_list_init"));
		var addSystem = Marshal.GetDelegateForFunctionPointer<TrustListAddSystemCall>(NativeLibrary.GetExport(gnutls, "gnutls_x509_trust_list_add_system_trust"));
		var deinit = Marshal.GetDelegateForFunctionPointer<TrustListDeinitCall>(NativeLibrary.GetExport(gnutls, "gnutls_x509_trust_list_deinit"));
		Assert.Equal(0, init(out var list, 0));
		int added;
		try
		{
			added = addSystem(list, 0, 0);
		}
		finally
		{
			deinit(list, 1);
		}

		static int Count(System.Security.Cryptography.X509Certificates.StoreName name)
		{
			using var store = new System.Security.Cryptography.X509Certificates.X509Store(name, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
			store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly | System.Security.Cryptography.X509Certificates.OpenFlags.OpenExistingOnly);
			return store.Certificates.Count;
		}

		var windows = Count(System.Security.Cryptography.X509Certificates.StoreName.Root) + Count(System.Security.Cryptography.X509Certificates.StoreName.CertificateAuthority);
		Assert.True(added > 0, "GnuTLS's system trust added no certificate");
		Assert.Equal(windows, added);
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int TrustListInitCall(out nint list, uint size);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int TrustListAddSystemCall(nint list, uint flags, uint verifyFlags);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void TrustListDeinitCall(nint list, uint all);

	private static string? GetString(nint ctx, string name)
	{
		var get = Marshal.GetDelegateForFunctionPointer<GetPropertyStringCall>(
			NativeLibrary.GetExport(NativeLibrary.Load(Libmpv.InstalledPath), "mpv_get_property_string"));
		var value = get(ctx, name);
		if (value == 0)
		{
			return null;
		}

		try
		{
			return Libmpv.Utf8(value);
		}
		finally
		{
			Libmpv.mpv_free(value);
		}
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate nint GetPropertyStringCall(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate nint CurlVersionInfoCall(int age);

	/// <summary>The start of curl's <c>curl_version_info_data</c> (the fields every age has).</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct CurlVersionInfo
	{
		public int Age;
		public nint Version;
		public uint VersionNum;
		public nint Host;
		public int Features;
		public nint SslVersion;
	}
}
