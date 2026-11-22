using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluentFin.Core.Outside;

/// <summary>1.7: one cached public list: which source and list, when it was fetched, and its bytes as the source sent them.</summary>
public sealed record OutsideCacheEntry(OutsideSourceId Source, string Name, DateTimeOffset FetchedAt, byte[] Data)
{
	public TimeSpan Age(DateTimeOffset now) => now - FetchedAt;
}

/// <summary>
/// 1.7: the public lists Nixie fetched, in its own data folder (<c>&lt;data&gt;\Outside\&lt;source&gt;\</c>): kept apart from any
/// server or user, because they're the same for everyone (plan §3), each with its fetch date. A list is fresh for its source's
/// <see cref="OutsideSourceInfo.RefreshAfter"/> and never used, and deleted, past its <see cref="OutsideSourceInfo.MaxAge"/>
/// (TMDb: 6 months at most). The ages come from today's rules, not from the file, so an edited file can't keep a list longer.
/// </summary>
public sealed partial class OutsideCache(string root, Func<DateTimeOffset>? clock = null)
{
	/// <summary>The cache's folder under Nixie's data folder.</summary>
	public const string FolderName = "Outside";

	private const string DataSuffix = ".dat";
	private const string MetaSuffix = ".meta.json";
	private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
	private readonly Lock _lock = new();

	/// <summary>The cache in <paramref name="folders"/>' data folder (a <c>--data-dir</c> run's own, never the user's).</summary>
	public static OutsideCache For(KnownFolders folders) => new(Path.Combine(folders.ApplicationData, FolderName));

	public string Root => root;

	[GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,95}$")]
	private static partial Regex NameRule();

	/// <summary>A list's name: lowercase letters, digits, '.', '-' and '_' ("collections", "awards-best-picture").</summary>
	public static bool IsValidName(string? name) => name is not null && NameRule().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

	private string Folder(OutsideSourceInfo source) => Path.Combine(root, source.Key);

	private string DataPath(OutsideSourceInfo source, string name) => Path.Combine(Folder(source), name + DataSuffix);

	private string MetaPath(OutsideSourceInfo source, string name) => Path.Combine(Folder(source), name + MetaSuffix);

	/// <summary>The cached list, or null when there is none or it is past its source's <see cref="OutsideSourceInfo.MaxAge"/> (then it is deleted).</summary>
	public OutsideCacheEntry? Read(OutsideSourceInfo source, string name)
	{
		Check(name);
		lock (_lock)
		{
			try
			{
				var meta = MetaPath(source, name);
				var data = DataPath(source, name);
				if (!File.Exists(meta) || !File.Exists(data))
				{
					return null;
				}

				var fetched = ReadFetched(meta);
				var now = _clock();
				if (fetched is not { } at || now - at > source.MaxAge || at > now + TimeSpan.FromDays(1))
				{
					Delete(source, name);
					return null;
				}

				return new OutsideCacheEntry(source.Id, name, at, File.ReadAllBytes(data));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}
	}

	/// <summary>Whether <paramref name="entry"/> is still fresh: younger than its source's <see cref="OutsideSourceInfo.RefreshAfter"/>.</summary>
	public bool IsFresh(OutsideCacheEntry entry) => entry.Age(_clock()) < OutsideSources.Get(entry.Source).RefreshAfter;

	/// <summary>Keeps <paramref name="data"/> as the list <paramref name="name"/> of <paramref name="source"/>, fetched now (or <paramref name="fetchedAt"/>).</summary>
	public void Write(OutsideSourceInfo source, string name, byte[] data, DateTimeOffset? fetchedAt = null)
	{
		Check(name);
		lock (_lock)
		{
			Directory.CreateDirectory(Folder(source));
			var dataPath = DataPath(source, name);
			var metaPath = MetaPath(source, name);
			var tmp = dataPath + ".tmp";
			File.WriteAllBytes(tmp, data);
			File.Move(tmp, dataPath, overwrite: true);
			var meta = JsonSerializer.Serialize(new Meta(source.Key, name, (fetchedAt ?? _clock()).ToUniversalTime()));
			File.WriteAllText(metaPath + ".tmp", meta);
			File.Move(metaPath + ".tmp", metaPath, overwrite: true);
		}
	}

	/// <summary>
	/// <see cref="Write"/>, false instead of an exception when the folder can't be written (a full disk, no access): the list
	/// fetched is still used for this run (review #3).
	/// </summary>
	public bool TryWrite(OutsideSourceInfo source, string name, byte[] data, DateTimeOffset? fetchedAt = null)
	{
		try
		{
			Write(source, name, data, fetchedAt);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>Deletes every list past its source's <see cref="OutsideSourceInfo.MaxAge"/>, and anything in the folder that isn't a list; the count deleted.</summary>
	public int Purge()
	{
		var deleted = 0;
		lock (_lock)
		{
			foreach (var source in OutsideSources.All)
			{
				var folder = Folder(source);
				if (!Directory.Exists(folder))
				{
					continue;
				}

				try
				{
					foreach (var file in Directory.GetFiles(folder))
					{
						var fileName = Path.GetFileName(file);
						var name = fileName.EndsWith(MetaSuffix, StringComparison.Ordinal) ? fileName[..^MetaSuffix.Length]
							: fileName.EndsWith(DataSuffix, StringComparison.Ordinal) ? fileName[..^DataSuffix.Length]
							: null;
						var keep = name is not null && IsValidName(name) && File.Exists(MetaPath(source, name)) && File.Exists(DataPath(source, name))
							&& ReadFetched(MetaPath(source, name)) is { } at && _clock() - at <= source.MaxAge;
						if (!keep && File.Exists(file))
						{
							File.Delete(file);
							deleted++;
						}
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					// the next start tries again
				}
			}
		}

		return deleted;
	}

	/// <summary>Deletes the cached lists of <paramref name="source"/>, or of every source.</summary>
	public void Clear(OutsideSourceId? source = null)
	{
		lock (_lock)
		{
			foreach (var info in OutsideSources.All.Where(x => source is null || x.Id == source))
			{
				if (Directory.Exists(Folder(info)))
				{
					Directory.Delete(Folder(info), recursive: true);
				}
			}
		}
	}

	private void Delete(OutsideSourceInfo source, string name)
	{
		File.Delete(DataPath(source, name));
		File.Delete(MetaPath(source, name));
	}

	private static DateTimeOffset? ReadFetched(string metaPath)
	{
		try
		{
			return JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath))?.FetchedUtc;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static void Check(string name)
	{
		if (!IsValidName(name))
		{
			throw new ArgumentException($"Not a list name: {name}", nameof(name));
		}
	}

	private sealed record Meta(string Source, string Name, DateTimeOffset FetchedUtc);
}
