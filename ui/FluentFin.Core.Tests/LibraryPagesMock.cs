using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.1: a library behind a mocked <see cref="IJellyfinClient"/>, served a page at a time as the server does
/// (<see cref="IJellyfinClient.GetLibraryItems"/>: from a start, up to a limit, the size with the first page). In memory each
/// page answers at once, so the whole library is in when the page's OnNavigatedTo returns.
/// </summary>
internal static class LibraryPagesMock
{
	public static void SetupLibrary(this Mock<IJellyfinClient> client, IReadOnlyList<BaseItemDto> items) => client.SetupLibrary(() => items);

	/// <summary><paramref name="items"/> is asked on every page: null fails that page.</summary>
	public static void SetupLibrary(this Mock<IJellyfinClient> client, Func<IReadOnlyList<BaseItemDto>?> items) =>
		client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns<BaseItemDto, int, int, bool, CancellationToken>((_, start, limit, withTotal, _) => Task.FromResult(Page(items(), start, limit, withTotal)));

	public static BaseItemDtoQueryResult? Page(IReadOnlyList<BaseItemDto>? items, int start, int limit, bool withTotal) =>
		items is null ? null : new BaseItemDtoQueryResult { Items = [.. items.Skip(start).Take(limit)], StartIndex = start, TotalRecordCount = withTotal ? items.Count : 0 };

	/// <summary>How many times the library was loaded: its first page asked.</summary>
	public static void VerifyLibraryLoads(this Mock<IJellyfinClient> client, Times times) =>
		client.Verify(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), 0, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), times);

	public static void VerifyLibraryLoads(this Mock<IJellyfinClient> client, Func<Times> times) => client.VerifyLibraryLoads(times());
}
