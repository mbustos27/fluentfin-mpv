using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9, the review of the ui stream (Minor 1): the categories' one-time step (<see cref="CollectionSettings.Migrate"/>) runs
/// at every start before the first page, over the real settings file. A setting of the wrong kind there ("CollectionsKindsOff":
/// 5, "CollectionsCategoriesSettled": "yes" or null) threw out of it, and Nixie failed to start, every time. Now it is said (the
/// exception's type) and the step is left for the next start, not marked done. With a sound file it does its work and keeps it.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class CategoriesMigrate190Tests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nixie-190-migrate-" + Guid.NewGuid().ToString("N"));

	private string SettingsFile => Path.Combine(_root, "LocalSettings.json");

	public void Dispose() => TestFolder.Delete(_root);

	private LocalSettingsService WithFile(string json)
	{
		Directory.CreateDirectory(_root);
		File.WriteAllText(SettingsFile, json);
		return new LocalSettingsService(new KnownFolders(_root));
	}

	[Theory]
	[InlineData("{ \"CollectionsKindsOff\": 5 }")]
	[InlineData("{ \"CollectionsCategoriesSettled\": \"yes\" }")]
	[InlineData("{ \"CollectionsCategoriesSettled\": null, \"CollectionsKindsOff\": [1, 2] }")]
	[InlineData("{ \"CollectionsKindsOff\": { \"Franchise\": true } }")]
	public void ASettingOfTheWrongKind_NeverStopsTheStart_AndTheStepWaitsForTheNext(string json)
	{
		var settings = WithFile(json);
		var log = new CoreListLogger<CategoriesMigrate190Tests>();

		var thrown = Record.Exception(() => CollectionSettings.Migrate(settings, log));

		Assert.Null(thrown);
		// said once, by the exception's type only, and not marked done: the next start tries again
		var said = Assert.Single(log.Entries, x => x.Message.StartsWith("Collections: the categories' one-time step couldn't", StringComparison.Ordinal));
		Assert.DoesNotContain("\"CollectionsCategoriesSettled\":true", File.ReadAllText(SettingsFile).Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
		Assert.Matches(@"\((JsonException|InvalidOperationException|IOException|UnauthorizedAccessException)\)", said.Message);
	}

	[Fact]
	public void WithASoundFile_EveryKindOffIn181_KeepsTheCategoriesOff_AcrossARestart_AndAFreshFileStaysOn()
	{
		var settings = WithFile("{ \"CollectionsKindsOff\": \"" + string.Join(",", CollectionSettings.Kinds181) + "\" }");
		CollectionSettings.Migrate(settings);

		var again = new LocalSettingsService(new KnownFolders(_root)); // the next start
		Assert.Contains(SmartCollectionKind.MicroGenre, CollectionSettings.KindsOff(again));
		Assert.True(again.ReadSetting(CollectionSettingKeys.CategoriesSettled));

		File.Delete(SettingsFile);
		var fresh = new LocalSettingsService(new KnownFolders(_root));
		CollectionSettings.Migrate(fresh);
		var next = new LocalSettingsService(new KnownFolders(_root));
		Assert.Empty(CollectionSettings.KindsOff(next));
		Assert.True(next.ReadSetting(CollectionSettingKeys.CategoriesSettled));
	}
}
