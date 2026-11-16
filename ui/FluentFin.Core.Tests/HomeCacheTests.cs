using System.Reactive.Subjects;
using System.Text.Json;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Kiota.Serialization.Json;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: the last home page (HomeCache): kept on disk across starts exactly as the server sent it, per server and
/// user; fresh in memory until it ages or the server reports a watch-state or library change.
/// </summary>
public sealed class HomeCacheTests : IDisposable
{
	private const string Server = "https://jellyfin.example:8920";
	private static readonly Guid User = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-homecache-test-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _server = new();
	private readonly ManualClock _clock = new();

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private HomeCache NewCache() => new(_folder, _server, new CoreListLogger<HomeCache>(), _clock);

	[Fact]
	public async Task ASnapshot_SurvivesARestart_AsTheServerSentIt_ButIsRefreshed()
	{
		var episode = Parse("""
			{"Id":"0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a","Name":"The Return","Type":"Episode","SeriesName":"Show","SeriesId":"1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b",
			 "SeriesPrimaryImageTag":"sp1","ParentIndexNumber":6,"IndexNumber":4,
			 "ImageTags":{"Primary":"p1","Thumb":"t1"},"ImageBlurHashes":{"Thumb":{"t1":"LEHV6nWB2yk8pyo0adR*.7kCMdnj"}},
			 "UserData":{"PlaybackPositionTicks":4200,"PlayedPercentage":12.5,"Played":false,"IsFavorite":true}}
			""");
		var library = Parse("""{"Id":"2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c","Name":"Movies","Type":"CollectionFolder","CollectionType":"movies"}""");
		var movie = Parse("""{"Id":"3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d","Name":"A Film","Type":"Movie","ProductionYear":1999,"ImageTags":{"Primary":"p2"}}""");
		using (var before = NewCache())
		{
			await before.Save(Server, User, new HomeSnapshot([episode], [], [new HomeSnapshotRow(library, [movie])], [EmptyLibrary]), (await before.Get(Server, User)).ChangesSeen);
		}

		using var after = NewCache();
		var cached = await after.Get(Server, User);

		Assert.False(cached.IsFresh);
		var snapshot = Assert.IsType<HomeSnapshot>(cached.Snapshot);
		var e = Assert.Single(snapshot.ContinueItems);
		Assert.Equal((episode.Id, "The Return", BaseItemDto_Type.Episode, "Show", episode.SeriesId, "sp1", 6, 4), (e.Id, e.Name, e.Type, e.SeriesName, e.SeriesId, e.SeriesPrimaryImageTag, e.ParentIndexNumber, e.IndexNumber));
		Assert.Equal("t1", $"{e.ImageTags!.AdditionalData["Thumb"]}");
		Assert.Equal("LEHV6nWB2yk8pyo0adR*.7kCMdnj", $"{e.ImageBlurHashes!.Thumb!.AdditionalData["t1"]}");
		Assert.Equal((4200L, 12.5, false, true), (e.UserData!.PlaybackPositionTicks, e.UserData.PlayedPercentage, e.UserData.Played, e.UserData.IsFavorite));
		Assert.Empty(snapshot.NextUpItems);
		var row = Assert.Single(snapshot.Rows);
		Assert.Equal((library.Id, "Movies", BaseItemDto_CollectionType.Movies), (row.Library.Id, row.Library.Name, row.Library.CollectionType));
		Assert.Equal((movie.Id, "A Film", 1999, "p2"), (row.Items[0].Id, row.Items[0].Name, row.Items[0].ProductionYear, $"{row.Items[0].ImageTags!.AdditionalData["Primary"]}"));
		Assert.Equal([EmptyLibrary], snapshot.EmptyLibraries);
	}

	private static readonly Guid EmptyLibrary = Guid.Parse("4e4e4e4e-4e4e-4e4e-4e4e-4e4e4e4e4e4e");

	[Fact]
	public async Task AnotherUserOrServer_GetsNothing_AndTheFileNameSaysNeither()
	{
		using var cache = NewCache();
		await cache.Save(Server, User, Some(), 0);

		using var restarted = NewCache();
		Assert.Null((await restarted.Get(Server, Guid.NewGuid())).Snapshot);
		Assert.Null((await restarted.Get("https://other.example", User)).Snapshot);
		Assert.Null((await cache.Get(Server, Guid.NewGuid())).Snapshot);
		Assert.NotNull((await restarted.Get(Server + "/", User)).Snapshot);
		var file = Assert.Single(Directory.GetFiles(_folder));
		Assert.DoesNotContain("example", file, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(User.ToString("N"), file, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ADamagedFile_IsIgnored()
	{
		using (var cache = NewCache())
		{
			await cache.Save(Server, User, Some(), 0);
		}
		File.WriteAllText(Assert.Single(Directory.GetFiles(_folder)), "{\"Version\":1,\"Continue\":[");

		using var restarted = NewCache();
		var cached = await restarted.Get(Server, User);

		Assert.Null(cached.Snapshot);
		Assert.False(cached.IsFresh);
	}

	[Fact]
	public async Task InMemory_ASnapshotIsFresh_UntilItAges()
	{
		using var cache = NewCache();
		await cache.Save(Server, User, Some(), (await cache.Get(Server, User)).ChangesSeen);
		Assert.True((await cache.Get(Server, User)).IsFresh);

		_clock.Advance(HomeCache.FreshFor - TimeSpan.FromSeconds(1));
		Assert.True((await cache.Get(Server, User)).IsFresh);
		_clock.Advance(TimeSpan.FromSeconds(2));
		var aged = await cache.Get(Server, User);
		Assert.False(aged.IsFresh);
		Assert.NotNull(aged.Snapshot);

		await cache.Save(Server, User, Some(), aged.ChangesSeen);
		Assert.True((await cache.Get(Server, User)).IsFresh);
	}

	[Theory]
	[InlineData("userdata")]
	[InlineData("library")]
	public async Task AWatchStateOrLibraryChange_MakesItStale_OtherMessagesDoNot(string change)
	{
		using var cache = NewCache();
		await cache.Save(Server, User, Some(), (await cache.Get(Server, User)).ChangesSeen);

		_server.OnNext(new SessionsMessage());
		Assert.True((await cache.Get(Server, User)).IsFresh);

		_server.OnNext(change == "userdata" ? new UserDataChangedMessage() : new LibraryChangedMessage());
		Assert.False((await cache.Get(Server, User)).IsFresh);
	}

	[Fact]
	public async Task AChangeReportedDuringARefresh_KeepsTheRefreshedSnapshotStale()
	{
		using var cache = NewCache();
		var before = await cache.Get(Server, User);

		_server.OnNext(new UserDataChangedMessage()); // while the page asks the server
		await cache.Save(Server, User, Some(), before.ChangesSeen);
		Assert.False((await cache.Get(Server, User)).IsFresh);

		var again = await cache.Get(Server, User);
		await cache.Save(Server, User, Some(), again.ChangesSeen);
		Assert.True((await cache.Get(Server, User)).IsFresh);
	}

	[Theory]
	[InlineData("sign-in")]
	[InlineData("player page")]
	public async Task ASignInOrAPlayerPage_MakesItStale_WebsocketOrNot(string change)
	{
		using var cache = NewCache();
		await cache.Save(Server, User, Some(), (await cache.Get(Server, User)).ChangesSeen);
		Assert.True((await cache.Get(Server, User)).IsFresh);

		if (change == "sign-in")
		{
			cache.SignedIn(Server, User, ServerId, "alice");
		}
		else
		{
			cache.MarkStale();
		}

		var cached = await cache.Get(Server, User);
		Assert.False(cached.IsFresh);
		Assert.NotNull(cached.Snapshot);
	}

	[Fact]
	public async Task SigningOut_DeletesThePage_AndARefreshAskedForBefore_IsNotSaved()
	{
		using var cache = NewCache();
		cache.SignedIn(Server, User, ServerId, "alice");
		var before = await cache.Get(Server, User);
		await cache.Save(Server, User, Some(), before.ChangesSeen);
		Assert.Single(Directory.GetFiles(_folder));

		await cache.Forget(Server + "/", User);

		Assert.Empty(Directory.GetFiles(_folder));
		Assert.Null((await cache.Get(Server, User)).Snapshot);

		// a refresh that read the cache before the sign-out ends after it: its page would bring the file back
		await cache.Save(Server, User, Some(), before.ChangesSeen);
		Assert.Empty(Directory.GetFiles(_folder));
		Assert.Null((await cache.Get(Server, User)).Snapshot);

		// the next sign-in's pages are saved as usual
		await cache.Save(Server, User, Some(), (await cache.Get(Server, User)).ChangesSeen);
		Assert.Single(Directory.GetFiles(_folder));
	}

	[Fact]
	public async Task RemovingASavedLogin_DeletesItsPage_AndPagesOfNoKnownLogin_NotOtherLogins()
	{
		var bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
		using (var lastRun = NewCache())
		{
			lastRun.SignedIn(Server, User, ServerId, "Alice");
			await lastRun.Save(Server, User, Some(), (await lastRun.Get(Server, User)).ChangesSeen);
			lastRun.SignedIn(Server, bob, ServerId, "bob");
			await lastRun.Save(Server, bob, Some(), (await lastRun.Get(Server, bob)).ChangesSeen);
			await lastRun.Save("https://old.example", User, Some(), 0); // saved with no sign-in heard of: whose, not known
		}
		Assert.Equal(3, Directory.GetFiles(_folder).Length);

		// another start: Settings > Delete User for alice on that server (the server's id as the server list keeps it)
		using var cache = NewCache();
		await cache.ForgetLogin(ServerId.ToUpperInvariant(), "ALICE");

		Assert.Null((await cache.Get(Server, User)).Snapshot);
		Assert.Null((await cache.Get("https://old.example", User)).Snapshot);
		Assert.NotNull((await cache.Get(Server, bob)).Snapshot);
		Assert.Single(Directory.GetFiles(_folder));
	}

	[Fact]
	public async Task RemovingTheSignedInUsersSavedLogin_DropsThePageInMemory_AndARefreshFromBefore()
	{
		using var cache = NewCache();
		cache.SignedIn(Server, User, ServerId, "alice");
		var before = await cache.Get(Server, User);
		await cache.Save(Server, User, Some(), before.ChangesSeen);

		await cache.ForgetLogin(ServerId, "alice");
		await cache.Save(Server, User, Some(), before.ChangesSeen);

		Assert.Null((await cache.Get(Server, User)).Snapshot);
		Assert.False(Directory.Exists(_folder) && Directory.GetFiles(_folder).Length > 0);
	}

	[Fact]
	public async Task NoFilePath_IsSaved()
	{
		// Next Up asks the server for each episode's path (Fields=Path); a saved page is not a list of the server's files
		var episode = Parse("""
			{"Id":"0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a","Name":"The Return","Type":"Episode","Path":"/srv/media/Shows/Private Show/S06E04.mkv",
			 "MediaSources":[{"Id":"0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a","Path":"/srv/media/Shows/Private Show/S06E04.mkv"}],
			 "UserData":{"PlaybackPositionTicks":4200}}
			""");
		var library = Parse("""{"Id":"2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c","Name":"Shows","Type":"CollectionFolder","Path":"/srv/media/Shows"}""");
		using (var before = NewCache())
		{
			await before.Save(Server, User, new HomeSnapshot([], [episode], [new HomeSnapshotRow(library, [episode])], []), 0);
		}

		var text = File.ReadAllText(Assert.Single(Directory.GetFiles(_folder)));
		Assert.DoesNotContain("/srv/media", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\"Path\"", text, StringComparison.Ordinal);

		// the page itself is all there, and the records the page shows keep their paths in memory
		using var after = NewCache();
		var snapshot = (await after.Get(Server, User)).Snapshot!;
		Assert.Equal(("The Return", 4200L), (snapshot.NextUpItems[0].Name, snapshot.NextUpItems[0].UserData!.PlaybackPositionTicks));
		Assert.Equal("Shows", snapshot.Rows[0].Library.Name);
		Assert.Null(snapshot.NextUpItems[0].Path);
		Assert.Equal("/srv/media/Shows/Private Show/S06E04.mkv", episode.Path);
	}

	private const string ServerId = "5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e";

	private static HomeSnapshot Some() => new([new BaseItemDto { Id = Guid.NewGuid(), Name = "Half watched", Type = BaseItemDto_Type.Movie }], [], [], []);

	private static BaseItemDto Parse(string json)
	{
		using var document = JsonDocument.Parse(json);
		return new JsonParseNode(document.RootElement.Clone()).GetObjectValue(BaseItemDto.CreateFromDiscriminatorValue);
	}
}
