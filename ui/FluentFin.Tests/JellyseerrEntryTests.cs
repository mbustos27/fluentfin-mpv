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
