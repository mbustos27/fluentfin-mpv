using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Moq;
using static FluentFin.Core.Tests.Journal.Events;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (BH-151-m8, the rest): the sign-in ended mid-playback, so the server refused the backend's progress and Stopped
/// reports (auth_failed, then reported: failed). 1.6.1 said so in the player with Sign in again, but that playback's place and
/// watched state were lost. After the new sign-in the place the player last saw is written, by the server's rules, as the
/// item's user data (the backend stays the one reporter).
/// </summary>
[Collection(SignInStateCollection.Name)]
public sealed class RefusedPlayPlaceTests : IDisposable
{
	private const string Server = "https://jellyfin.example";
	private static readonly Guid User = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
	private static readonly long Runtime = 45 * Minute;

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Subject<BridgeEvent> _events = new();
	private readonly ManualClock _clock = new();
	private readonly RecentPlays _plays;
	private readonly List<(Guid Id, bool Played, long Position, DateTimeOffset At)> _placed = [];
	private DateTimeOffset? _lastPlayedOnServer;
	private Guid _user = User;

	public RefusedPlayPlaceTests()
	{
		SignInState.SignedIn();
		_client.SetupGet(x => x.BaseUrl).Returns(Server);
		_client.SetupGet(x => x.UserId).Returns(() => _user);
		_client.Setup(x => x.GetItem(It.IsAny<Guid>())).ReturnsAsync((Guid id) => new BaseItemDto
		{
			Id = id, RunTimeTicks = Runtime, UserData = new UserItemDataDto { Played = false, PlaybackPositionTicks = 3 * Minute, LastPlayedDate = _lastPlayedOnServer },
		});
		_client.Setup(x => x.SetPlace(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>()))
			.ReturnsAsync((Guid id, bool played, long position, DateTimeOffset at) =>
			{
				lock (_placed)
				{
					_placed.Add((id, played, position, at));
				}

				return new UserItemDataDto { Played = played, PlaybackPositionTicks = position, LastPlayedDate = at };
			});
		_plays = new RecentPlays(_events, _client.Object, _clock, ui: null);
	}

	public void Dispose()
	{
		_plays.Dispose();
		SignInState.SignedIn();
	}

	private static BridgeEvent Refused() => Of(Bridge.Events.Error, new { code = "auth_failed", message = "the server refused the progress report (401)", fatal = false });

	/// <summary>A play of <paramref name="item"/> to <paramref name="to"/>, its reports refused midway (or not), and its Stopped's outcome.</summary>
	private void Play(Guid item, string session, long to, bool refused, string outcome = "failed", string reason = "stopped")
	{
		_events.OnNext(State("playing", item, session, Runtime));
		_events.OnNext(Position(5 * Minute, Runtime));
		if (refused)
		{
			_events.OnNext(Refused());
		}

		_events.OnNext(Position(to, Runtime));
		_events.OnNext(Ended(item, reason));
		_events.OnNext(Reported(item, session, outcome));
	}

	private async Task SignInAgain()
	{
		SignInState.MarkEnded();
		SignInState.SignedIn();
		await _plays.SendingOwed;
	}

	[Fact]
	public async Task AfterSignInAgain_ThePlaysPlaceIsSent_Once()
	{
		var item = Guid.NewGuid();
		Play(item, "ps-1", 20 * Minute, refused: true);
		var stopped = _clock.GetUtcNow();

		Assert.Empty(_placed); // nothing while the sign-in is ended
		await SignInAgain();

		var placed = Assert.Single(_placed);
		Assert.Equal((item, false, 20 * Minute, stopped), placed); // its resume point, when it stopped, still not watched
		await SignInAgain();
		Assert.Single(_placed); // sent once
	}

	[Fact]
	public async Task APlayToItsEnd_IsWatched_AndOneUnder5Percent_HasNoResumePoint()
	{
		var finished = Guid.NewGuid();
		var started = Guid.NewGuid();
		Play(finished, "ps-1", Runtime, refused: true, reason: "eof");
		Play(started, "ps-2", 1 * Minute, refused: true);

		await SignInAgain();

		Assert.Contains((finished, true, 0L, _clock.GetUtcNow()), _placed);
		Assert.Contains(_placed, x => x.Id == started && x.Position == 0 && !x.Played);
	}

	/// <summary>
	/// 1.7.5 (the review's probe): the player's own Sign in again, with a saved sign-in, signs in while the item still plays,
	/// then goes back; the stop is refused after that moment (the backend's old token), so the place was never written then,
	/// only at some unrelated later sign-in, up to 12 hours on. It is sent when the stop is refused.
	/// </summary>
	[Fact]
	public async Task SignedInAgainWhileItPlays_ThePlaceIsSentWhenTheStopIsRefused()
	{
		var item = Guid.NewGuid();
		_events.OnNext(State("playing", item, "ps-1", Runtime));
		_events.OnNext(Position(5 * Minute, Runtime));
		_events.OnNext(Refused());
		SignInState.MarkEnded();   // the player says so, with Sign in again
		SignInState.SignedIn();    // the saved sign-in, in place, while the item still plays
		await _plays.SendingOwed;
		Assert.Empty(_placed);     // nothing owed yet: it hasn't stopped

		_events.OnNext(Position(20 * Minute, Runtime));
		_events.OnNext(Ended(item, "stopped"));     // Back: the player stops it
		_events.OnNext(Reported(item, "ps-1", "failed"));
		await _plays.SendingOwed;

		var placed = Assert.Single(_placed);
		Assert.Equal((item, false, 20 * Minute), (placed.Id, placed.Played, placed.Position));
		await SignInAgain();
		Assert.Single(_placed); // and not again at a later sign-in
	}

	/// <summary>
	/// 1.7.5 (the review): a Mark watched or unwatched while the place waits for the sign-in: the place would undo it (a watched
	/// mark or a resume point back), so it is dropped; so is one older than the item's next play.
	/// </summary>
	[Fact]
	public async Task AMarkOrANewPlayOfTheItem_DropsThePlaceItWouldUndo()
	{
		var marked = new BaseItemDto { Id = Guid.NewGuid(), Name = "Heat", Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { Played = true } };
		var replayed = Guid.NewGuid();
		_client.Setup(x => x.SetPlayed(marked, false)).ReturnsAsync(new UserItemDataDto { Played = false, PlaybackPositionTicks = 0 });
		Play(marked.Id!.Value, "ps-1", Runtime, refused: true, reason: "eof"); // watched to its end; its stop refused
		Play(replayed, "ps-2", 20 * Minute, refused: true);
		SignInState.MarkEnded();

		Assert.True(await ItemUserData.SetPlayedAsync(_client.Object, marked, false)); // Mark unwatched meanwhile
		_events.OnNext(State("playing", replayed, "ps-3", Runtime));                  // and the other played again
		await SignInAgain();

		Assert.Empty(_placed);
	}

	[Fact]
	public async Task NothingIsSent_ForAReportThatFailedOtherwise_APlaySince_OrAnotherUser()
	{
		// a Stopped that timed out (no auth_failed): not the sign-in's, nothing owed
		Play(Guid.NewGuid(), "ps-1", 20 * Minute, refused: false);
		await SignInAgain();
		Assert.Empty(_placed);

		// played again since (on another device): the server's place stands
		Play(Guid.NewGuid(), "ps-2", 20 * Minute, refused: true);
		_lastPlayedOnServer = _clock.GetUtcNow().AddMinutes(1);
		await SignInAgain();
		Assert.Empty(_placed);

		// the new sign-in is another user's: theirs is not this play's
		_lastPlayedOnServer = null;
		Play(Guid.NewGuid(), "ps-3", 20 * Minute, refused: true);
		_user = Guid.NewGuid();
		await SignInAgain();
		Assert.Empty(_placed);
	}
}
