using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentFin.Core.Contracts.Services;

namespace FluentFin.Core.Services;

public class LocalSettingsService : ILocalSettingsService
{
	// Nixie 1.8.1 (BH-181-M1): one lock around every read and write. A first start's two threads read the missing
	// CollectionsKindsOff at once (the index's publish and Home's fill); both wrote the default into this one JsonObject, which
	// left a property with no name, and every later save and missing-key read threw for the rest of the run
	private readonly Lock _gate = new();
	private readonly JsonObject _settings = [];
	private readonly string _file;
	private readonly JsonSerializerOptions _options;
	public LocalSettingsService(KnownFolders knownFolders)
	{
		_options = new JsonSerializerOptions()
		{
			WriteIndented = true,
			Converters =
			{
				new JsonStringEnumConverter()
			},
			NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
		};

		_file = Path.Combine(knownFolders.ApplicationData, KnownFolders.SettingsFileName);
		try
		{
			if (File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is { } jsonNode)
			{
				_settings = jsonNode.AsObject();
				// Nixie 1.8.1 (BH-181-M1): a parsed JsonObject builds its dictionary on first use, and two first uses at once race
				// too: build it here (Count builds it), before any other thread can see this service
				_ = _settings.Count;
			}
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			// keep the unreadable file for recovery and start with defaults instead of failing every launch
			File.Copy(_file, _file + ".corrupt", true);
		}
	}

	public T ReadSetting<T>(string key, T defaultValue)
	{
		// Nixie 1.8.1 (BH-181-M1): under the lock; a missing key's default is still written, under the same lock
		lock (_gate)
		{
			if (!_settings.TryGetPropertyValue(key, out var node))
			{
				SaveLocked(key, Parse(defaultValue));
				return defaultValue;
			}

			return node.Deserialize<T>(_options) ?? defaultValue;
		}
	}

	public JsonNode? ReadSettingRaw(string key)
	{
		// Nixie 1.8.1 (BH-181-M1): a copy, taken under the lock, so a caller reads it on its own thread while another saves
		lock (_gate)
		{
			return _settings.TryGetPropertyValue(key, out var node) ? node?.DeepClone() : null;
		}
	}

	public void RemoveSetting(string key)
	{
		lock (_gate)
		{
			_settings.Remove(key);
		}
	}

	public void SaveSetting<T>(string key, T value)
	{
		var node = Parse(value);
		lock (_gate)
		{
			SaveLocked(key, node);
		}
	}

	private JsonNode? Parse<T>(T value) => JsonNode.Parse(JsonSerializer.Serialize(value, _options));

	// Nixie 1.8.1 (BH-181-M1): with _gate held. Each write has its own temp file (a second window of the app shares this
	// file), and the replace stays one atomic move
	private void SaveLocked(string key, JsonNode? node)
	{
		_settings[key] = node;
		// write then rename, so a crash mid-write can't leave a truncated settings file
		var tempFile = $"{_file}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.WriteAllText(tempFile, _settings.ToJsonString(_options));
			File.Move(tempFile, _file, true);
		}
		catch
		{
			TryDelete(tempFile);
			throw;
		}
	}

	private static void TryDelete(string file)
	{
		try
		{
			File.Delete(file);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// a temp file left behind holds no setting; the next write has its own
		}
	}

	public byte[] GetEntropyBytes()
	{
		return ReadSetting("Entropy", RandomNumberGenerator.GetBytes(20));
	}
}
