using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.3 (the repository polish's note, docs\screenshots\home-drive-in.webp): Drive-in Home's Up next board counted
/// Continue Watching in its link, so "All of Up next (3)" stood over one entry, and over none with Next Up empty. The sign's
/// entries, its link's count and what the link opens are one list: Next Up.
/// </summary>
public sealed class DriveInUpNextTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-upnext-test-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _socket = new();
	private readonly HomeCache _cache;

	public DriveInUpNextTests() => _cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), new ManualClock());

	public void Dispose()
	{
		_cache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private HomeViewModel NewHome()
	{
		var client = Mock.Of<IJellyfinClient>();
		return new HomeViewModel(client, _socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), client, Mock.Of<INavigationViewServiceCore>(), Mock.Of<Settings.ISettings>()), _cache);
	}

	private static BaseItemViewModel Item(string name, BaseItemDto_Type type) =>
		BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = name, Type = type });

	// what the board shows: its entries (the first three or four of the sign's list) and the count its link reads
	private static (int Entries, int LinkCount) Board(HomeViewModel home, bool wide) =>
		(home.DriveInUpNext().Take(DriveInHome.SignEntries(wide)).Count(), home.DriveInUpNextCount);

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void NextUpEmpty_ContinueWatchingFull_TheLinkCountsNothing_AndOpensNothing(bool wide)
	{
		var home = NewHome();
		home.ContinueItems = [Item("Half watched", BaseItemDto_Type.Movie), Item("Paused", BaseItemDto_Type.Episode), Item("Resumed", BaseItemDto_Type.Movie)];
		home.NextUpItems = [];

		Assert.Equal((0, 0), Board(home, wide));
		Assert.Empty(home.DriveInUpNext());
	}

	[Fact]
	public void NextUpShort_ContinueWatchingFull_TheLinkCountsTheEntries_NotContinueWatching()
	{
		var home = NewHome();
		var next = Item("Next episode", BaseItemDto_Type.Episode);
		home.ContinueItems = [Item("Half watched", BaseItemDto_Type.Movie), Item("Paused", BaseItemDto_Type.Episode)];
		home.NextUpItems = [next];

		// the screenshot's board: one entry under "All of Up next (3)"
		Assert.Equal((1, 1), Board(home, wide: true));
		Assert.Equal([next], home.DriveInUpNext());
		Assert.Equal("All of Up next (1)", DriveInHome.AllUpNextText(home.DriveInUpNextCount));
	}

	[Fact]
	public void NextUpLong_TheBoardShowsFour_AndTheLinkCountsAndOpensAll()
	{
		var home = NewHome();
		home.ContinueItems = [Item("Half watched", BaseItemDto_Type.Movie)];
		home.NextUpItems = [.. Enumerable.Range(1, 9).Select(i => Item($"Episode {i}", BaseItemDto_Type.Episode))];

		Assert.Equal((4, 9), Board(home, wide: true));
		Assert.Equal((3, 9), Board(home, wide: false));
		Assert.Equal(home.NextUpItems, home.DriveInUpNext());
	}

	[Fact]
	public void TheLinkFollowsNextUp_AsItChanges()
	{
		var home = NewHome();
		home.ContinueItems = [Item("Half watched", BaseItemDto_Type.Movie)];
		var a = Item("A", BaseItemDto_Type.Episode);
		home.NextUpItems.Add(a);
		Assert.Equal(1, home.DriveInUpNextCount);

		home.NextUpItems.Remove(a);
		Assert.Equal(0, home.DriveInUpNextCount);
		Assert.Empty(home.DriveInUpNext());
	}
}
