using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeviceId;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.Contracts.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// Nixie (1.2.0): the client every sign-in uses (the server page's check, Quick Connect, a password, and the automatic
/// sign-in of a saved user at every start; all through JellyfinAuthenticationService's one client) tells the server
/// Client="Nixie", with the same device id Seaglass sent (the Windows device's). A saved sign-in is the user name and a
/// DPAPI-protected password, not a token: the automatic sign-in sends them again, so the server sees the same device sign in
/// as Nixie and gives it a new token. A fake server on localhost records the requests; no real server is ever asked.
/// </summary>
public sealed class SignInClientTests : IDisposable
{
	private readonly HttpListener _listener = new();
	private readonly List<(string Path, string? Authorization, string Body)> _requests = [];
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

			var path = context.Request.Url!.AbsolutePath;
			using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
			var body = await reader.ReadToEndAsync();
			lock (_requests)
			{
				_requests.Add((path, context.Request.Headers["Authorization"] ?? context.Request.Headers["X-Emby-Authorization"], body));
			}

			var answer = path.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase)
				? """{"AccessToken":"a-new-token-for-nixie","ServerId":"fake-server","User":{"Id":"aaaaaaaa000000000000000000000003","Name":"alice"}}"""
				: """{"ServerName":"fake","Version":"10.11.0","Id":"fake-server","StartupWizardCompleted":true}""";
			var bytes = Encoding.UTF8.GetBytes(answer);
			context.Response.StatusCode = 200;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = bytes.Length;
			await context.Response.OutputStream.WriteAsync(bytes);
			context.Response.Close();
		}
	}

	public void Dispose()
	{
		_listener.Stop();
		_listener.Close();
		_loop.Wait(TimeSpan.FromSeconds(2));
	}

	private static Dictionary<string, string> Fields(string header) =>
		Regex.Matches(header, "(\\w+)=\"([^\"]*)\"").ToDictionary(m => m.Groups[1].Value, m => Uri.UnescapeDataString(m.Groups[2].Value));

	private static string WindowsDeviceId => new DeviceIdBuilder().OnWindows(windows => windows.AddWindowsDeviceId()).ToString();

	[Fact]
	public async Task TheSignInClient_SaysNixie_OnTheSameDevice()
	{
		var (info, error) = await JellyfinAuthenticationService.GetPublicInfo(_baseUrl);

		Assert.Null(error);
		Assert.Equal("fake", info?.ServerName);
		string? header;
		lock (_requests)
		{
			header = Assert.Single(_requests).Authorization;
		}

		Assert.NotNull(header);
		var fields = Fields(header);
		Assert.Equal("Nixie", fields["Client"]);
		Assert.Equal(WindowsDeviceId, fields["DeviceId"]);
		Assert.Equal(Environment.MachineName, fields["Device"]);
		Assert.Equal(AppVersion.Current, fields["Version"]);
		Assert.DoesNotContain("Seaglass", header);
	}

	[Fact]
	public async Task ASavedSignIn_SignsInAgain_WithItsUserNameAndPassword_AsNixie_OnTheSameDevice()
	{
		// a sign-in Seaglass saved (and the data folder copy brought over with its entropy): a user name and a protected password
		var entropy = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
		var settings = new Mock<ILocalSettingsService>();
		settings.Setup(x => x.GetEntropyBytes()).Returns(entropy);
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Initialize(It.IsAny<string>(), It.IsAny<Jellyfin.Sdk.Generated.Models.AuthenticationResult>())).Returns(Task.CompletedTask);
		var titleBar = new Mock<ITitleBarViewModel>();
		titleBar.SetupAllProperties();
		var service = new JellyfinAuthenticationService(client.Object, titleBar.Object, settings.Object, Mock.Of<INavigationService>(),
			NullLogger<JellyfinAuthenticationService>.Instance);
		var server = new SavedServer { DisplayName = "home", PublicUrl = _baseUrl, LocalUrl = _baseUrl, Id = "fake-server" };
		var user = new SavedUser { Username = "alice", Password = "s3cret".Protect(entropy) };

		try
		{
			Assert.True(await service.Authenticate(server, user));
		}
		finally
		{
			SessionInfo.CurrentUserCrendentials = null!; // static: no other test sees this user
		}

		(string Path, string? Authorization, string Body) request;
		lock (_requests)
		{
			request = Assert.Single(_requests, r => r.Path.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase));
		}

		using (var body = JsonDocument.Parse(request.Body))
		{
			Assert.Equal("alice", body.RootElement.GetProperty("Username").GetString());
			Assert.Equal("s3cret", body.RootElement.GetProperty("Pw").GetString());
		}

		Assert.NotNull(request.Authorization);
		var fields = Fields(request.Authorization);
		Assert.Equal("Nixie", fields["Client"]);
		Assert.Equal(WindowsDeviceId, fields["DeviceId"]);
		Assert.Equal(Environment.MachineName, fields["Device"]);
		Assert.False(fields.ContainsKey("Token")); // nothing saved is sent but the user name and password
		// the session the server made for Nixie is the one the app goes on with
		client.Verify(x => x.Initialize(_baseUrl, It.Is<Jellyfin.Sdk.Generated.Models.AuthenticationResult>(a => a.AccessToken == "a-new-token-for-nixie")), Times.Once);
	}
}
