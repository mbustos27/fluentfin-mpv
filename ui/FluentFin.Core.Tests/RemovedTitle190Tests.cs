using System.Net;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the index's load on a server that isn't the user's): the library index reads every title at most once a day, so
/// a row of Collections can still hold a title the server removed since. Opening it said "Check this PC's connection" (the
/// client answers null for the server's 404); it says the server doesn't have it any more, with no step on the server (rule
/// 11). A connection that failed, or the server's own error, still says theirs.
/// </summary>
[Collection(ItemUserDataCollection.Name)]
public sealed class RemovedTitle190Tests
{
	private static BaseItemDto Film(Guid id) => new() { Id = id, Type = BaseItemDto_Type.Movie, Name = "Wild Canyons" };

	private static BaseItemDto Show(Guid id) => new() { Id = id, Type = BaseItemDto_Type.Series, Name = "Kestrel Bay" };

	// the client's GetItem as it is: null for any failure, the answer noted in the page load's trace (AnswerWatch)
	private static Mock<IJellyfinClient> Client(Guid id, HttpStatusCode? status, bool noAnswer = false)
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(id)).ReturnsAsync(() =>
		{
			if (status is { } answered)
			{
				AnswerTrace.Current?.Answered(answered, "application/json");
			}

			if (noAnswer)
			{
				AnswerTrace.Current?.NoAnswer();
			}

			return null;
		});
		client.Setup(x => x.GetItemCollections(id)).ReturnsAsync([]);
		return client;
	}

	private static GlobalCommands Commands(Mock<IJellyfinClient> client) =>
		new(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());

	[Fact]
	public async Task AFilmTheServerNoLongerHas_SaysSo_NotTheConnection()
	{
		var id = Guid.NewGuid();
		var client = Client(id, HttpStatusCode.NotFound);
		var vm = new MovieViewModel(client.Object, Commands(client));

		await vm.OnNavigatedTo(Film(id));

		Assert.True(vm.State.IsFailed);
		Assert.Equal("Couldn't load this movie", vm.State.Title);
		Assert.EndsWith(" doesn't have it any more. Nixie's collections drop it when they're next read in full, within a day; go back to pick another.", vm.State.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("connection", vm.State.Message, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("your server", vm.State.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AShowTheServerNoLongerHas_SaysSo()
	{
		var id = Guid.NewGuid();
		var client = Client(id, HttpStatusCode.NotFound);
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		var vm = new SeriesViewModel(client.Object);

		await vm.OnNavigatedTo(Show(id));

		Assert.True(vm.State.IsFailed);
		Assert.Equal("Couldn't load this series", vm.State.Title);
		Assert.Contains(" doesn't have it any more.", vm.State.Message, StringComparison.Ordinal);
	}

	// the words stay the connection's or the server's when that is what failed: a 404 beside a request with no answer, or a 500
	[Theory]
	[InlineData(null, false, "connection")]
	[InlineData(HttpStatusCode.NotFound, true, "connection")]
	[InlineData(HttpStatusCode.InternalServerError, false, "had a problem")]
	public async Task OtherFailures_KeepTheirWords(HttpStatusCode? status, bool noAnswer, string words)
	{
		var id = Guid.NewGuid();
		var client = Client(id, status, noAnswer);
		var vm = new MovieViewModel(client.Object, Commands(client));

		await vm.OnNavigatedTo(Film(id));

		Assert.True(vm.State.IsFailed);
		Assert.Contains(words, vm.State.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("any more", vm.State.Message, StringComparison.Ordinal);
	}
}
