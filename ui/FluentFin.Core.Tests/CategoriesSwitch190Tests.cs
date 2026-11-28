using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 "Collections 2", the ui stream (the text stream's review, Major 1; rule 9): Settings' Categories switch turns the
/// shows' summaries pass (<see cref="TextPass"/>) off at once: off, the matches it kept on this PC
/// (<c>LibraryIndex\Summaries\*.json</c>) and in memory are deleted; on again, the pass reads them anew. Before, nothing
/// called <see cref="TextPass.Switched"/>: the pass stopped only at its next request and the file stayed until its next run.
/// </summary>
[Collection(CollectionNamesSerial.Name)] // TextPass.App is static
public sealed class CategoriesSwitch190Tests : IDisposable
{
	private static readonly Guid ShowsId = Guid.Parse("bbbbbbbb-2222-0000-0000-000000000190");
	private static readonly MicroGenreCatalogue Catalogue = new(
	[
		new MicroGenre("tv-space", "Space TV Shows", IndexKind.Series, "space", null, new MicroGenreRule { TextAny = ["starship"] }, "Its summary mentions a starship"),
	], []);

	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-190-switch-" + Guid.NewGuid().ToString("N"));
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly HomeCache _home;
	private readonly List<IDisposable> _made = [];
	private int _textRequests;

	static CategoriesSwitch190Tests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public CategoriesSwitch190Tests()
	{
		_home = new HomeCache(Path.Combine(_folder, "HomeCache"), new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000190"));
		List<BaseItemDto> shows = [.. Enumerable.Range(0, 30).Select(i => new BaseItemDto
		{
			Id = Guid.Parse($"{i:D8}-0002-0190-0000-000000000001"),
			Name = $"Show {i:D2}",
			Type = BaseItemDto_Type.Series,
			ProductionYear = 2000 + i,
			Genres = ["Drama"],
			UserData = new UserItemDataDto { Played = false },
		})];
		var view = new BaseItemDto { Id = ShowsId, Name = "Shows", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Tvshows };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([view]);
		_client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync("30:newest");
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. shows.Skip(start).Take(limit)], TotalRecordCount = total ? shows.Count : null });
		_client.Setup(x => x.GetPlayedEpisodesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		BaseItemDto Text(BaseItemDto show) => new() { Id = show.Id, Type = show.Type, Overview = $"A {(show.ProductionYear % 2 == 0 ? "starship" : "farm")} story.", Taglines = [] };
		_client.Setup(x => x.GetLibraryTextPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) =>
			{
				Interlocked.Increment(ref _textRequests);
				return new BaseItemDtoQueryResult { Items = [.. shows.Skip(start).Take(limit).Select(Text)], TotalRecordCount = total ? shows.Count : null };
			});
		_client.Setup(x => x.GetTextByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) =>
			{
				Interlocked.Increment(ref _textRequests);
				return (IReadOnlyList<BaseItemDto>?)[.. shows.Where(x => ids.Contains(x.Id!.Value)).Select(Text)];
			});
	}

	public void Dispose()
	{
		TextPass.App = null;
		foreach (var made in _made)
		{
			made.Dispose();
		}

		_home.Dispose();
		TestFolder.Delete(_folder);
	}

	private string[] Saved() => Directory.Exists(Path.Combine(_folder, "LibraryIndex", "Summaries")) ? Directory.GetFiles(Path.Combine(_folder, "LibraryIndex", "Summaries"), "*.json") : [];

	private static async Task Until(Func<bool> done, string what)
	{
		for (var i = 0; i < 400 && !done(); i++)
		{
			await Task.Delay(25);
		}

		Assert.True(done(), what);
	}

	// 1.9 (the pre-release nit): Home never shows a category, so the switch mustn't make Home ask the server for its page again
	[Fact]
	public void TheCategoriesSwitch_LeavesHomesPageAsItIs_AnotherKindsSwitchMarksItStale()
	{
		var settings = new MemorySettings();
		var kinds = new CollectionsSettingsViewModel(settings, _home).Kinds;
		var categories = kinds.Single(x => x.Name == "Categories");
		var franchises = kinds.Single(x => x.Name == SmartCollectionKinds.Plural(SmartCollectionKind.Franchise));
		var before = _home.ChangeCount;

		categories.IsOn = false;
		categories.IsOn = true;
		Assert.Equal(before, _home.ChangeCount);
		Assert.DoesNotContain(SmartCollectionKind.MicroGenre, CollectionSettings.KindsOff(settings)); // the switch itself still works

		franchises.IsOn = false; // Home shows franchise picks: its page is stale
		Assert.Equal(before + 1, _home.ChangeCount);
	}

	[Fact]
	public async Task TheCategoriesSwitchOff_DeletesTheSummariesMatchesAtOnce_AndOnReadsThemAgain()
	{
		var settings = new MemorySettings();
		var index = new LibraryIndex(_client.Object, _home, Path.Combine(_folder, "LibraryIndex"), new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<LibraryIndex>(), TimeProvider.System) { PeopleWanted = () => false };
		_made.Add(index);
		var pass = new TextPass(_client.Object, index, _home, Path.Combine(_folder, "LibraryIndex", "Summaries"), new CoreListLogger<TextPass>(), TimeProvider.System, new PauseGate(), shared: false)
		{
			// as the app's: on while Settings' Categories switch is (the off-list the switch writes)
			Wanted = () => !CollectionSettings.KindsOff(settings).Contains(TextPass.Kind),
			Catalogue = () => Catalogue,
			AfterFirstScreen = _ => Task.CompletedTask,
		};
		_made.Add(pass);
		TextPass.App = pass;
		await index.GetAsync();
		await Until(() => Saved().Length == 1 && pass.Current.Count > 0, "the pass saved its matches");

		var categories = new CollectionsSettingsViewModel(settings).Kinds.Single(x => x.Name == "Categories");
		Assert.Equal(SmartCollectionKind.MicroGenre, TextPass.Kind);
		categories.IsOn = false; // Settings > Collections > Made by Nixie collections > Categories, off

		await Until(() => Saved().Length == 0, "the matches' file deleted at once");
		Assert.Equal(0, pass.Current.Count);

		var asked = _textRequests;
		categories.IsOn = true; // and on: read again now
		await Until(() => Saved().Length == 1 && _textRequests > asked, "read again");
	}

	// the review's Minor 3 (its probe C, back to back): off then on while the pass reads. Off cancels the pass, which takes a
	// moment to end (a request's cancel); on came while it was still running, was taken for "again after this one", and the
	// cancelled loop ended without it: no pass ran until the next start
	private TaskCompletionSource? _block;
	private int _blocked;

	[Fact]
	public async Task OffThenOnAtOnce_WhileAPassReads_APassRunsAfterOn()
	{
		var shows = Enumerable.Range(0, 30).Select(i => new BaseItemDto
		{
			Id = Guid.Parse($"{i:D8}-0002-0190-0000-000000000001"),
			Type = BaseItemDto_Type.Series,
			ProductionYear = 2000 + i,
		}).ToList();
		BaseItemDto Text(BaseItemDto show) => new() { Id = show.Id, Type = show.Type, Overview = $"A {(show.ProductionYear % 2 == 0 ? "starship" : "farm")} story.", Taglines = [] };
		_block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_client.Setup(x => x.GetLibraryTextPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool total, CancellationToken ct) =>
			{
				Interlocked.Increment(ref _textRequests);
				if (_block is { } block)
				{
					Interlocked.Increment(ref _blocked);
					try
					{
						await block.Task.WaitAsync(ct);
					}
					catch (OperationCanceledException)
					{
						await Task.Delay(50, CancellationToken.None); // as a socket's cancel: it ends a moment later
						throw;
					}
				}

				return new BaseItemDtoQueryResult { Items = [.. shows.Skip(start).Take(limit).Select(Text)], TotalRecordCount = total ? shows.Count : null };
			});
		var settings = new MemorySettings();
		var index = new LibraryIndex(_client.Object, _home, Path.Combine(_folder, "LibraryIndex"), new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<LibraryIndex>(), TimeProvider.System) { PeopleWanted = () => false };
		_made.Add(index);
		var pass = new TextPass(_client.Object, index, _home, Path.Combine(_folder, "LibraryIndex", "Summaries"), new CoreListLogger<TextPass>(), TimeProvider.System, new PauseGate(), shared: false)
		{
			Wanted = () => !CollectionSettings.KindsOff(settings).Contains(TextPass.Kind),
			Catalogue = () => Catalogue,
			AfterFirstScreen = _ => Task.CompletedTask,
		};
		_made.Add(pass);
		TextPass.App = pass;
		await index.GetAsync();
		await Until(() => Volatile.Read(ref _blocked) > 0, "the first pass is reading");

		var categories = new CollectionsSettingsViewModel(settings).Kinds.Single(x => x.Name == "Categories");
		categories.IsOn = false;
		categories.IsOn = true; // back to back
		var block0 = _block;
		_block = null;
		block0!.TrySetResult();

		await Until(() => Saved().Length == 1 && pass.Current.Count > 0, "a pass ran after the switch came back on");
	}

	/// <summary>Settings in memory: what was saved, else the default asked for (as LocalSettingsService answers).</summary>
	private sealed class MemorySettings : ILocalSettingsService
	{
		private readonly Dictionary<string, object?> _values = [];

		public System.Text.Json.Nodes.JsonNode? ReadSettingRaw(string key) => null;

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = value;

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
