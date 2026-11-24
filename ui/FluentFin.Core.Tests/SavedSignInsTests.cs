using FluentFin.Core.Settings;

namespace FluentFin.Core.Tests;

/// <summary>
/// 174-signin: a server's saved sign-ins. Sign out and Switch user end the token on the server, so a saved Quick Connect
/// sign-in (its token; its password is an empty one) is forgotten; a saved password stays as it was (the next start signs
/// in with it). One saved user per name whatever its case: Jellyfin's names ignore case, a password sign-in saves the
/// name as typed and Quick Connect the server's, and the start signs in by itself only with one saved user.
/// </summary>
public class SavedSignInsTests
{
	private static SavedServer Server(params SavedUser[] users) => new() { Id = "s", Users = [.. users] };

	[Fact]
	public void ASavedQuickConnectSignIn_IsForgotten()
	{
		var server = Server(new SavedUser { Username = "alice", Token = [1, 2] }, new SavedUser { Username = "bob", Password = [3] });
		var changes = 0;
		server.Users.CollectionChanged += (_, _) => changes++; // what Settings saves on

		Assert.True(SavedSignIns.ForgetToken(server, "alice"));

		Assert.Equal("bob", Assert.Single(server.Users).Username);
		Assert.Equal(1, changes);
	}

	[Fact]
	public void AQuickConnectEntry_GoesWhole_ItsPasswordIsNoneOfItsOwn()
	{
		// a Quick Connect entry's password is an empty one, protected (for 1.7.3 and earlier): kept, the next start would
		// sign in with it and be refused
		var server = Server(new SavedUser { Username = "alice", Password = [3], Token = [1, 2] });

		Assert.True(SavedSignIns.ForgetToken(server, "alice"));

		Assert.Empty(server.Users);
	}

	[Theory]
	[InlineData("ALICE")]
	[InlineData("Alice")]
	[InlineData(" alice ")]
	public void ForgetToken_IgnoresTheNamesCase(string signedOut)
	{
		var server = Server(new SavedUser { Username = "alice", Token = [1, 2] });

		Assert.True(SavedSignIns.ForgetToken(server, signedOut));
		Assert.Empty(server.Users);
	}

	[Theory]
	[InlineData("alice")]
	[InlineData("carol")]
	[InlineData("")]
	[InlineData(null)]
	public void APasswordOnly_OrNoSuchUser_ChangesNothing(string? user)
	{
		var server = Server(new SavedUser { Username = "alice", Password = [3] });
		var changes = 0;
		server.Users.CollectionChanged += (_, _) => changes++;

		Assert.False(SavedSignIns.ForgetToken(server, user));
		Assert.False(SavedSignIns.ForgetToken(null, user));

		Assert.Equal([3], Assert.Single(server.Users).Password);
		Assert.Equal(0, changes);
	}

	[Fact]
	public void Remember_TheSameUserInAnotherCase_IsOneSavedUser_InItsPlace()
	{
		var server = Server(new SavedUser { Username = "bob", Password = [9] }, new SavedUser { Username = "Alice", Password = [3] });
		var quickConnect = new SavedUser { Username = "alice", Password = [0], Token = [1, 2] };

		SavedSignIns.Remember(server, quickConnect);

		Assert.Equal(["bob", "alice"], server.Users.Select(x => x.Username));
		Assert.Same(quickConnect, server.Users[1]);
	}

	[Fact]
	public void Remember_MergesTheSameUserSavedTwice()
	{
		// two entries an earlier build saved for one user ("Alice" typed, "alice" from Quick Connect)
		var server = Server(new SavedUser { Username = "Alice", Password = [3] }, new SavedUser { Username = "bob", Password = [9] },
			new SavedUser { Username = "alice", Token = [1] });
		var typed = new SavedUser { Username = "ALICE", Password = [4] };

		SavedSignIns.Remember(server, typed);

		Assert.Equal(["ALICE", "bob"], server.Users.Select(x => x.Username));
		Assert.Same(typed, server.Users[0]);
	}

	[Fact]
	public void Remember_ANewUser_IsAdded()
	{
		var server = Server(new SavedUser { Username = "bob", Password = [9] });

		SavedSignIns.Remember(server, new SavedUser { Username = "alice", Password = [3] });

		Assert.Equal(["bob", "alice"], server.Users.Select(x => x.Username));
	}

	// ---- 174-sessions (the basics sweep of 1.7.3: BS-M1, BS-M2, BS-M4, BS-m1, BS-m3)

	private static readonly DateTimeOffset Earlier = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);

	[Fact]
	public void ForStart_TheServerSignedInToLast_AsItsLastUser()
	{
		var one = new SavedServer { Id = "one", LastUser = "alice", LastSignIn = Earlier, Users = [new SavedUser { Username = "alice", Password = [1] }] };
		var two = new SavedServer { Id = "two", LastUser = "Bob", LastSignIn = Earlier.AddMinutes(1), Users = [new SavedUser { Username = "carol", Password = [1] }, new SavedUser { Username = "bob", Token = [2] }] };

		var start = SavedSignIns.ForStart([one, two]);

		Assert.NotNull(start);
		Assert.Same(two, start.Value.Server);
		Assert.Equal("bob", start.Value.User.Username); // whatever the case, among two saved users (BS-M2: it took exactly one)
	}

	[Fact]
	public void ForStart_NeverSignsInAsSomeoneElse_WhenTheLastOnesSignInWent()
	{
		// Sign out (or a refused password) took alice's saved sign-in: bob stays saved on the same server, carol on another
		var one = new SavedServer { Id = "one", LastUser = "alice", LastSignIn = Earlier.AddMinutes(1), Users = [new SavedUser { Username = "bob", Password = [1] }] };
		var two = new SavedServer { Id = "two", LastUser = "carol", LastSignIn = Earlier, Users = [new SavedUser { Username = "carol", Password = [1] }] };

		Assert.Null(SavedSignIns.ForStart([one, two]));
		Assert.Null(SavedSignIns.ForStart([one]));
		one.LastUser = ""; // the last one asked not to be remembered
		Assert.Null(SavedSignIns.ForStart([one]));
	}

	[Fact]
	public void ForStart_WithNoSignInRecorded_KeepsTheOldRule()
	{
		// a data folder from 1.7.3: one server with one saved user that can sign in
		var alone = new SavedServer { Id = "one", Users = [new SavedUser { Username = "alice", Password = [1] }] };
		Assert.Same(alone.Users[0], SavedSignIns.ForStart([alone])?.User);

		Assert.Null(SavedSignIns.ForStart([new SavedServer { Id = "one", Users = [new SavedUser { Username = "alice" }] }])); // nothing to sign in with
		Assert.Null(SavedSignIns.ForStart([new SavedServer { Id = "one", Users = [new SavedUser { Username = "a", Password = [1] }, new SavedUser { Username = "b", Password = [1] }] }]));
		Assert.Null(SavedSignIns.ForStart([alone, new SavedServer { Id = "two" }]));
		Assert.Null(SavedSignIns.ForStart([]));
	}

	[Fact]
	public void SignedIn_Kept_IsTheLastOfAll_EvenWithinOneClockTick()
	{
		var future = DateTimeOffset.UtcNow.AddYears(1); // another server's, later than this clock
		var one = new SavedServer { Id = "one", LastSignIn = future };
		var two = new SavedServer { Id = "two" };

		SavedSignIns.SignedIn([one, two], two, " Bob ", kept: true);

		Assert.Equal("Bob", two.LastUser);
		Assert.True(two.LastSignIn > future);
		Assert.Same(two, new[] { one, two }.MaxBy(x => x.LastSignIn));
	}

	[Fact]
	public void SignedIn_NotKept_IsTheLastSignIn_WithNoName_AndForgetsNothing()
	{
		// the review's m2: a sign-in without "Remember me" (carol) is the last one, so the next start signs in as nobody, not as
		// the user remembered before; the review's Major: and no saved sign-in goes, carol's own neither
		var server = Server(new SavedUser { Username = "Alice", Password = [1] }, new SavedUser { Username = "carol", Password = [2] });
		server.LastUser = "alice";
		server.LastSignIn = Earlier;
		var other = new SavedServer { Id = "other", LastUser = "dave", LastSignIn = DateTimeOffset.UtcNow.AddYears(1), Users = [new SavedUser { Username = "dave", Password = [3] }] };

		SavedSignIns.SignedIn([server, other], server, "carol", kept: false);

		Assert.Equal(["Alice", "carol"], server.Users.Select(x => x.Username));
		Assert.Equal("", server.LastUser);
		Assert.True(server.LastSignIn > other.LastSignIn);
		Assert.Null(SavedSignIns.ForStart([server, other]));
	}

	[Fact]
	public void ForgetName_OnlyThatUsersName()
	{
		var server = new SavedServer { LastUser = "alice" };
		SavedSignIns.ForgetName(server, "bob");
		Assert.Equal("alice", server.LastUser);
		SavedSignIns.ForgetName(server, " ALICE ");
		Assert.Equal("", server.LastUser);
		SavedSignIns.ForgetName(null, "alice");
	}

	[Fact]
	public void Forget_TakesAPasswordOrAToken_KeepsTheLastUsersName()
	{
		var server = Server(new SavedUser { Username = "alice", Password = [1] }, new SavedUser { Username = "ALICE", Token = [2] }, new SavedUser { Username = "bob", Password = [3] });
		server.LastUser = "alice";
		var changes = 0;
		server.Users.CollectionChanged += (_, _) => changes++; // what Settings saves on

		Assert.True(SavedSignIns.Forget(server, " Alice "));

		Assert.Equal("bob", Assert.Single(server.Users).Username);
		Assert.Equal("alice", server.LastUser);
		Assert.Equal(2, changes);
		Assert.False(SavedSignIns.Forget(server, "carol"));
		Assert.False(SavedSignIns.Forget(server, ""));
		Assert.False(SavedSignIns.Forget(null, "bob"));
	}

	[Fact]
	public void CanSignIn_APasswordOrAToken()
	{
		Assert.True(SavedSignIns.CanSignIn(new SavedUser { Username = "a", Password = [1] }));
		Assert.True(SavedSignIns.CanSignIn(new SavedUser { Username = "a", Token = [1] }));
		Assert.False(SavedSignIns.CanSignIn(new SavedUser { Username = "a" }));
	}

	[Fact]
	public void UsernameFor_TheLastOne_ElseTheOneSaved_ElseNone()
	{
		Assert.Equal("bob", SavedSignIns.UsernameFor(Server(new SavedUser { Username = "bob", Password = [1] })));
		Assert.Equal("alice", SavedSignIns.UsernameFor(new SavedServer { LastUser = " alice ", Users = [new SavedUser { Username = "bob", Password = [1] }] }));
		Assert.Equal("", SavedSignIns.UsernameFor(Server(new SavedUser { Username = "a", Password = [1] }, new SavedUser { Username = "b", Password = [1] })));
		Assert.Equal("", SavedSignIns.UsernameFor(Server()));
		Assert.Equal("", SavedSignIns.UsernameFor(null));
	}

	/// <summary>The two fields are new: Nixie 1.7.3 reads a file with them, and 1.7.4 one without them (null, "").</summary>
	[Fact]
	public void TheLastSignIn_RoundTripsThroughTheSettingsFile_AndAnOldFileHasNone()
	{
		var options = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
		var server = new SavedServer { Id = "one", LastUser = "alice", LastSignIn = Earlier };
		var back = System.Text.Json.JsonSerializer.Deserialize<SavedServer>(System.Text.Json.JsonSerializer.Serialize(server, options), options)!;
		Assert.Equal(("alice", Earlier), (back.LastUser, back.LastSignIn));

		var old = System.Text.Json.JsonSerializer.Deserialize<SavedServer>("""{"Id":"one","Users":[{"Username":"alice","Password":"AQ=="}]}""", options)!;
		Assert.Equal(("", (DateTimeOffset?)null), (old.LastUser, old.LastSignIn));
	}
}
