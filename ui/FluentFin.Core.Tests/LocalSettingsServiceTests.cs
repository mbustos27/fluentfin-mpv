using FluentFin.Core;
using FluentFin.Core.Services;

namespace FluentFin.Core.Tests;

public sealed class LocalSettingsServiceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));
	private string SettingsFile => Path.Combine(_root, "LocalSettings.json");

	public void Dispose() => Directory.Delete(_root, true);

	[Fact]
	public void SavedValue_SurvivesRestart_AndLeavesNoTempFile()
	{
		new LocalSettingsService(new KnownFolders(_root)).SaveSetting("Volume", 42);

		var reloaded = new LocalSettingsService(new KnownFolders(_root));

		Assert.Equal(42, reloaded.ReadSetting("Volume", 0));
		Assert.False(File.Exists(SettingsFile + ".tmp"));
		Assert.Empty(Directory.GetFiles(_root, "*.tmp")); // 1.8.1 (BH-181-M1): each write's own temp file is gone too
	}

	// Nixie 1.8.1 (BH-181-M1): a first start's two threads read the missing CollectionsKindsOff at once (the index's publish and
	// Home's fill); both wrote the default into the one JsonObject, which left a property with no name, and every later save
	// and missing-key read threw for the rest of the run (no Made by Nixie, an empty Collections page, Movies and Shows dead)
	[Fact]
	public void ManyThreadsReadingMissingKeys_AndSaving_OnAParsedFile_NothingThrows_AndTheFileKeepsEveryKey()
	{
		const int threads = 8;
		const int reads = 200;
		Directory.CreateDirectory(_root);
		File.WriteAllText(SettingsFile, "{ \"Volume\": 42, \"Kept\": { \"a\": \"1\", \"b\": \"2\" }, \"Servers\": [ { \"Name\": \"one\" } ] }");
		var service = new LocalSettingsService(new KnownFolders(_root));
		using var start = new Barrier(threads);
		var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

		var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
		{
			try
			{
				start.SignalAndWait();
				for (var i = 0; i < reads; i++)
				{
					Assert.Equal(i, service.ReadSetting($"Missing{t}_{i}", i));
					if (i % 10 == 0)
					{
						service.SaveSetting($"Saved{t}_{i}", $"value {t} {i}");
						Assert.Equal("1", service.ReadSettingRaw("Kept")?["a"]?.GetValue<string>());
					}
				}
			}
			catch (Exception ex)
			{
				failures.Enqueue(ex);
			}
		})).ToList();
		workers.ForEach(x => x.Start());
		workers.ForEach(x => x.Join());

		Assert.Empty(failures);
		var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsFile))!.AsObject();
		Assert.Equal(3 + (threads * reads) + (threads * reads / 10), file.Count);
		Assert.All(file, x => Assert.False(string.IsNullOrEmpty(x.Key)));
		var reloaded = new LocalSettingsService(new KnownFolders(_root));
		for (var t = 0; t < threads; t++)
		{
			for (var i = 0; i < reads; i++)
			{
				Assert.Equal(i, file[$"Missing{t}_{i}"]!.GetValue<int>());
				Assert.Equal(i, reloaded.ReadSetting($"Missing{t}_{i}", -1));
				if (i % 10 == 0)
				{
					Assert.Equal($"value {t} {i}", reloaded.ReadSetting($"Saved{t}_{i}", ""));
				}
			}
		}

		Assert.Equal(42, reloaded.ReadSetting("Volume", 0));
		Assert.Equal("2", reloaded.ReadSettingRaw("Kept")?["b"]?.GetValue<string>());
		Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
	}

	// Nixie 1.8.1 (BH-181-M1): a raw read is a copy, so a caller reading it on its own thread never races a save
	[Fact]
	public void RawRead_IsACopy_TheSavedValueIsUnchanged()
	{
		var service = new LocalSettingsService(new KnownFolders(_root));
		service.SaveSetting("Map", new Dictionary<string, string> { ["a"] = "1" });

		var raw = service.ReadSettingRaw("Map")!.AsObject();
		raw["a"] = "changed";

		Assert.Equal("1", service.ReadSettingRaw("Map")?["a"]?.GetValue<string>());
		Assert.Null(service.ReadSettingRaw("Absent"));
	}

	[Theory]
	[InlineData("")]
	[InlineData("{\"Servers\": [ {\"Disp")]
	[InlineData("[1, 2]")]
	public void UnreadableFile_StartsWithDefaults_AndKeepsCopy(string content)
	{
		Directory.CreateDirectory(_root);
		File.WriteAllText(SettingsFile, content);

		var service = new LocalSettingsService(new KnownFolders(_root));

		Assert.Equal(7, service.ReadSetting("Volume", 7));
		Assert.Equal(content, File.ReadAllText(SettingsFile + ".corrupt"));
	}
}
