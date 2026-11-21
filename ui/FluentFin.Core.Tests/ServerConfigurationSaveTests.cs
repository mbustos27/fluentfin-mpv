using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.5 (the button review's S22): the administration pages' Save is on only when something differs from what the server has
/// (or the last Save failed), and says how it went ("Saved", or that the server did not take it).
/// </summary>
public sealed class ServerConfigurationSaveTests
{
	private sealed class Page(IJellyfinClient client) : ServerConfigurationViewModel(client)
	{
		protected override List<JellyfinConfigItemViewModel> CreateItems(ServerConfiguration config) =>
		[
			new JellyfinConfigItemViewModel<string>(() => config.ServerName ?? "", value => config.ServerName = value) { DisplayName = "Server name" },
			new JellyfinGroupedConfigItemViewModel
			{
				Items = [new JellyfinConfigItemViewModel<bool>(() => config.QuickConnectAvailable ?? false, value => config.QuickConnectAvailable = value)],
			},
		];
	}

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly ServerConfiguration _config = new() { ServerName = "Home", QuickConnectAvailable = true };

	private async Task<Page> Open(bool takes = true)
	{
		_client.Setup(x => x.GetConfiguration()).ReturnsAsync(_config);
		_client.Setup(x => x.TrySaveConfiguration(It.IsAny<ServerConfiguration>())).ReturnsAsync(takes);
		var page = new Page(_client.Object);
		await page.OnNavigatedTo(null!);
		return page;
	}

	[Fact]
	public async Task Save_IsOff_UntilSomethingChanges_AndOffAgainWhenItIsPutBack()
	{
		var page = await Open();
		var name = (JellyfinConfigItemViewModel<string>)page.Items[0];

		Assert.False(page.SaveCommand.CanExecute(null));
		name.Value = "Den";
		Assert.True(page.SaveCommand.CanExecute(null));
		name.Value = "Home";
		Assert.False(page.SaveCommand.CanExecute(null));

		// an item inside a group counts too
		var quick = (JellyfinConfigItemViewModel<bool>)((JellyfinGroupedConfigItemViewModel)page.Items[1]).Items[0];
		quick.Value = false;
		Assert.True(page.SaveCommand.CanExecute(null));

		// Undo changes puts the server's values back: nothing to save
		page.ResetCommand.Execute(null);
		Assert.False(page.SaveCommand.CanExecute(null));
	}

	[Fact]
	public async Task ASave_SaysSaved_AndSaveIsOffAfterIt()
	{
		var page = await Open();
		((JellyfinConfigItemViewModel<string>)page.Items[0]).Value = "Den";

		await page.SaveCommand.ExecuteAsync(null);

		Assert.Equal("Saved", page.SaveStatus);
		Assert.Equal("Den", _config.ServerName);
		Assert.False(page.SaveCommand.CanExecute(null));
		_client.Verify(x => x.TrySaveConfiguration(_config), Times.Once);

		// a new change clears "Saved"
		((JellyfinConfigItemViewModel<string>)page.Items[0]).Value = "Attic";
		Assert.Equal("", page.SaveStatus);
	}

	[Fact]
	public async Task AFailedSave_SaysSo_AndSaveStaysOn()
	{
		var page = await Open(takes: false);
		((JellyfinConfigItemViewModel<string>)page.Items[0]).Value = "Den";

		await page.SaveCommand.ExecuteAsync(null);

		Assert.StartsWith("Couldn't save", page.SaveStatus, StringComparison.Ordinal);
		Assert.True(page.SaveCommand.CanExecute(null));
	}
}
