using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// image tags and blur hashes, watch state; no token). A snapshot read from disk is always refreshed; one in memory is
/// refreshed when it is older than <see cref="FreshFor"/> or the server has reported a watch-state or library change
/// since (UserDataChanged, LibraryChanged), so Back to Home after opening an item asks the server nothing.
/// </summary>
public sealed class HomeCache : IDisposable
{
	public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);
	private const int FormatVersion = 1;

	private readonly string _folder;
	private readonly ILogger<HomeCache> _logger;
	private readonly TimeProvider _time;
	private readonly IDisposable _subscription;
	private readonly Lock _lock = new();
	private readonly SemaphoreSlim _writing = new(1, 1);
	private Remembered? _memory;
	private int _changes;

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
				lock (_lock)
				{
					_changes++;
					if (_memory is { } memory)
					{
						_memory = memory with { Changed = true };
					}
				}
			}
		});
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
	/// a server change reported during the refresh keeps the snapshot stale.
	/// </summary>
	public async Task Save(string serverUrl, Guid userId, HomeSnapshot snapshot, int changesSeen)
	{
		var key = Key(serverUrl, userId);
		lock (_lock)
		{
			_memory = new Remembered(key, snapshot, _time.GetTimestamp(), Changed: _changes != changesSeen);
		}

		await _writing.WaitAsync();
		try
		{
			await Task.Run(() => Write(PathOf(key), snapshot));
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

	private string PathOf(string key) => Path.Combine(_folder, key + ".json");

	// the server address and user, hashed: a file name, and nothing readable about either
	private static string Key(string serverUrl, Guid userId) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{serverUrl.TrimEnd('/').ToLowerInvariant()}|{userId:N}")))[..32];

	private void Write(string path, HomeSnapshot snapshot)
	{
		Directory.CreateDirectory(_folder);
		using var buffer = new MemoryStream();
		using (var json = new Utf8JsonWriter(buffer))
		{
			json.WriteStartObject();
			json.WriteNumber("Version", FormatVersion);
			json.WritePropertyName("Continue");
			json.WriteRawValue(Kiota(snapshot.ContinueItems));
			json.WritePropertyName("NextUp");
			json.WriteRawValue(Kiota(snapshot.NextUpItems));
			json.WriteStartArray("Rows");
			foreach (var row in snapshot.Rows)
			{
				json.WriteStartObject();
				json.WritePropertyName("Library");
				json.WriteRawValue(Kiota(row.Library));
				json.WritePropertyName("Items");
				json.WriteRawValue(Kiota(row.Items));
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteStartArray("EmptyLibraries");
			foreach (var id in snapshot.EmptyLibraries)
			{
				json.WriteStringValue(id.ToString("N"));
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}

		var temporary = path + ".tmp";
		File.WriteAllBytes(temporary, buffer.ToArray());
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

	private static string Kiota(IEnumerable<BaseItemDto> items)
	{
		using var writer = new JsonSerializationWriter();
		writer.WriteCollectionOfObjectValues(null, items);
		return Content(writer);
	}

	private static string Kiota(BaseItemDto item)
	{
		using var writer = new JsonSerializationWriter();
		writer.WriteObjectValue(null, item);
		return Content(writer);
	}

	private static string Content(JsonSerializationWriter writer)
	{
		using var reader = new StreamReader(writer.GetSerializedContent());
		return reader.ReadToEnd();
	}

	public void Dispose()
	{
		_subscription.Dispose();
		_writing.Dispose();
	}
}
