using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin): a fake Jellyfin on localhost (HttpListener, a fake token) for the administration and item-dialog
/// tests: the app's real JellyfinClient signs in to it, and every request is recorded with its body, so a test can say which
/// requests a button sent to the server and what they carried. Endpoints answer what <see cref="Respond"/> set; anything else
/// is a 404. Never a real server.
/// </summary>
internal sealed class AdminFakeServer : IDisposable
{
	public const string FakeToken = "fake-admin-token-0123456789";
	public static readonly Guid AdminId = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");

	private static readonly HashSet<string> SignInPaths = new(StringComparer.OrdinalIgnoreCase) { "/Sessions/Capabilities/Full", "/socket" };

	private readonly HttpListener _listener = new();
	private readonly ConcurrentDictionary<string, (int Status, string Body)> _responses = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentQueue<AdminRequest> _requests = new();
	private readonly ConcurrentQueue<(string Method, string Prefix, Func<AdminRequest, (int Status, string Body)> Handle)> _handlers = new();
	private Task? _loop;

	public string BaseUrl { get; private set; } = "";

	public IReadOnlyList<AdminRequest> Requests => [.. _requests];

	/// <summary>The requests that change something on the server: every one but GET (and the sign-in's capabilities).</summary>
	public IReadOnlyList<AdminRequest> Changes => [.. _requests.Where(x => !x.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !SignInPaths.Contains(x.Path))];

	/// <summary>The answer to <paramref name="method"/> on <paramref name="path"/> (exact path, no query).</summary>
	public void Respond(string method, string path, int status, string body = "") => _responses[$"{method.ToUpperInvariant()} {path}"] = (status, body);

	/// <summary>
	/// An answer worked out per request, for every path starting with <paramref name="pathPrefix"/> (a server with state, such as
	/// one that renumbers images after a delete, as Jellyfin does). The latest handler that matches wins over Respond.
	/// </summary>
	public void Handle(string method, string pathPrefix, Func<AdminRequest, (int Status, string Body)> handle) =>
		_handlers.Enqueue((method.ToUpperInvariant(), pathPrefix, handle));

	public void Start()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		BaseUrl = $"http://localhost:{port}";
		_listener.Prefixes.Add(BaseUrl + "/");
		_listener.Start();
		Respond("POST", "/Sessions/Capabilities/Full", 204);
		Respond("GET", "/Plugins", 200, "[]");
		Respond("GET", "/UserViews", 200, """{"Items":[],"TotalRecordCount":0}""");
		_loop = Task.Run(Serve);
	}

	/// <summary>The app's own client, signed in to this server as an administrator with the fake token.</summary>
	public async Task<JellyfinClient> SignIn()
	{
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Mpv);
		var client = new JellyfinClient(NullLogger<JellyfinClient>.Instance, Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(),
			Mock.Of<IDeviceProfileFactory>(), settings.Object, TestHomeCache.Unused());
		await client.Initialize(BaseUrl, new AuthenticationResult
		{
			AccessToken = FakeToken,
			ServerId = "fake-admin-server",
			User = new UserDto { Id = AdminId, Name = "admin", Policy = new UserPolicy { IsAdministrator = true } },
		});
		return client;
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
			catch (Exception) when (!_listener.IsListening)
			{
				return;
			}
			catch (HttpListenerException)
			{
				return;
			}

			var url = context.Request.Url!;
			string body;
			using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
			{
				body = await reader.ReadToEndAsync();
			}

			var method = context.Request.HttpMethod.ToUpperInvariant();
			var signedIn = (context.Request.Headers["Authorization"] ?? "").Contains($"Token=\"{FakeToken}\"", StringComparison.Ordinal);
			var request = new AdminRequest(method, url.AbsolutePath, url.ToString(), System.Web.HttpUtility.ParseQueryString(url.Query), body, signedIn);
			_requests.Enqueue(request);
			var handler = _handlers.Reverse().FirstOrDefault(x => x.Method == method && url.AbsolutePath.StartsWith(x.Prefix, StringComparison.OrdinalIgnoreCase));
			var (status, answer) = handler.Handle is { } handle ? handle(request)
				: _responses.TryGetValue($"{method} {url.AbsolutePath}", out var response) ? response : (404, "");
			try
			{
				context.Response.StatusCode = status;
				if (answer.Length > 0)
				{
					var bytes = Encoding.UTF8.GetBytes(answer);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = bytes.Length;
					await context.Response.OutputStream.WriteAsync(bytes);
				}
				context.Response.Close();
			}
			catch (Exception)
			{
				// the client went away
			}
		}
	}

	public void Dispose()
	{
		try
		{
			_listener.Stop();
			_listener.Close();
		}
		catch (ObjectDisposedException)
		{
		}

		_loop?.Wait(TimeSpan.FromSeconds(2));
	}
}

/// <param name="SignedIn">It carried the signed-in user's token (in the Authorization header).</param>
internal sealed record AdminRequest(string Method, string Path, string Url, NameValueCollection Query, string Body, bool SignedIn)
{
	public JsonElement Json => JsonDocument.Parse(Body).RootElement;

	public override string ToString() => $"{Method} {Path}{(Body.Length > 0 ? " " + Body : "")}";
}
