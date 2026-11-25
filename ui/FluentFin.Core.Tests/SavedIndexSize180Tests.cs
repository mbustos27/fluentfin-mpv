using System.Diagnostics;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 review (Major 2): what the people step adds to the saved index, at real size (25,738 films, a director and three
/// actors each, names of real length), through the index's own save and load: the file's size, and how long a start takes
/// to show the saved index (what Home waits for first). Logged; the bounds are wide (a loaded test machine).
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class SavedIndexSize180Tests(ITestOutputHelper output) : IDisposable
{
	private const int Films = 25_738;
	private static readonly string[] First = ["Maria", "James", "Yuki", "Oluwaseun", "Ana", "Henrik", "Priya", "Tom", "Isabelle", "Kenji", "Lucia", "Ahmed", "Sofia", "Michael", "Chloé", "Dmitri"];
	private static readonly string[] Last = ["Fernández", "O'Connor", "Takahashi", "Adeyemi", "Kowalski", "Lindqvist", "Ramanathan", "Hughes", "Moreau", "Watanabe", "Bianchi", "Haddad", "Petrova", "Johnson", "Dubois", "Novak", "Okafor", "Silva"];
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-size180-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();

	static SavedIndexSize180Tests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public void Dispose() => TestFolder.Delete(_folder);

	private static string Person(int n) => $"{First[n % First.Length]} {Last[(n / First.Length) % Last.Length]}";

	private static BaseItemDto Film(int i) => new()
	{
		Id = Guid.Parse($"{i:D8}-0180-0000-0000-000000000000"),
		Name = $"A Film Title of Real Length {i}",
		SortName = $"film title of real length {i}",
		Type = BaseItemDto_Type.Movie,
		ProductionYear = 1950 + (i % 75),
		PremiereDate = new DateTimeOffset(1950 + (i % 75), 1 + (i % 12), 1 + (i % 28), 0, 0, 0, TimeSpan.Zero),
		CommunityRating = 5 + (i % 50 / 10f),
		CriticRating = i % 3 == 0 ? 60 + (i % 40) : null,
		OfficialRating = "PG-13",
		RunTimeTicks = TimeSpan.FromMinutes(80 + (i % 70)).Ticks,
		Genres = ["Drama", i % 2 == 0 ? "Thriller" : "Comedy"],
		Tags = i % 5 == 0 ? ["heist"] : [],
		Studios = [new NameGuidPair { Name = $"Studio Name {i % 300}" }],
		ProductionLocations = ["United States of America"],
		DateCreated = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
		ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = $"{100000 + i}", ["Imdb"] = $"tt{1000000 + i}" } },
		ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = $"{i:x8}0123456789abcdef01234567" } },
		ImageBlurHashes = new BaseItemDto_ImageBlurHashes { Primary = new BaseItemDto_ImageBlurHashes_Primary { AdditionalData = new Dictionary<string, object> { [$"{i:x8}0123456789abcdef01234567"] = "WEHV6nWB2yk8pyo0adR*.7kCMdnjS#M|%1%2Sis.slNHRjt7ayof" } } },
		UserData = new UserItemDataDto { Played = i % 40 == 0, PlayCount = i % 40 == 0 ? 1 : 0, LastPlayedDate = i % 40 == 0 ? new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero) : null },
	};

	private static BaseItemDto WithPeople(BaseItemDto film, int i, bool people) => new()
	{
		Id = film.Id,
		Type = BaseItemDto_Type.Movie,
		People = people
			? [new BaseItemPerson { Name = Person(i % 4_000), Id = Guid.NewGuid(), Type = BaseItemPerson_Type.Director },
				.. Enumerable.Range(1, 3).Select(k => new BaseItemPerson { Name = Person((i * 7 * k) % 30_000), Id = Guid.NewGuid(), Type = BaseItemPerson_Type.Actor })]
			: [],
	};

	// the saved index's size and its load at a start (the median of three), after a first read with or without people
	private async Task<(long Bytes, long LoadMs, int WithPeople)> Measure(string name, bool people)
	{
		var films = Enumerable.Range(0, Films).Select(Film).ToList();
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns("http://server");
		client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000180"));
		client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies }]);
		client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync($"{Films}:newest");
		client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. films.Skip(start).Take(limit)], TotalRecordCount = total ? films.Count : null });
		client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) => new BaseItemDtoQueryResult
			{
				Items = [.. films.Skip(start).Take(limit).Select((x, k) => WithPeople(x, start + k, people))],
				TotalRecordCount = total ? films.Count : null,
			});
		using var homeCache = new HomeCache(Path.Combine(_folder, name, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		var folder = Path.Combine(_folder, name, "LibraryIndex");
		using (var first = new LibraryIndex(client.Object, homeCache, folder, _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System))
		{
			Assert.NotNull(await first.GetAsync());
			await first.Settled;
			Assert.True(first.Current!.HasPeople);
		}

		var file = Directory.GetFiles(folder, "*.json").Single();
		var loads = new List<long>();
		var withPeople = 0;
		for (var run = 0; run < 3; run++)
		{
			using var start = new LibraryIndex(client.Object, homeCache, folder, _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
			var watch = Stopwatch.StartNew();
			var saved = await start.SavedAsync();
			loads.Add(watch.ElapsedMilliseconds);
			withPeople = saved!.Entries.Count(x => x.People.Length > 0);
			await start.Settled;
		}

		return (new FileInfo(file).Length, loads.Order().ElementAt(1), withPeople);
	}

	[Fact]
	public async Task TheSavedIndex_WithItsPeople_AtRealSize_StaysSmallAndQuickToShow()
	{
		var none = await Measure("none", people: false);
		var with = await Measure("people", people: true);

		var perTitle = (with.Bytes - none.Bytes) / (double)Films;
		output.WriteLine($"{Films:N0} films: saved index {none.Bytes / 1048576.0:0.0} MB without people, {with.Bytes / 1048576.0:0.0} MB with them ({perTitle:0} bytes a title more); "
			+ $"the saved index shown at a start in {none.LoadMs} ms without people, {with.LoadMs} ms with them (median of three)");
		Assert.Equal(Films, with.WithPeople);
		Assert.InRange(perTitle, 1, 200); // a director and three actors, names alone (the server's person ids aren't kept: nothing reads them)
		Assert.InRange(with.LoadMs, 0, Math.Max(3_000, none.LoadMs * 3)); // a start shows it without a wait Home would notice
	}
}
