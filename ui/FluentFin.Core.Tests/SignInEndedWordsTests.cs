using System.Net;
using FluentFin.Core.Admin;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (the 1.6.1 streams, KNOWN-ISSUES "Sign-in ended"): the server's restart and shut down, the library scan and
/// the delete notices had no words for an ended sign-in ("The server didn't accept the request"), and the Watched and
/// Favourite toggles sprang back with nothing said. Each now says why, the ended sign-in with Sign in again.
/// </summary>
[Collection(SignInStateCollection.Name)]
public sealed class SignInEndedWordsTests : IDisposable
{
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<string> _refused = [];

	public SignInEndedWordsTests()
	{
		SignInState.SignedIn();
		ItemUserData.Refused += OnRefused;
	}

	public void Dispose()
	{
		ItemUserData.Refused -= OnRefused;
		SignInState.SignedIn();
	}

	private void OnRefused(object? sender, string message) => _refused.Add(message);

	[Fact]
	public async Task ARestartRefused_AfterTheSignInEnded_SaysSo_NotThatTheServerRefused()
	{
		_client.Setup(x => x.Restart()).ReturnsAsync(() =>
		{
			SignInState.MarkEnded(); // as SignInWatch does, before the caller sees its 401
			return false;
		});
		var power = new ServerPower(_client.Object) { Delay = _ => Task.CompletedTask };

		await power.Restart("server-1", "Harness");

		Assert.Equal("Harness didn't restart", power.Notice!.Title);
		Assert.Contains("sign-in", power.Notice.Message, StringComparison.Ordinal);
		Assert.Contains("ended, so nothing changed. Sign in again with Switch user in the account menu. Then try again.", power.Notice.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("Select Sign in again", power.Notice.Message, StringComparison.Ordinal); // 1.7.5 review: no such button on these pages
		Assert.DoesNotContain("didn't accept", power.Notice.Message, StringComparison.Ordinal);
		Assert.False(power.IsBusy); // Sign in again, then Restart is offered again

		// signed in: the words it always had
		SignInState.SignedIn();
		_client.Setup(x => x.Restart()).ReturnsAsync(false);
		await power.Restart("server-1", "Harness");
		Assert.Equal("The server didn't accept the request. Nothing changed.", power.Notice!.Message);
	}

	[Fact]
	public async Task ASignInThatEndsWhileTheServerRestarts_StopsAsking_AndSaysWhy()
	{
		var polls = 0;
		_client.Setup(x => x.Restart()).ReturnsAsync(true);
		_client.Setup(x => x.GetSystemInfo()).ReturnsAsync(() =>
		{
			if (++polls == 2)
			{
				SignInState.MarkEnded();
			}

			return null;
		});
		var power = new ServerPower(_client.Object) { Delay = _ => Task.CompletedTask };

		await power.Restart("server-1", "Harness");

		Assert.Equal(2, polls); // not 90 polls (three minutes) of asking a server that refuses the sign-in
		Assert.Equal(ServerPowerState.NotBack, power.Notice!.State);
		Assert.EndsWith("Sign in again with Switch user in the account menu.", power.Notice.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("Select Sign in again", power.Notice.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("start it again on its computer", power.Notice.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AShutDownAScanAndADelete_RefusedAfterTheSignInEnded_SaySo()
	{
		SignInState.MarkEnded();
		_client.Setup(x => x.Shutdown()).ReturnsAsync(false);
		_client.Setup(x => x.DeleteUser(It.IsAny<UserDto>())).ReturnsAsync(false);
		var power = new ServerPower(_client.Object);

		await power.ShutDown("server-1", "Harness");
		var users = new UsersViewModel(_client.Object, Mock.Of<INavigationServiceCore>()) { Confirm = _ => Task.FromResult(true) };
		await users.DeleteUserCommand.ExecuteAsync(new UserDto { Name = "guest" });

		foreach (var notice in new[] { power.Notice!, users.Notice! })
		{
			Assert.Contains("ended, so nothing changed. Sign in again with Switch user in the account menu.", notice.Message, StringComparison.Ordinal);
			Assert.DoesNotContain("Select", notice.Message, StringComparison.Ordinal);
			Assert.DoesNotContain("didn't accept", notice.Message, StringComparison.Ordinal);
		}

		Assert.Equal("guest wasn't deleted", users.Notice!.Title);
		Assert.Equal(ServerPower.NotTaken("x"), users.Notice.Message);
		SignInState.SignedIn();
		Assert.Equal("The server didn't accept the request.", ServerPower.NotTaken("The server didn't accept the request."));
	}

	private static BaseItemDto Film() => new() { Id = Guid.NewGuid(), Name = "Heat", Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { Played = false, IsFavorite = false } };

	[Fact]
	public async Task AToggleThatSpringsBack_SaysWhy_TheSignInEnded()
	{
		var film = Film();
		_client.Setup(x => x.SetPlayed(film, true)).ReturnsAsync(() =>
		{
			SignInState.MarkEnded();
			return null;
		});

		Assert.False(await ItemUserData.SetPlayedAsync(_client.Object, film, true));

		var said = Assert.Single(_refused);
		Assert.StartsWith("Couldn't mark Heat as watched: your sign-in to ", said, StringComparison.Ordinal);
		Assert.EndsWith(" ended. Sign in again, then try again.", said, StringComparison.Ordinal);
		Assert.False(film.UserData!.Played); // it sprang back
	}

	[Fact]
	public async Task AToggleThatSpringsBack_SaysWhy_TheServersErrorOrTheConnection()
	{
		var film = Film();
		_client.Setup(x => x.SetIsFavorite(film, true)).ReturnsAsync(() =>
		{
			AnswerTrace.Current?.Answered(HttpStatusCode.InternalServerError, "application/json"); // as AnswerWatch notes it
			return null;
		});
		_client.Setup(x => x.SetIsFavorite(film, false)).ReturnsAsync((UserItemDataDto?)null);

		await ItemUserData.SetFavoriteAsync(_client.Object, film, true);
		await ItemUserData.SetFavoriteAsync(_client.Object, film, false);

		Assert.Equal(2, _refused.Count);
		Assert.StartsWith("Couldn't add Heat to your favourites: ", _refused[0], StringComparison.Ordinal);
		Assert.EndsWith(" had a problem answering just now. Try again; if it keeps happening, its owner may need to know.", _refused[0], StringComparison.Ordinal);
		Assert.StartsWith("Couldn't remove Heat from your favourites: Nixie couldn't reach ", _refused[1], StringComparison.Ordinal);
		Assert.EndsWith(" just now. Check this PC's connection, then try again.", _refused[1], StringComparison.Ordinal);

		// taken: nothing to say
		_client.Setup(x => x.SetIsFavorite(film, true)).ReturnsAsync(new UserItemDataDto { IsFavorite = true });
		Assert.True(await ItemUserData.SetFavoriteAsync(_client.Object, film, true));
		Assert.Equal(2, _refused.Count);
	}
}
