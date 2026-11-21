using System.Collections.Concurrent;
using System.Net;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>1.5.1 fixes (BH-150-M2): tests that end the sign-in (<see cref="SignInState"/>, static) run alone, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SignInStateCollection
{
	public const string Name = "1.5.1 fixes: SignInState (static), alone";
}

/// <summary>
/// 1.5.1 fixes (BH-150-M2): a token the server stops accepting asks for a new sign-in, instead of "Check that the server is
/// running" with a Try again that can never work. The client's watch (<see cref="SignInWatch"/>) ends the sign-in only when
/// the server refuses the token twice (a 401, then a 401 to <c>GET /Users/Me</c>), before the caller sees its 401; every page's
/// load state then says so, with Sign in again.
/// </summary>
[Collection(SignInStateCollection.Name)]
public sealed class SignInEndedTests : IDisposable
{
	private const string Token = "token-0123456789abcdef";

	public SignInEndedTests() => SignInState.SignedIn();

	public void Dispose()
	{
		SignInState.SignedIn();
		SignInState.SignInAgain = null;
	}

	/// <summary>A server: <paramref name="answer"/> per request path; every path asked, in order.</summary>
	private sealed class Stub(Func<string, HttpStatusCode> answer, TimeSpan? delay = null) : HttpMessageHandler
	{
		public ConcurrentQueue<(string Path, string? Authorization)> Asked { get; } = new();

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var path = request.RequestUri!.AbsolutePath;
			Asked.Enqueue((path, request.Headers.TryGetValues("Authorization", out var values) ? values.First() : null));
			await Task.Delay(delay ?? TimeSpan.FromMilliseconds(1), cancellationToken);
			return new HttpResponseMessage(answer(path));
		}
	}

	private static HttpClient Client(HttpMessageHandler server, Func<string>? token = null) =>
		new(new SignInWatch(() => "http://server/jellyfin/", token ?? (() => Token), NullLogger.Instance) { InnerHandler = server });

	private static HttpRequestMessage Get(string path, string token = Token)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, "http://server/jellyfin" + path);
		request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Client=\"Nixie\", Device=\"PC\", DeviceId=\"d\", Version=\"1.5.1\", Token=\"{token}\"");
		return request;
	}

	[Fact]
	public async Task A401_TheServerRefusesUsersMeToo_EndsTheSignIn_BeforeTheCallerSeesIt()
	{
		var server = new Stub(_ => HttpStatusCode.Unauthorized);
		using var client = Client(server);

		using var answer = await client.SendAsync(Get("/Items/abc"));

		Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);   // the caller still gets its 401
		Assert.True(SignInState.Ended);
		Assert.Equal(["/jellyfin/Items/abc", "/jellyfin/Users/Me"], server.Asked.Select(x => x.Path));   // a sub-path server: under it
		Assert.Contains($"Token=\"{Token}\"", server.Asked.Last().Authorization);
	}

	[Fact]
	public async Task A401_UsersMeAnswers_EndsNothing()
	{
		// Jellyfin answers 401 to Quick Connect's Authorize while Quick Connect is off on the server, with a good token
		var server = new Stub(path => path.EndsWith("/Users/Me", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
		using var client = Client(server);

		using var answer = await client.SendAsync(Get("/QuickConnect/Authorize"));

		Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
		Assert.False(SignInState.Ended);
	}

	[Theory]
	[InlineData(HttpStatusCode.OK)]
	[InlineData(HttpStatusCode.Forbidden)]          // an administrator's page for an ordinary user: the sign-in is fine
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.NotFound)]
	public async Task AnythingButA401_IsNotLookedAt(HttpStatusCode status)
	{
		var server = new Stub(_ => status);
		using var client = Client(server);

		using var _ = await client.SendAsync(Get("/Plugins"));

		Assert.Single(server.Asked);
		Assert.False(SignInState.Ended);
	}

	[Fact]
	public async Task A401_ToAnOlderTokenOrNone_IsNotLookedAt()
	{
		// a request still on its way from before a new sign-in, or one that carries no token (the server page's)
		var server = new Stub(_ => HttpStatusCode.Unauthorized);
		using var client = Client(server, token: () => "the-new-token");

		using var older = await client.SendAsync(Get("/Items/abc", token: Token));
		using var none = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://server/jellyfin/System/Info"));

		Assert.Equal(2, server.Asked.Count);
		Assert.False(SignInState.Ended);
	}

	[Fact]
	public async Task ABurstOf401s_SharesOneCheck()
	{
		// Home asks its rows together: one question to the server, not one per row
		var server = new Stub(_ => HttpStatusCode.Unauthorized, TimeSpan.FromMilliseconds(50));
		using var client = Client(server);

		var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => client.SendAsync(Get($"/Items/{i}"))));

		Assert.All(answers, a => Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode));
		Assert.True(SignInState.Ended);
		Assert.Single(server.Asked, x => x.Path.EndsWith("/Users/Me", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ACheckWithNoAnswer_EndsNothing()
	{
		var server = new ThrowsOnUsersMe();
		using var client = Client(server);

		using var answer = await client.SendAsync(Get("/Items/abc"));

		Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
		Assert.False(SignInState.Ended);
	}

	private sealed class ThrowsOnUsersMe : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			request.RequestUri!.AbsolutePath.EndsWith("/Users/Me", StringComparison.Ordinal)
				? throw new HttpRequestException("the server went away")
				: Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
	}

	[Fact]
	public async Task OnceEnded_No401IsCheckedAgain_UntilASignIn()
	{
		var server = new Stub(_ => HttpStatusCode.Unauthorized);
		using var client = Client(server);
		using var first = await client.SendAsync(Get("/Items/1"));
		var asked = server.Asked.Count;

		using var second = await client.SendAsync(Get("/Items/2"));

		Assert.Equal(asked + 1, server.Asked.Count);
		SignInState.SignedIn();
		Assert.False(SignInState.Ended);
	}

	// ---- the real client, against a fake Jellyfin

	[Fact]
	public async Task TheAppsClient_ATokenTheServerStopsTaking_EndsTheSignIn_AndANewSignInClearsIt()
	{
		using var server = new FakeJellyfin();
		server.Start();
		var item = Guid.NewGuid();
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(),
			Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		var signIn = new AuthenticationResult { AccessToken = Token, ServerId = "fake-server", User = new UserDto { Id = Guid.NewGuid(), Name = "alice" } };
		await client.Initialize(server.BaseUrl, signIn);
		try
		{
			server.Respond($"/Items/{item}", 401, "");
			server.Respond("/Users/Me", 401, "");

			Assert.Null(await client.GetItem(item));

			Assert.True(SignInState.Ended);
			var check = Assert.Single(server.ApiRequests, x => x.Path.Equals("/Users/Me", StringComparison.OrdinalIgnoreCase));
			Assert.Contains($"Token=\"{Token}\"", check.Authorization);

			await client.Initialize(server.BaseUrl, signIn);
			Assert.False(SignInState.Ended);
		}
		finally
		{
			client.StopWebSocket();
		}
	}

	// ---- what every page shows (LoadState)

	[Fact]
	public async Task AFailedPage_OnceTheSignInEnded_AsksToSignInAgain()
	{
		SessionInfo.ServerName = "Home server";
		try
		{
			var state = new LoadState("this library");
			await state.RunAsync(() =>
			{
				SignInState.MarkEnded();   // as the watch does, before the page's request answers
				return Task.FromResult(LoadMessages.CouldNotLoad("this library"));
			});

			Assert.True(state.IsFailed);
			Assert.True(state.HasMessage);
			Assert.True(state.SignInNeeded);
			Assert.Equal("Your sign-in to Home server ended", state.Title);
			Assert.Contains("no longer accepts this sign-in", state.Message);
			Assert.DoesNotContain("Check that the server is running", state.Message);
			Assert.Equal("Sign in again", state.RetryText);
		}
		finally
		{
			SessionInfo.ServerName = null!;
		}
	}

	[Fact]
	public async Task AThrowingLoad_OnceEnded_AsksToSignInAgain()
	{
		var state = new LoadState("Home");
		await state.RunAsync(() =>
		{
			SignInState.MarkEnded();
			throw new HttpRequestException("401");
		});

		Assert.True(state.IsFailed);
		Assert.Equal("Sign in again", state.RetryText);
	}

	[Fact]
	public async Task APartlyLoadedHome_OnceEnded_KeepsWhatItShows_WithTheSignInAbove()
	{
		var state = new LoadState("Home");
		await state.RunAsync(() =>
		{
			SignInState.MarkEnded();
			return Task.FromResult(LoadOutcome.Partly("Some of Home couldn't be loaded", LoadMessages.CheckServer()));
		});

		Assert.True(state.IsLoaded);
		Assert.True(state.HasProblem);
		Assert.False(state.HasMessage);
		Assert.StartsWith("Your sign-in to", state.Title);
		Assert.Equal("Sign in again", state.RetryText);
	}

	[Fact]
	public async Task AnEmptyPage_OnceEnded_IsNotTakenAsTheServersAnswer()
	{
		var state = new LoadState();
		await state.RunAsync(() =>
		{
			SignInState.MarkEnded();
			return Task.FromResult(LoadOutcome.Empty("Nothing here", "This library is empty."));
		});

		Assert.True(state.IsFailed);
		Assert.True(state.SignInNeeded);
	}

	[Fact]
	public async Task AFullyLoadedPage_IsLeftAlone()
	{
		var state = new LoadState();
		await state.RunAsync(() =>
		{
			SignInState.MarkEnded();
			return Task.FromResult(LoadOutcome.Loaded);
		});

		Assert.True(state.IsLoaded);
		Assert.False(state.HasProblem);
		Assert.False(state.SignInNeeded);
	}

	[Fact]
	public async Task WhileTheSignInIsGood_AFailureStillSaysCheckTheServer_AndTryAgainLoadsAgain()
	{
		var loads = 0;
		var state = new LoadState("this library");
		await state.RunAsync(() =>
		{
			loads++;
			return Task.FromResult(LoadMessages.CouldNotLoad("this library"));
		});

		Assert.False(state.SignInNeeded);
		Assert.Contains("Check that the server is running", state.Message);
		Assert.Equal("Try again", state.RetryText);
		await state.RetryCommand.ExecuteAsync(null);
		Assert.Equal(2, loads);
	}

	[Fact]
	public async Task SignInAgain_SignedInInPlace_LoadsThePageAgain()
	{
		var loads = 0;
		var signIns = 0;
		SignInState.SignInAgain = () =>
		{
			signIns++;
			SignInState.SignedIn();   // the saved sign-in worked
			return Task.CompletedTask;
		};
		var state = new LoadState();
		await state.RunAsync(() =>
		{
			if (++loads == 1)
			{
				SignInState.MarkEnded();
				return Task.FromResult(LoadMessages.CouldNotLoad("Home"));
			}

			return Task.FromResult(LoadOutcome.Loaded);
		});
		Assert.Equal("Sign in again", state.RetryText);

		await state.RetryCommand.ExecuteAsync(null);

		Assert.Equal(1, signIns);
		Assert.Equal(2, loads);
		Assert.True(state.IsLoaded);
		Assert.False(state.SignInNeeded);
		Assert.Equal("Try again", state.RetryText);
	}

	[Fact]
	public async Task SignInAgain_ToTheSignInPage_DoesNotLoadThePageAgain()
	{
		var loads = 0;
		var signIns = 0;
		SignInState.SignInAgain = () =>
		{
			signIns++;   // went to the sign-in page: still ended until the user signs in there
			return Task.CompletedTask;
		};
		var state = new LoadState();
		await state.RunAsync(() =>
		{
			loads++;
			SignInState.MarkEnded();
			return Task.FromResult(LoadMessages.CouldNotLoad("Home"));
		});

		await state.RetryCommand.ExecuteAsync(null);

		Assert.Equal(1, signIns);
		Assert.Equal(1, loads);
	}

	[Fact]
	public async Task SignedInByAnotherPage_ThisPagesButtonLoadsAgain_WithoutAnotherSignIn()
	{
		var signIns = 0;
		SignInState.SignInAgain = () =>
		{
			signIns++;
			return Task.CompletedTask;
		};
		var loads = 0;
		var state = new LoadState();
		await state.RunAsync(() =>
		{
			if (++loads == 1)
			{
				SignInState.MarkEnded();
				return Task.FromResult(LoadMessages.CouldNotLoad("Home"));
			}

			return Task.FromResult(LoadOutcome.Loaded);
		});
		SignInState.SignedIn();

		await state.RetryCommand.ExecuteAsync(null);

		Assert.Equal(0, signIns);
		Assert.Equal(2, loads);
	}
}
