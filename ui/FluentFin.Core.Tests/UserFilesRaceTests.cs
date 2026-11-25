using System.Text.Json.Nodes;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.6.1 (the 1.6 review's collections queue: "a write-back can race sign-out"): the library index read before a
/// sign-out and saved after it wrote the signed-out user's file again (its write waited for the sign-out's delete). A write
/// now says which sign-out it began after, and one that began before its user's sign-out is refused.
/// </summary>
public sealed class UserFilesRaceTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-userfiles-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	[Fact]
	public async Task AReadThatBeganBeforeTheSignOut_IsNotSavedAfterIt_ALaterOneIs()
	{
		using var homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), System.Reactive.Linq.Observable.Never<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		var files = new UserFiles(Path.Combine(_folder, "LibraryIndex"), homeCache, new CoreListLogger<UserFiles>());
		var alice = Guid.NewGuid();
		var bob = Guid.NewGuid();
		var key = HomeCache.FileKey("http://server", alice);
		var began = files.Generation; // the read begins

		await homeCache.Forget("http://server", alice); // she signs out while it reads
		await files.Write(key, new JsonObject { ["Titles"] = 1 }, login: null, since: began);

		Assert.False(File.Exists(files.PathOf(key)));
		// another user's read that began at the same time is theirs to save
		var bobs = HomeCache.FileKey("http://server", bob);
		await files.Write(bobs, new JsonObject { ["Titles"] = 2 }, login: null, since: began);
		Assert.True(File.Exists(files.PathOf(bobs)));
		// she signs in again: a read that begins now is saved
		await files.Write(key, new JsonObject { ["Titles"] = 3 }, login: null, since: files.Generation);
		Assert.True(File.Exists(files.PathOf(key)));
		// a write that does not say which (the saved collections, written as the user acts) is written as before
		File.Delete(files.PathOf(key));
		await files.Write(key, new JsonObject { ["Titles"] = 4 }, login: null);
		Assert.True(File.Exists(files.PathOf(key)));
	}

	[Fact]
	public void TheIndex_TakesTheNumberWhenItsReadBegins_AndSavesWithIt()
	{
		var root = HomeAndLibraryXamlTests.RepoRoot;
		var index = File.ReadAllText(Path.Combine(root, "FluentFin.Core", "SmartCollections", "LibraryIndex.cs"));
		var read = index[index.IndexOf("private Task<IndexSnapshot?> Read(string key, ReadKind kind = ReadKind.Whole", StringComparison.Ordinal)..];
		read = read[..read.IndexOf("private async Task ReadFromServer(", StringComparison.Ordinal)]; // 1.7.1: the read's first index is a task of its own
		Assert.Contains("since = _files.Generation;", read);
		Assert.True(read.IndexOf("since = _files.Generation;", StringComparison.Ordinal) < read.IndexOf("Task.Run(() => ReadFromServer(key, kind, url, user, since, previous, begun, firstIn, cancellation", StringComparison.Ordinal));
		Assert.Contains("var login = _homeCache.LoginKey(url, user);", index); // 1.7.1: saved behind the index it says is in
		Assert.Contains("_files.WriteAs(key, saving => ToDocument(snapshot, saving), Json, login, since)", index); // 1.8 (BH-180-m2): streamed
		Assert.Contains("}, _homeCache.LoginKey(url, user), since);", read); // 1.7.1 review M2: the note of its last read, too
	}
}
