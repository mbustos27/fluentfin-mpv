using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6 (smart collections phase 1): every film and show of the user's movie and show libraries, in one lean index
/// the Nixie collections are built from on this PC. Read page by page (<see cref="LibraryPageReader"/>, the 1.4.1 big-library
/// paging: a 25,800-film library never goes in one request), off the UI thread and cancellable; then the people in a pass of
/// their own (they cost about 3.5 times the index), then the played episodes (no show carries a last-played date). Kept per
/// server and user like <see cref="HomeCache"/>, in memory and in the data folder; read again when the server reports a
/// library change, on R, and once a day; forgotten when the user signs out or the saved login is removed.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
	public static readonly TimeSpan FreshFor = TimeSpan.FromDays(1);

	private const int FormatVersion = 2; // 1.7 keyless: 2 adds the Tmdb, Imdb and Tvdb ids (an older saved index is read again)

	private readonly IJellyfinClient _client;
	private readonly HomeCache _homeCache;
	private readonly UserFiles _files;
	private readonly ILogger _logger;
	private readonly TimeProvider _time;
	private readonly IDisposable _subscription;
	private readonly Lock _lock = new();
	private string? _memoryKey;
	private IndexSnapshot? _memory;
	private bool _stale;
	private string? _readingKey;
	private Task<IndexSnapshot?>? _reading;
	private CancellationTokenSource? _readingCancel;

	public LibraryIndex(IJellyfinClient client, HomeCache homeCache, KnownFolders folders, IObservable<IOutboundWebSocketMessage> serverMessages, ILogger<LibraryIndex> logger)
		: this(client, homeCache, Path.Combine(folders.ApplicationData, "LibraryIndex"), serverMessages, logger, TimeProvider.System)
	{
	}

	internal LibraryIndex(IJellyfinClient client, HomeCache homeCache, string folder, IObservable<IOutboundWebSocketMessage> serverMessages, ILogger logger, TimeProvider time)
	{
		_client = client;
		_homeCache = homeCache;
		_logger = logger;
		_time = time;
		_files = new UserFiles(folder, homeCache, logger);
		_files.Deleted += Drop;
		_subscription = serverMessages.Subscribe(message =>
		{
			if (message is LibraryChangedMessage)
			{
				MarkStale();
			}
		});
	}

	/// <summary>A new index came in (from the server, or the saved one); raised off the UI thread.</summary>
	public event EventHandler<IndexSnapshot>? Changed;

	/// <summary>How long the last read took, for the log and the time-budget test.</summary>
	public TimeSpan LastReadTook { get; private set; }

	private string CurrentKey => HomeCache.FileKey(_client.BaseUrl, _client.UserId);

	/// <summary>The index in memory for the signed-in user; null until it is read.</summary>
	public IndexSnapshot? Current
	{
		get
		{
			lock (_lock)
			{
				return _memoryKey == CurrentKey ? _memory : null;
			}
		}
	}

	/// <summary>The server reported a library change: the next <see cref="GetAsync"/> reads it again (the old one shows meanwhile).</summary>
	public void MarkStale()
	{
		lock (_lock)
		{
			_stale = true;
		}
	}

	/// <summary>
	/// The index for the signed-in user: in memory, else the saved one, else read from the server now (null when that
	/// failed). One that is stale (a day old, a library change, a saved one) is read again in the background, and
	/// <see cref="Changed"/> says when it is in.
	/// </summary>
	public async Task<IndexSnapshot?> GetAsync(CancellationToken cancellationToken = default)
	{
		var key = CurrentKey;
		IndexSnapshot? known;
		bool stale;
		lock (_lock)
		{
			known = _memoryKey == key ? _memory : null;
			stale = _stale;
		}

		if (known is null && await Load(key).ConfigureAwait(false) is { } saved)
		{
			lock (_lock)
			{
				if (_memoryKey != key || _memory is null)
				{
					(_memoryKey, _memory) = (key, saved);
				}

				known = _memory;
			}

			Changed?.Invoke(this, saved);
			stale = true; // a saved index is read again, whatever its age
		}

		if (known is null)
		{
			return await Read(key).WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		if (stale || _time.GetUtcNow() - known.ReadAt > FreshFor)
		{
			_ = Read(key);
		}

		return known;
	}

	/// <summary>R: the index read again now; the one in memory stays until the new one is in.</summary>
	public Task<IndexSnapshot?> RefreshAsync() => Read(CurrentKey);

	// one read at a time per user; a read for another user cancels the one running
	private Task<IndexSnapshot?> Read(string key)
	{
		lock (_lock)
		{
			if (_reading is { IsCompleted: false } running && _readingKey == key)
			{
				return running;
			}

			_readingCancel?.Cancel();
			_readingCancel = new CancellationTokenSource();
			_readingKey = key;
			_stale = false;
			var cancellation = _readingCancel.Token;
			var url = _client.BaseUrl;
			var user = _client.UserId;
			var since = _files.Generation; // 1.6.1: a sign-out after this refuses the read's write-back
			return _reading = Task.Run(() => ReadFromServer(key, url, user, since, cancellation), CancellationToken.None);
		}
	}

	private async Task<IndexSnapshot?> ReadFromServer(string key, string url, Guid user, long since, CancellationToken cancellationToken)
	{
		var watch = Stopwatch.StartNew();
		try
		{
			var snapshot = await ReadIndex(_client, _time, cancellationToken, _logger).ConfigureAwait(false);
			if (snapshot is null || cancellationToken.IsCancellationRequested || key != CurrentKey)
			{
				return null;
			}

			LastReadTook = watch.Elapsed;
			_logger.LogInformation("Library index: {Titles} titles in {Libraries} libraries, people {People}, {Seconds:0.0} s",
				snapshot.Entries.Count, snapshot.Libraries.Count, snapshot.HasPeople, watch.Elapsed.TotalSeconds);
			lock (_lock)
			{
				(_memoryKey, _memory) = (key, snapshot);
			}

			Changed?.Invoke(this, snapshot);
			await _files.Write(key, ToJson(snapshot), _homeCache.LoginKey(url, user), since).ConfigureAwait(false);
			return snapshot;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Library index: the read failed");
			return null;
		}
	}

	/// <summary>
	/// The whole read: the media libraries, their index pages, the people pass and the played episodes. Null when the
	/// libraries or an index page could not be read (the index in memory stays); a failed people or played pass leaves those
	/// kinds out.
	/// </summary>
	internal static async Task<IndexSnapshot?> ReadIndex(IJellyfinClient client, TimeProvider time, CancellationToken cancellationToken, ILogger? log = null)
	{
		var views = await client.GetUserViewsOrNull().ConfigureAwait(false);
		if (views is null)
		{
			return null;
		}

		var libraries = views.Where(v => v.Id is not null && JellyfinClient.IndexKinds(v) is not null).ToList();
		var entries = new List<IndexEntry>();
		var hasPeople = true;
		foreach (var library in libraries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var reader = new LibraryPageReader(library, (start, limit, total, ct) => client.GetLibraryIndexPage(library, start, limit, total, ct)) { Log = log };
			if (await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false) is not { } items)
			{
				return null;
			}

			var read = items.Select(x => IndexEntry.From(x, library.Id!.Value)).OfType<IndexEntry>().ToList();
			// the people pass: directors and the first actors, by id
			var peopleReader = new LibraryPageReader(library, (start, limit, total, ct) => client.GetLibraryPeoplePage(library, start, limit, total, ct)) { Log = log };
			if (await peopleReader.ReadAllAsync(cancellationToken).ConfigureAwait(false) is { } withPeople)
			{
				var people = withPeople.Where(x => x.Id is not null).GroupBy(x => x.Id!.Value).ToDictionary(g => g.Key, g => IndexEntry.PeopleOf(g.First()));
				read = [.. read.Select(x => people.TryGetValue(x.Id, out var p) ? x with { People = p } : x)];
			}
			else
			{
				hasPeople = false;
			}

			entries.AddRange(read);
		}

		var plays = new Dictionary<Guid, SeriesPlays>();
		if (libraries.Any(l => l.CollectionType == BaseItemDto_CollectionType.Tvshows))
		{
			var played = new BaseItemDto { Name = "played episodes" };
			var reader = new LibraryPageReader(played, (start, limit, total, ct) => client.GetPlayedEpisodesPage(start, limit, total, ct)) { Log = log };
			if (await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false) is { } episodes)
			{
				foreach (var show in episodes.Where(x => x.SeriesId is not null).GroupBy(x => x.SeriesId!.Value))
				{
					var last = show.Select(x => x.UserData?.LastPlayedDate).Where(x => x is not null).DefaultIfEmpty(null).Max();
					plays[show.Key] = new SeriesPlays(show.Count(), last ?? DateTimeOffset.MinValue);
				}
			}
		}

		return new IndexSnapshot([.. libraries.Select(l => new IndexLibrary(l.Id!.Value, l.Name ?? "", l.CollectionType == BaseItemDto_CollectionType.Movies ? IndexKind.Movie : IndexKind.Series))],
			entries, plays, hasPeople, time.GetUtcNow());
	}

	private void Drop()
	{
		lock (_lock)
		{
			// a sign-out or a removed login: nothing kept in memory for anyone until the next read
			_readingCancel?.Cancel();
			_memoryKey = null;
			_memory = null;
		}

		Forgotten?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>A user signed out or a saved login was removed: what was made of an index (the presets) goes too.</summary>
	public event EventHandler? Forgotten;

	private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

	private JsonObject ToJson(IndexSnapshot snapshot) => new()
	{
		["Version"] = FormatVersion,
		["ReadAt"] = snapshot.ReadAt.ToString("O"),
		["HasPeople"] = snapshot.HasPeople,
		["Libraries"] = JsonSerializer.SerializeToNode(snapshot.Libraries, Json),
		["Entries"] = JsonSerializer.SerializeToNode(snapshot.Entries, Json),
		["SeriesPlays"] = JsonSerializer.SerializeToNode(snapshot.SeriesPlays, Json),
	};

	private async Task<IndexSnapshot?> Load(string key)
	{
		if (await _files.Read(key).ConfigureAwait(false) is not { } root)
		{
			return null;
		}

		try
		{
			if (root["Version"]?.GetValue<int>() != FormatVersion)
			{
				return null;
			}

			return new IndexSnapshot(
				root["Libraries"].Deserialize<List<IndexLibrary>>(Json) ?? [],
				root["Entries"].Deserialize<List<IndexEntry>>(Json) ?? [],
				root["SeriesPlays"].Deserialize<Dictionary<Guid, SeriesPlays>>(Json) ?? [],
				root["HasPeople"]?.GetValue<bool>() == true,
				DateTimeOffset.Parse(root["ReadAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
		}
		catch (Exception ex)
		{
			_logger.LogInformation(ex, "Ignoring a saved library index");
			return null;
		}
	}

	public void Dispose()
	{
		_subscription.Dispose();
		_readingCancel?.Cancel();
	}
}
