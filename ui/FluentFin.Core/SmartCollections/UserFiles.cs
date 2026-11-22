using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Services;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6: one JSON file per server and user in a folder of the data folder (the library index, the saved collections),
/// named as <see cref="HomeCache"/> names its files (hashed), carrying the saved login it was written for, and deleted
/// when that user signs out or that login is removed (with any file whose login is unknown, as HomeCache does).
/// </summary>
internal sealed class UserFiles
{
	private readonly string _folder;
	private readonly ILogger _logger;
	private readonly SemaphoreSlim _writing = new(1, 1);

	public UserFiles(string folder, HomeCache homeCache, ILogger logger)
	{
		_folder = folder;
		_logger = logger;
		homeCache.Forgetting += (url, user) =>
		{
			Forgot(HomeCache.FileKey(url, user), login: null); // 1.6.1: before the delete, so a write that waits for it is refused
			_ = Delete(path => Path.GetFileNameWithoutExtension(path).StartsWith(HomeCache.FileKey(url, user), StringComparison.OrdinalIgnoreCase));
		};
		homeCache.ForgettingLogin += login =>
		{
			Forgot(key: null, login);
			_ = Delete(path => SavedLogin(path) is not { } saved || saved == login);
		};
	}

	// Nixie 1.6.1 (the 1.6 review: "a write-back can race sign-out"): a read that began before a sign-out finished after it,
	// and its write, waiting for the sign-out's delete, wrote the signed-out user's file again. Each sign-out (and each removed
	// login) is numbered; a write says which number it began after (Generation) and is refused when its user or login was
	// forgotten since. A later sign-in reads again, with a newer number.
	private long _generation;
	private readonly List<(string? Key, string? Login, long At)> _forgotten = [];

	/// <summary>Nixie 1.6.1: the number of the last sign-out or removed login (a read takes it when it begins).</summary>
	public long Generation => Interlocked.Read(ref _generation);

	private void Forgot(string? key, string? login)
	{
		lock (_forgotten)
		{
			_forgotten.Add((key, login, Interlocked.Increment(ref _generation)));
		}
	}

	/// <summary>Whether <paramref name="key"/>'s user, or <paramref name="login"/>, was forgotten after <paramref name="since"/>.</summary>
	internal bool ForgottenSince(string key, string? login, long since)
	{
		lock (_forgotten)
		{
			return _forgotten.Any(f => f.At > since
				&& ((f.Key is { } k && key.StartsWith(k, StringComparison.OrdinalIgnoreCase)) || (f.Login is { } l && (login is null || l == login))));
		}
	}

	/// <summary>Raised when files were deleted (a sign-out, a removed login): what is in memory for them goes too.</summary>
	public event Action? Deleted;

	public string PathOf(string key) => Path.Combine(_folder, key + ".json");

	/// <param name="since">1.6.1: the <see cref="Generation"/> the data was read after; null: written whatever came since.</param>
	public async Task Write(string key, JsonObject root, string? login, long? since = null)
	{
		root["Login"] = login;
		await _writing.WaitAsync().ConfigureAwait(false);
		try
		{
			if (since is { } began && ForgottenSince(key, login, began))
			{
				_logger.LogInformation("Not saving {File}: its user signed out while it was read", Path.GetFileName(_folder));
				return;
			}

			await Task.Run(() =>
			{
				Directory.CreateDirectory(_folder);
				var path = PathOf(key);
				var temporary = path + ".tmp";
				File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(root));
				File.Move(temporary, path, overwrite: true);
			}).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not save {File}", Path.GetFileName(_folder));
		}
		finally
		{
			_writing.Release();
		}
	}

	public async Task<JsonObject?> Read(string key)
	{
		var path = PathOf(key);
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return await Task.Run(() => JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogInformation(ex, "Ignoring a damaged {File} file", Path.GetFileName(_folder));
			return null;
		}
	}

	private async Task Delete(Func<string, bool> which)
	{
		await _writing.WaitAsync().ConfigureAwait(false);
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
			}).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not delete a saved {File} file", Path.GetFileName(_folder));
		}
		finally
		{
			_writing.Release();
		}

		Deleted?.Invoke();
	}

	private static string? SavedLogin(string path)
	{
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllBytes(path));
			return document.RootElement.TryGetProperty("Login", out var login) && login.ValueKind == JsonValueKind.String ? login.GetString() : null;
		}
		catch
		{
			return null;
		}
	}
}
