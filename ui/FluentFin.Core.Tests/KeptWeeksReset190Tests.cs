using System.Text.Json.Nodes;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the rebuild, before b3): a week kept on this PC holds to its Sunday, so the owner's b2 week (its categories and
/// its Spotlight, chosen before the rebuild) would have shown on b3 until Monday. Weeks saved without the store's
/// <see cref="SmartCollectionStore.WeeksRevision"/> are dropped once; the saved filters, favourites and pins stay.
/// </summary>
public sealed class KeptWeeksReset190Tests : IDisposable
{
	private const string Server = "http://127.0.0.1:8096";
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-weeks190-" + Guid.NewGuid().ToString("N"));
	private readonly HomeCache _home;
	private readonly Moq.Mock<Contracts.Services.IJellyfinClient> _client = new();
	private readonly Guid _user = Guid.NewGuid();

	public KeptWeeksReset190Tests()
	{
		_client.SetupGet(x => x.BaseUrl).Returns(Server);
		_client.SetupGet(x => x.UserId).Returns(_user);
		_home = new HomeCache(Path.Combine(_folder, "home"), new System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
	}

	public void Dispose()
	{
		_home.Dispose();
		TestFolder.Delete(_folder);
	}

	private string Key => HomeCache.FileKey(Server, _user);

	private string File => Path.Combine(_folder, "sc", Key + ".json");

	// a new store on the folder: the app's next start
	private SmartCollectionStore Store() => new(_client.Object, _home, Path.Combine(_folder, "sc"), new CoreListLogger<SmartCollectionStore>());

	private JsonObject Saved() => (JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(File))!;

	[Fact]
	public async Task WeeksKeptBeforeTheRebuild_AreDroppedOnce_TheFavouritesStay_AWeekKeptAfterHolds()
	{
		// b2's file: week 48's categories and Spotlight pick, and a favourite; no revision of the weeks
		Directory.CreateDirectory(Path.GetDirectoryName(File)!);
		await System.IO.File.WriteAllTextAsync(File, """
			{ "Version": 1, "Filters": [], "Favorites": ["saved:late-night"], "Spotlight": { "2026-W48": "spotlight:actor:b2" },
			  "PinnedTitles": { "spotlight:actor:b2": "Spotlight: B2" },
			  "Categories": { "2026-W48": { "Ids": ["micro:b2-a", "micro:b2-b"], "Explore": "micro:b2-b" } } }
			""");

		var store = Store();
		Assert.Empty(await store.CategoryWeeksAsync(Key)); // the rebuild chooses this week itself
		Assert.Empty(await store.SpotlightPicksAsync(Key));
		var stamped = Saved();
		Assert.Equal(SmartCollectionStore.WeeksRevision, stamped["WeeksRevision"]!.GetValue<int>()); // written at once: the reset is once
		Assert.Equal(["saved:late-night"], stamped["Favorites"]!.AsArray().Select(x => x!.GetValue<string>()));
		Assert.Equal("Spotlight: B2", stamped["PinnedTitles"]!["spotlight:actor:b2"]!.GetValue<string>()); // a pin keeps its name

		await store.SaveCategoryWeek("2026-W48", new CategoryWeek(["micro:new"], "micro:new"), Key);
		await store.SaveSpotlightPick("2026-W48", "spotlight:actor:new", Key);

		// the next start: what the rebuild kept holds, nothing dropped again
		var next = Store();
		Assert.Equal(["micro:new"], (await next.CategoryWeeksAsync(Key))["2026-W48"].Ids);
		Assert.Equal("spotlight:actor:new", (await next.SpotlightPicksAsync(Key))["2026-W48"]);
	}

	[Fact]
	public async Task ANewUser_HasNothingToDrop_AndTheirFirstWeekIsStamped()
	{
		var store = Store();
		Assert.Empty(await store.CategoryWeeksAsync(Key));
		Assert.False(System.IO.File.Exists(File)); // nothing written for nothing

		await store.SaveCategoryWeek("2026-W48", new CategoryWeek(["micro:first"]), Key);

		Assert.Equal(SmartCollectionStore.WeeksRevision, Saved()["WeeksRevision"]!.GetValue<int>());
		Assert.Equal(["micro:first"], (await Store().CategoryWeeksAsync(Key))["2026-W48"].Ids);
	}
}
