using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Plugins.Jellyseer;
using FluentFin.Plugins.Jellyseer.ViewModels;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// Seaglass 1.1.3 (UX review, prioritised fix 1): Jellyseerr is not on the rail any more; Settings > Integrations has its entry,
/// which says whether it is set up for the signed-in server and opens the Jellyseerr page either way (it asks for the address).
/// </summary>
public sealed class JellyseerrEntryTests
{
	private const string Server = "6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f";

	[Fact]
	public void ThePlugin_PutsNothingOnTheRail()
	{
		var rail = new Mock<INavigationViewServiceCore>();

		new JellyseerPlugin().AddNavigationViewItem(rail.Object);

		rail.Verify(x => x.AddNavigationItem(It.IsAny<CustomNavigationViewItem>()), Times.Never);
	}

	[Fact]
	public void SetUp_ItOpensTheRequests_NamingTheAddress()
	{
		var options = new JellyseerOptions { ServerMapping = { [Server] = "https://requests.example" } };

		Assert.Equal("https://requests.example", JellyseerrEntry.Address(options, Server));
		Assert.Contains("https://requests.example", JellyseerrEntry.Description(options, Server));
		Assert.Equal("Open", JellyseerrEntry.ActionText(options, Server));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a")]
	public void NotSetUpForThisServer_ItOffersToSetItUp(string? serverId)
	{
		var options = new JellyseerOptions { ServerMapping = { [Server] = "https://requests.example" } };

		Assert.Null(JellyseerrEntry.Address(options, serverId));
		Assert.Contains("Not set up", JellyseerrEntry.Description(options, serverId));
		Assert.Equal("Set up", JellyseerrEntry.ActionText(options, serverId));
	}

	[Fact]
	public void WithoutOptions_ItIsNotSetUp()
	{
		Assert.Null(JellyseerrEntry.Address(null, Server));
		Assert.Equal("Set up", JellyseerrEntry.ActionText(new JellyseerOptions { ServerMapping = { [Server] = " " } }, Server));
	}

	[Fact]
	public void ItIsSpeltJellyseerr_AndOpensItsPage()
	{
		Assert.Equal("Jellyseerr", JellyseerrEntry.Name);
		Assert.Equal(typeof(JellyseerDashboardViewModel).FullName, JellyseerrEntry.PageKey);
	}
}

/// <summary>
/// Seaglass 1.1.3 (review): a Set up whose login fails or throws leaves no address behind, so Settings > Integrations does not
/// say "Open" and name an address that was never set up; one that works keeps it and saves it.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class JellyseerrSetUpTests : IDisposable
{
	private const string Server = "6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f";
	private readonly JellyseerOptions? _options = JellyseerOptions.Current;
	private readonly string _serverId = FluentFin.Core.SessionInfo.ServerId;
	private int _saves;

	public JellyseerrSetUpTests()
	{
		FluentFin.Core.SessionInfo.ServerId = Server;
		JellyseerOptions.Current = new JellyseerOptions { Save = () => _saves++ };
	}

	public void Dispose()
	{
		JellyseerOptions.Current = _options!;
		FluentFin.Core.SessionInfo.ServerId = _serverId;
	}

	private static JellyseerDashboardViewModel Dashboard(Func<Task<bool>> login)
	{
		var client = new Mock<IJellyseerClient>();
		client.Setup(x => x.Login()).Returns(login);
		return new JellyseerDashboardViewModel(client.Object);
	}

	[Fact]
	public async Task AFailedLogin_LeavesItNotSetUp()
	{
		var vm = Dashboard(() => Task.FromResult(false));

		await vm.SetServerUrlCommand.ExecuteAsync("https://typo.example");

		Assert.Null(JellyseerrEntry.Address(JellyseerOptions.Current, Server));
		Assert.Equal("Set up", JellyseerrEntry.ActionText(JellyseerOptions.Current, Server));
		Assert.False(vm.HasServer);
		Assert.Equal(0, _saves);
	}

	[Fact]
	public async Task AThrowingLogin_PutsBackTheAddressThatWorked()
	{
		JellyseerOptions.Current.ServerMapping[Server] = "https://requests.example";
		var vm = Dashboard(() => throw new HttpRequestException("no route"));

		await Assert.ThrowsAsync<HttpRequestException>(() => vm.SetServerUrlCommand.ExecuteAsync("https://typo.example"));

		Assert.Equal("https://requests.example", JellyseerrEntry.Address(JellyseerOptions.Current, Server));
	}

	[Fact]
	public async Task ALoginThatWorks_KeepsAndSavesTheAddress()
	{
		var vm = Dashboard(() => Task.FromResult(true));

		await vm.SetServerUrlCommand.ExecuteAsync("https://requests.example");

		Assert.Equal("https://requests.example", JellyseerrEntry.Address(JellyseerOptions.Current, Server));
		Assert.Equal("Open", JellyseerrEntry.ActionText(JellyseerOptions.Current, Server));
		Assert.True(vm.HasServer);
		Assert.Equal(1, _saves);
	}
}
