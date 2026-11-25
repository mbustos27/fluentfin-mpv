using System.Net;
using System.Text;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (stream B's review of 1.7.3): whose words a failed load gets when the server's answer never reached the page
/// as a throw. A captive portal answering 200 with HTML said "Nixie couldn't show this page… restart Nixie" (the connection);
/// a 5xx the client swallowed into null said "Check this PC's connection" (the server's own fault). Through the client's own
/// handlers (<see cref="JellyfinClient.WatchedHttpClient(Func{string}, Func{string}, Microsoft.Extensions.Logging.ILogger, HttpMessageHandler?)"/>)
/// and the SDK, as a page asks.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class LoadStateAnswerTests : IDisposable
{
	private const string Connection = "Nixie couldn't get it from Home server just now. Check this PC's connection, then select Try again.";
	private const string ServersOwn = "Home server had a problem answering just now. Select Try again; if it keeps happening, its owner may need to know.";
	private const string NixiesOwn = "Nixie couldn't show this page. Select Try again; if it keeps happening, restart Nixie, or use Report a problem in Settings.";

	public LoadStateAnswerTests() => SessionInfo.ServerName = "Home server";

	public void Dispose() => SessionInfo.ServerName = null!;

	/// <summary>The server the page asks, answering every request with <paramref name="answer"/>.</summary>
	private static JellyfinApiClient Server(Func<HttpRequestMessage, HttpResponseMessage> answer)
	{
		var settings = new JellyfinSdkSettings();
		settings.Initialize("Nixie", "1.7.5", "PC", "0123456789abcdef0123456789abcdef");
		settings.SetServerUrl("http://jf.test");
		settings.SetAccessToken("token");
		var http = JellyfinClient.WatchedHttpClient(() => "http://jf.test", () => "token", NullLogger.Instance, new Answering(answer));
		return new JellyfinApiClient(new JellyfinRequestAdapter(new JellyfinAuthenticationProvider(settings), settings, http));
	}

	private static HttpResponseMessage Answer(HttpStatusCode status, string mediaType, string body) =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

	private const string PortalPage = "<!DOCTYPE html><html><body>Sign in to the hotel Wi-Fi</body></html>";

	public static TheoryData<string, string> PortalAnswers => new()
	{
		{ "text/html (Kiota: no reader for it)", "text/html" },
		{ "labelled JSON (a JsonException reading it)", "application/json" },
	};

	[Theory]
	[MemberData(nameof(PortalAnswers))]
	public async Task APortalsPage_ThatReachesTheLoad_IsTheConnection_NotNixiesOwn(string what, string mediaType)
	{
		var server = Server(_ => Answer(HttpStatusCode.OK, mediaType, PortalPage));
		var state = new LoadState("this movie");

		await state.RunAsync(async () =>
		{
			await server.System.Info.Public.GetAsync();
			return LoadOutcome.Loaded;
		});

		Assert.True(state.IsFailed, what);
		Assert.Equal(Connection, state.Message);
	}

	[Theory]
	[MemberData(nameof(PortalAnswers))]
	public async Task APortalsPage_ThrownOutsideALoad_IsTheConnectionToo(string what, string mediaType)
	{
		var server = Server(_ => Answer(HttpStatusCode.OK, mediaType, PortalPage));

		var thrown = await Record.ExceptionAsync(() => server.System.Info.Public.GetAsync());

		Assert.NotNull(thrown);
		Assert.True(LoadMessages.CauseOf(thrown!) == LoadMessages.Cause.Server, $"{thrown!.GetType()}: {thrown.Message} / {thrown.InnerException?.GetType()}: {thrown.InnerException?.Message}");
		Assert.False(string.IsNullOrEmpty(what));
	}

	[Fact]
	public async Task AFileOfNixiesOwnThatIsntJson_StaysNixiesOwn()
	{
		var state = new LoadState("your collections");

		await state.RunAsync(() => Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<LoadOutcome>(PortalPage)));

		Assert.Equal(NixiesOwn, state.Message);
	}

	/// <summary>The page as most pages are: the client swallows the error and answers null, and the page says it couldn't load.</summary>
	private static async Task<LoadOutcome> PageThatGetsNull(JellyfinApiClient server)
	{
		Jellyfin.Sdk.Generated.Models.PublicSystemInfo? info;
		try
		{
			info = await server.System.Info.Public.GetAsync();
		}
		catch (Exception)
		{
			info = null; // as JellyfinClient does: logged, and null
		}

		return info is null ? LoadMessages.CouldNotLoad("this movie") : LoadOutcome.Loaded;
	}

	[Theory]
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.BadGateway)]
	public async Task A5xxTheClientSwallowed_SaysTheServersWords(HttpStatusCode status)
	{
		var state = new LoadState("this movie");

		await state.RunAsync(() => PageThatGetsNull(Server(_ => Answer(status, "application/json", "{}"))));

		Assert.True(state.IsFailed);
		Assert.Equal("Couldn't load this movie", state.Title);
		Assert.Equal(ServersOwn, state.Message);
		Assert.DoesNotContain("connection", state.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 1.7.5 (the review): a captive portal's 511 ("Network Authentication Required": sign in to this Wi-Fi first) is a 5xx, but
	/// not the server's: swallowed into null or thrown to the load, it is the connection's words, never "had a problem".
	/// </summary>
	[Fact]
	public async Task APortals511_IsTheConnection_SwallowedOrThrown()
	{
		var swallowed = new LoadState("this movie");
		await swallowed.RunAsync(() => PageThatGetsNull(Server(_ => Answer((HttpStatusCode)511, "text/plain", "Sign in to the Wi-Fi"))));
		Assert.Equal(Connection, swallowed.Message);

		var thrown = new LoadState("this movie");
		await thrown.RunAsync(() => throw new Microsoft.Kiota.Abstractions.ApiException("x") { ResponseStatusCode = 511 });
		Assert.Equal(Connection, thrown.Message);
	}

	[Fact]
	public async Task NoAnswer_OrAPortalsPage_TheClientSwallowed_IsTheConnection()
	{
		var refused = new LoadState("this movie");
		await refused.RunAsync(() => PageThatGetsNull(Server(_ => throw new HttpRequestException("No connection could be made"))));
		Assert.Equal(Connection, refused.Message);

		var portal = new LoadState("this movie");
		await portal.RunAsync(() => PageThatGetsNull(Server(_ => Answer(HttpStatusCode.OK, "text/html", PortalPage))));
		Assert.Equal(Connection, portal.Message);

		// a 5xx and a request that got no answer in one load: the connection first (what the user can try here)
		var calls = 0;
		var both = new LoadState("this movie");
		var server = Server(_ => Interlocked.Increment(ref calls) == 1 ? Answer(HttpStatusCode.ServiceUnavailable, "text/plain", "busy") : throw new HttpRequestException("reset"));
		await both.RunAsync(async () =>
		{
			await PageThatGetsNull(server);
			return await PageThatGetsNull(server);
		});
		Assert.Equal(Connection, both.Message);
	}

	[Fact]
	public async Task TwoLoadsAtOnce_EachHasItsOwnAnswers()
	{
		var gate = new TaskCompletionSource();
		var failing = Server(_ => Answer(HttpStatusCode.InternalServerError, "application/json", "{}"));
		var down = Server(_ => throw new HttpRequestException("No connection could be made"));
		var first = new LoadState("this movie");
		var second = new LoadState("this show");

		var one = first.RunAsync(async () =>
		{
			_ = await PageThatGetsNull(failing);
			await gate.Task;
			return LoadMessages.CouldNotLoad("this movie");
		});
		var two = second.RunAsync(async () =>
		{
			await gate.Task;
			return await PageThatGetsNull(down);
		});
		gate.SetResult();
		await Task.WhenAll(one, two);

		Assert.Equal(ServersOwn, first.Message);
		Assert.Equal(Connection, second.Message);
		// and outside any load (an action's notice): the words it always had
		Assert.Equal(Connection, LoadMessages.CheckServer());
	}

	private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(answer(request));
	}
}
