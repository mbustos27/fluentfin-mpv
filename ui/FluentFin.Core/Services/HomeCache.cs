using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Serialization.Json;

namespace FluentFin.Core.Services;

/// <summary>
/// What the home page showed: Continue Watching, Next Up and the "Recently added" rows, in order, and the libraries that
/// had nothing recent (no placeholder row for them next time, so the rows below do not jump when it goes).
/// </summary>
public sealed record HomeSnapshot(IReadOnlyList<BaseItemDto> ContinueItems, IReadOnlyList<BaseItemDto> NextUpItems, IReadOnlyList<HomeSnapshotRow> Rows, IReadOnlyList<Guid> EmptyLibraries);

public sealed record HomeSnapshotRow(BaseItemDto Library, IReadOnlyList<BaseItemDto> Items);

/// <summary>The snapshot to show at once (none when there is none), whether it is fresh enough to skip a refresh, and the server changes seen when it was read.</summary>
public sealed record CachedHome(HomeSnapshot? Snapshot, bool IsFresh, int ChangesSeen);

/// <summary>
/// fluentfin-mpv: the last home page, per server address and user, so Home shows at once instead of placeholders.
/// In memory for the session and in the data folder across starts (the lean item records the server sent: names, ids,
/// image tags and blur hashes, watch state; no token, no file paths). A snapshot read from disk is always refreshed; one
/// in memory is refreshed when it is older than <see cref="FreshFor"/>, or something may have changed it since: the server
/// reported a watch-state or library change (UserDataChanged, LibraryChanged), someone signed in, or a player page was
/// opened or left (<see cref="MarkStale"/>: Continue Watching and Next Up change without a word from a server whose
/// websocket is not connected). So Back to Home after opening an item asks the server nothing.
/// <para>
/// A user's page goes when they sign out (<see cref="Forget"/>) or their saved login is removed (<see cref="ForgetLogin"/>,
/// found by the login it was saved for); a refresh asked for before either is not saved.
/// </para>
/// </summary>
public sealed class HomeCache : IDisposable
{
	public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

	// 2: the login the page was saved for, no file paths (a version 1 file is not read)
	private const int FormatVersion = 2;

	private readonly string _folder;
	private readonly ILogger<HomeCache> _logger;
	private readonly TimeProvider _time;
	private readonly IDisposable _subscription;
	private readonly Lock _lock = new();
	private readonly SemaphoreSlim _writing = new(1, 1);
	private Remembered? _memory;
	private int _changes;

	// the login each page of this run is for (SignedIn), written into its file so ForgetLogin can find it
	private readonly Dictionary<string, string> _logins = [];

	// the pages whose user signed out or whose saved login was removed, with _changes then: a refresh that read the cache
	// before that is not saved
	private readonly Dictionary<string, int> _forgotten = [];

	private sealed record Remembered(string Key, HomeSnapshot Snapshot, long SavedAt, bool Changed);

	public HomeCache(KnownFolders folders, IObservable<IOutboundWebSocketMessage> serverMessages, ILogger<HomeCache> logger)
		: this(Path.Combine(folders.ApplicationData, "HomeCache"), serverMessages, logger, TimeProvider.System)
	{
	}

	internal HomeCache(string folder, IObservable<IOutboundWebSocketMessage> serverMessages, ILogger<HomeCache> logger, TimeProvider time)
	{
		_folder = folder;
		_logger = logger;
		_time = time;
		_subscription = serverMessages.Subscribe(message =>
		{
			if (message is UserDataChangedMessage or LibraryChangedMessage)
			{
				MarkStale();
			}
		});
	}

	/// <summary>Something may have changed the page (a player page opened or left): the next visit asks the server again.</summary>
	public void MarkStale()
	{
		lock (_lock)
		{
			Changed();
		}
	}

	// under _lock: counts a change (a refresh running now is saved stale) and makes the page in memory stale
	private void Changed()
	{
		_changes++;
		if (_memory is { } memory)
		{
			_memory = memory with { Changed = true };
		}
	}

	/// <summary>
	/// A sign-in (JellyfinClient.Initialize): the page in memory is stale, and this user's page is saved with the login it is
	/// for (<paramref name="serverId"/>, <paramref name="userName"/>), so removing that saved login can find it.
	/// </summary>
	public void SignedIn(string serverUrl, Guid userId, string? serverId, string? userName)
	{
		var key = Key(serverUrl, userId);
		lock (_lock)
		{
			Changed();
			if (LoginOf(serverId, userName) is { } login)
			{
				_logins[key] = login;
			}
			else
			{
				_logins.Remove(key);
			}
		}
	}

	/// <summary>
	/// The user signs out: their page goes, from memory and from disk, and a refresh that read the cache before this is not
	/// saved (it would bring the file back). Never throws.
	/// </summary>
	public async Task Forget(string serverUrl, Guid userId)
	{
		var key = Key(serverUrl, userId);
		lock (_lock)
		{
			Changed();
			_forgotten[key] = _changes;
			if (_memory?.Key == key)
			{
				_memory = null;
			}
		}

		var file = Path.GetFileName(PathOf(key));
		await Delete(path => Path.GetFileName(path) is var name &&
			(name.Equals(file, StringComparison.OrdinalIgnoreCase) || name.Equals(file + ".tmp", StringComparison.OrdinalIgnoreCase)));
	}

	/// <summary>
	/// A saved login was removed (Settings): the page saved for it goes, found by the login it was written with, and so does
	/// any page whose login is not known (a file from before logins were written into it): a page costs one refresh, a
	/// forgotten one keeps a watch history on disk. Never throws.
	/// </summary>
	public async Task ForgetLogin(string serverId, string userName)
	{
		if (LoginOf(serverId, userName) is not { } login)
		{
			return;
		}

		lock (_lock)
		{
			Changed();
			foreach (var key in _logins.Where(x => x.Value == login).Select(x => x.Key))
			{
				_forgotten[key] = _changes;
				if (_memory?.Key == key)
				{
					_memory = null;
				}
			}
		}

		await Delete(path => SavedLogin(path) is not { } saved || saved == login);
	}

	/// <summary>Deletes the saved pages <paramref name="which"/> picks, once no write is running; off the UI thread.</summary>
	private async Task Delete(Func<string, bool> which)
	{
		await _writing.WaitAsync();
		try
		{
			await Task.Run(() =>
			{
				if (!Directory.Exists(_folder))
				{
					return;
				}

				foreach (var path in Directory.GetFiles(_folder))
				{
					if (which(path))
					{
						File.Delete(path);
					}
				}
			});
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not delete a saved home page");
		}
		finally
		{
			_writing.Release();
		}
	}

	// the login a saved page was written for; null when it has none or cannot be read
	private string? SavedLogin(string path)
	{
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllBytes(path));
			return document.RootElement.TryGetProperty("Login", out var login) && login.ValueKind == JsonValueKind.String ? login.GetString() : null;
		}
		catch (Exception ex)
		{
			_logger.LogInformation(ex, "A saved home page could not be read");
			return null;
		}
	}

	/// <summary>The last home page of this server and user, if there is one.</summary>
	public async Task<CachedHome> Get(string serverUrl, Guid userId)
	{
		var key = Key(serverUrl, userId);
		int changes;
		lock (_lock)
		{
			changes = _changes;
			if (_memory is { } memory && memory.Key == key)
			{
				return new CachedHome(memory.Snapshot, !memory.Changed && _time.GetElapsedTime(memory.SavedAt) < FreshFor, changes);
			}
		}

		// the first visit since the start (or another user): the snapshot saved on disk, refreshed whatever its age; read
		// off the UI thread, and not at all on a first start
		var path = PathOf(key);
		var snapshot = File.Exists(path) ? await Task.Run(() => Read(path)) : null;
		return new CachedHome(snapshot, IsFresh: false, changes);
	}

	/// <summary>
	/// Keeps what the page now shows. <paramref name="changesSeen"/> is from the <see cref="Get"/> that started the refresh:
	/// a change during the refresh keeps the snapshot stale, and a sign-out (or the removal of the saved login) during it
	/// drops it.
	/// </summary>
	public async Task Save(string serverUrl, Guid userId, HomeSnapshot snapshot, int changesSeen)
	{
		var key = Key(serverUrl, userId);
		string? login;
		lock (_lock)
		{
			if (ForgottenSince(key, changesSeen))
			{
				return;
			}

			_memory = new Remembered(key, snapshot, _time.GetTimestamp(), Changed: _changes != changesSeen);
			_logins.TryGetValue(key, out login);
		}

		await _writing.WaitAsync();
		try
		{
			// again, now that no deletion runs: one that came while this waited has the last word
			lock (_lock)
			{
				if (ForgottenSince(key, changesSeen))
				{
					return;
				}
			}

			await Task.Run(() => Write(PathOf(key), snapshot, login));
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not save the home page for the next start");
		}
		finally
		{
			_writing.Release();
		}
	}

	// under _lock
	private bool ForgottenSince(string key, int changesSeen) => _forgotten.TryGetValue(key, out var at) && changesSeen < at;

	private string PathOf(string key) => Path.Combine(_folder, key + ".json");

	// the server address and user, hashed: a file name, and nothing readable about either
	private static string Key(string serverUrl, Guid userId) => Hash($"{serverUrl.TrimEnd('/').ToLowerInvariant()}|{userId:N}");

	// a saved login (the server's id and the user name, which Jellyfin compares without case), hashed; null if either is missing
	private static string? LoginOf(string? serverId, string? userName) =>
		string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(userName)
			? null
			: Hash($"login|{serverId.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()}|{userName.ToLowerInvariant()}");

	private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

	private void Write(string path, HomeSnapshot snapshot, string? login)
	{
		Directory.CreateDirectory(_folder);
		var root = new JsonObject
		{
			["Version"] = FormatVersion,
			["Login"] = login,
			["Continue"] = Kiota(snapshot.ContinueItems),
			["NextUp"] = Kiota(snapshot.NextUpItems),
			["Rows"] = new JsonArray([.. snapshot.Rows.Select(row => new JsonObject { ["Library"] = Kiota(row.Library), ["Items"] = Kiota(row.Items) })]),
			["EmptyLibraries"] = new JsonArray([.. snapshot.EmptyLibraries.Select(id => JsonValue.Create(id.ToString("N")))]),
		};

		var temporary = path + ".tmp";
		File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(root));
		File.Move(temporary, path, overwrite: true);
	}

	private HomeSnapshot? Read(string path)
	{
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllBytes(path));
			var root = document.RootElement;
			if (!root.TryGetProperty("Version", out var version) || version.GetInt32() != FormatVersion)
			{
				return null;
			}

			return new HomeSnapshot(
				Items(root.GetProperty("Continue")),
				Items(root.GetProperty("NextUp")),
				[.. root.GetProperty("Rows").EnumerateArray().Select(row => new HomeSnapshotRow(
					new JsonParseNode(row.GetProperty("Library")).GetObjectValue(BaseItemDto.CreateFromDiscriminatorValue),
					Items(row.GetProperty("Items"))))],
				[.. root.GetProperty("EmptyLibraries").EnumerateArray().Select(x => Guid.Parse(x.GetString()!))]);
		}
		catch (Exception ex)
		{
			// a damaged or old file: the page loads as if there were none
			_logger.LogInformation(ex, "Ignoring the saved home page");
			return null;
		}
	}

	private static List<BaseItemDto> Items(JsonElement array) =>
		[.. new JsonParseNode(array).GetCollectionOfObjectValues(BaseItemDto.CreateFromDiscriminatorValue) ?? []];

	private static JsonNode? Kiota(IEnumerable<BaseItemDto> items)
	{
		using var writer = new JsonSerializationWriter();
		writer.WriteCollectionOfObjectValues(null, items);
		return Content(writer);
	}

	private static JsonNode? Kiota(BaseItemDto item)
	{
		using var writer = new JsonSerializationWriter();
		writer.WriteObjectValue(null, item);
		return Content(writer);
	}

	private static JsonNode? Content(JsonSerializationWriter writer) => WithoutPaths(JsonNode.Parse(writer.GetSerializedContent()));

	// the records as the server sent them, but no file paths: Next Up asks for the item's (Fields=Path), and nothing on Home
	// shows one; a saved page is not a list of where the server keeps its files
	private static JsonNode? WithoutPaths(JsonNode? node)
	{
		switch (node)
		{
			case JsonObject item:
				item.Remove("Path");
				foreach (var (_, value) in item)
				{
					WithoutPaths(value);
				}
				break;
			case JsonArray list:
				foreach (var value in list)
				{
					WithoutPaths(value);
				}
				break;
		}
		return node;
	}

	public void Dispose()
	{
		_subscription.Dispose();
		_writing.Dispose();
	}
}
