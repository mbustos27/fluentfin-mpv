using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DeviceId;
using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>
/// Nixie (1.2.0): the client every sign-in uses (the server page's check, Quick Connect, a password; all through
/// JellyfinAuthenticationService's one client) tells the server Client="Nixie", with the same device id Seaglass sent (the
/// Windows device's), so a server that already knows this PC from Seaglass sees the same device under the new name. A
/// fake server on localhost records the header; no real server is ever asked.
/// </summary>
public sealed class SignInClientTests : IDisposable
{
	private readonly HttpListener _listener = new();
	private readonly List<string?> _authorization = [];
	private readonly string _baseUrl;
	private readonly Task _loop;

	public SignInClientTests()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		_baseUrl = $"http://localhost:{port}";
		_listener.Prefixes.Add(_baseUrl + "/");
		_listener.Start();
		_loop = Task.Run(Serve);
	}

	private async Task Serve()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception)
			{
				return;
			}

			lock (_authorization)
			{
				_authorization.Add(context.Request.Headers["Authorization"] ?? context.Request.Headers["X-Emby-Authorization"]);
			}

			var body = Encoding.UTF8.GetBytes("""{"ServerName":"fake","Version":"10.11.0","Id":"fake-server","StartupWizardCompleted":true}""");
			context.Response.StatusCode = 200;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = body.Length;
			await context.Response.OutputStream.WriteAsync(body);
			context.Response.Close();
		}
	}

	public void Dispose()
	{
		_listener.Stop();
		_listener.Close();
		_loop.Wait(TimeSpan.FromSeconds(2));
	}

	[Fact]
	public async Task TheSignInClient_SaysNixie_OnTheSameDevice()
	{
		var (info, error) = await JellyfinAuthenticationService.GetPublicInfo(_baseUrl);

		Assert.Null(error);
		Assert.Equal("fake", info?.ServerName);
		string? header;
		lock (_authorization)
		{
			header = Assert.Single(_authorization);
		}

		Assert.NotNull(header);
		var fields = Regex.Matches(header, "(\\w+)=\"([^\"]*)\"").ToDictionary(m => m.Groups[1].Value, m => Uri.UnescapeDataString(m.Groups[2].Value));
		Assert.Equal("Nixie", fields["Client"]);
		Assert.Equal(new DeviceIdBuilder().OnWindows(windows => windows.AddWindowsDeviceId()).ToString(), fields["DeviceId"]);
		Assert.Equal(Environment.MachineName, fields["Device"]);
		Assert.Equal(Core.AppVersion.Current, fields["Version"]);
		Assert.DoesNotContain("Seaglass", header);
	}
}
