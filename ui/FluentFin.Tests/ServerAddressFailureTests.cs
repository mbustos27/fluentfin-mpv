using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFin.Contracts.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog.Core;
using Serilog.Events;

namespace FluentFin.Tests;

/// <summary>
/// 1.1.4 (states, review of wip/114-states): what the server page and the sign-in say when an address fails, through the
/// real Jellyfin client against servers on this PC's loopback (nothing leaves the PC): https:// typed for a plain-HTTP
/// port, a certificate Windows does not trust, an HTTPS connection that breaks some other way, a 401 from something in front
/// of the server. And the address in the log lines carries no user:password@ or ?api_key=.
/// </summary>
[Collection(nameof(SerilogCapture))]
public sealed class ServerAddressFailureTests
{
	[Fact]
	public async Task HttpsToAPlainHttpPort_SaysToUseHttp()
	{
		using var server = LoopbackServer.PlainHttp(400);

		var (info, error) = await JellyfinAuthenticationService.GetPublicInfo($"https://127.0.0.1:{server.Port}");

		Assert.Null(info);
		Assert.Contains("answers plain HTTP on this port", error, StringComparison.Ordinal);
		Assert.Contains($"Use http://127.0.0.1:{server.Port} instead", error, StringComparison.Ordinal);
		Assert.DoesNotContain("certificate", error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AnUntrustedCertificate_SaysSo()
	{
		using var server = LoopbackServer.SelfSignedTls();

		var (_, error) = await JellyfinAuthenticationService.GetPublicInfo($"https://127.0.0.1:{server.Port}");

		Assert.Contains("isn't trusted by Windows", error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnHttpsConnectionThatBreaksOtherwise_SaysHttps_NotCantReach()
	{
		using var server = LoopbackServer.HangUp();

		var (_, error) = await JellyfinAuthenticationService.GetPublicInfo($"https://127.0.0.1:{server.Port}");

		Assert.Contains("Couldn't set up a secure (HTTPS) connection", error, StringComparison.Ordinal);
		Assert.DoesNotContain("Can't reach", error, StringComparison.Ordinal);
		Assert.DoesNotContain("certificate", error, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(401)]
	[InlineData(403)]
	public async Task TheServerPage_SaysARefusalComesFromInFrontOfTheServer_NotAboutAPassword(int status)
	{
		using var server = LoopbackServer.PlainHttp(status);

		var (_, error) = await JellyfinAuthenticationService.GetPublicInfo($"http://127.0.0.1:{server.Port}");

		Assert.StartsWith($"The server at http://127.0.0.1:{server.Port} refused the connection ({status})", error, StringComparison.Ordinal);
		Assert.DoesNotContain("password", error, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("this account", error, StringComparison.Ordinal);
	}

	[Fact]
	public void TheSignIn_StillSaysWrongPassword_ForA401()
	{
		var text = JellyfinAuthenticationService.DescribeFailure(new Microsoft.Kiota.Abstractions.ApiException("x") { ResponseStatusCode = 401 }, "https://jf.example");

		Assert.StartsWith("Wrong username or password", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheServerPagesLogLine_ShowsNoUserInfoOrApiKey()
	{
		using var closed = LoopbackServer.HangUp();
		var port = closed.Port;
		closed.Dispose(); // nothing listens there any more: refused

		var lines = await SerilogCapture.During(() => JellyfinAuthenticationService.GetPublicInfo($"http://alice:hunter2@127.0.0.1:{port}/jellyfin?api_key=abc123"));

		Assert.Contains(lines, l => l.Contains($"http://127.0.0.1:{port}", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, l => l.Contains("hunter2", StringComparison.Ordinal) || l.Contains("abc123", StringComparison.Ordinal));
	}

	[Fact]
	public async Task TheSignInsLogLine_ShowsNoUserInfoOrApiKey()
	{
		using var closed = LoopbackServer.HangUp();
		var port = closed.Port;
		closed.Dispose();
		var logger = new ListLogger();
		var service = new JellyfinAuthenticationService(new Mock<IJellyfinClient>().Object, new Mock<ITitleBarViewModel>().Object,
			new Mock<ILocalSettingsService>().Object, new Mock<INavigationService>().Object, logger);
		var url = $"http://alice:hunter2@127.0.0.1:{port}/?api_key=abc123";
		var server = new SavedServer { Id = "s1", LocalUrl = url, PublicUrl = url };

		var error = await service.Authenticate(server, "alice", "pw", remember: false);

		Assert.NotNull(error);
		Assert.Contains(logger.Lines, l => l.Contains($"http://127.0.0.1:{port}", StringComparison.Ordinal));
		Assert.DoesNotContain(logger.Lines, l => l.Contains("hunter2", StringComparison.Ordinal) || l.Contains("abc123", StringComparison.Ordinal));
	}

	/// <summary>Everything a test's ILogger was given: each message, its arguments and its exception.</summary>
	private sealed class ListLogger : ILogger<JellyfinAuthenticationService>
	{
		public List<string> Lines { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			var arguments = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
			Lines.Add($"{formatter(state, exception)} | {arguments} | {exception}");
		}
	}
}

/// <summary>Serilog's static logger, captured for one test at a time (it is global: the tests that use it run one by one).</summary>
[CollectionDefinition(nameof(SerilogCapture), DisableParallelization = true)]
public sealed class SerilogCapture : ILogEventSink
{
	private readonly List<string> _lines = [];

	public void Emit(LogEvent logEvent)
	{
		lock (_lines)
		{
			_lines.Add($"{logEvent.RenderMessage()} | {string.Join(" ", logEvent.Properties.Select(p => $"{p.Key}={p.Value}"))} | {logEvent.Exception}");
		}
	}

	public static async Task<List<string>> During(Func<Task> act)
	{
		var sink = new SerilogCapture();
		var before = Serilog.Log.Logger;
		Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
		try
		{
			await act();
		}
		finally
		{
			Serilog.Log.Logger = before;
		}

		lock (sink._lines)
		{
			return [.. sink._lines];
		}
	}
}

/// <summary>A server on 127.0.0.1 that answers one way: a plain HTTP status, a TLS handshake with a self-signed certificate, or a hang-up.</summary>
internal sealed class LoopbackServer : IDisposable
{
	private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
	private readonly CancellationTokenSource _stop = new();

	private LoopbackServer(Func<TcpClient, Task> answer)
	{
		_listener.Start();
		Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
		_ = Task.Run(async () =>
		{
			while (!_stop.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await _listener.AcceptTcpClientAsync(_stop.Token);
				}
				catch (Exception)
				{
					return;
				}

				_ = Task.Run(async () =>
				{
					using (client)
					{
						try
						{
							await answer(client);
						}
						catch (Exception)
						{
						}
					}
				});
			}
		});
	}

	public int Port { get; }

	public static LoopbackServer PlainHttp(int status) => new(async client =>
	{
		var stream = client.GetStream();
		var buffer = new byte[8192];
		_ = await stream.ReadAsync(buffer);
		await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Status\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
	});

	public static LoopbackServer SelfSignedTls()
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		var names = new SubjectAlternativeNameBuilder();
		names.AddIpAddress(IPAddress.Loopback);
		request.CertificateExtensions.Add(names.Build());
		using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
		var certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
		return new(async client =>
		{
			using var tls = new SslStream(client.GetStream());
			await tls.AuthenticateAsServerAsync(certificate);
		});
	}

	public static LoopbackServer HangUp() => new(async client =>
	{
		var buffer = new byte[16];
		_ = await client.GetStream().ReadAsync(buffer);
		client.Client.LingerState = new LingerOption(true, 0);
		client.Close();
	});

	public void Dispose()
	{
		_stop.Cancel();
		_listener.Stop();
	}
}
