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

	/// <summary>
	/// 1.5.1 fixes: Libraries > Display's "Date added behavior for new content" is the server's metadata configuration
	/// (/System/Configuration/metadata): Save sends it as chosen, and a server that does not take it is "Couldn't save".
	/// </summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Display_SavesTheDateAddedChoice(bool takes)
	{
		var metadata = new Metadata { UseFileCreationTimeForDateAdded = true };
		_client.Setup(x => x.GetConfiguration()).ReturnsAsync(_config);
		_client.Setup(x => x.TrySaveConfiguration(It.IsAny<ServerConfiguration>())).ReturnsAsync(true);
		_client.Setup(x => x.GetMetadata()).ReturnsAsync(metadata);
		var saved = new List<bool>();
		_client.Setup(x => x.SaveMetadata(It.IsAny<Metadata>())).Returns((Metadata m) =>
		{
			saved.Add(m.UseFileCreationTimeForDateAdded);
			return takes ? Task.CompletedTask : Task.FromException(new HttpRequestException("400"));
		});
		var page = new LibrariesDisplayViewModel(_client.Object);
		await ((FluentFin.Contracts.ViewModels.INavigationAware)page).OnNavigatedTo(null!);
		var dateAdded = Assert.IsType<JellyfinSelectableConfigItemViewModel>(page.Items[0]);

		dateAdded.SelectedValue = page.DateAddedBehaviors[1]; // "Use date scanned into the library"
		Assert.True(page.SaveCommand.CanExecute(null));
		await page.SaveCommand.ExecuteAsync(null);

		Assert.Equal([false], saved);
		Assert.Equal(takes ? "Saved" : ServerConfigurationViewModel.NotSavedText, page.SaveStatus);
	}

	private static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();

	/// <summary>
	/// 1.6.1 (BH-151-m7): Display's Save posts the metadata configuration only when the date-added choice changed, and then the
	/// configuration as the server has it now (read again, every key kept), with only that choice changed. It posted a one-key
	/// copy over the whole configuration on every Save of the page.
	/// </summary>
	[Fact]
	public async Task Display_PostsTheMetadataConfiguration_OnlyWhenTheChoiceChanged_AsTheServerHasItNow()
	{
		var read = new Queue<Metadata>([
			new Metadata { UseFileCreationTimeForDateAdded = true, Other = new() { ["KeyFrom2027"] = Json("""{"A":1}""") } },
			// read again at the Save: someone added a key meanwhile
			new Metadata { UseFileCreationTimeForDateAdded = true, Other = new() { ["KeyFrom2027"] = Json("""{"A":2}"""), ["Another"] = Json("\"x\"") } },
		]);
		_client.Setup(x => x.GetConfiguration()).ReturnsAsync(_config);
		_client.Setup(x => x.TrySaveConfiguration(It.IsAny<ServerConfiguration>())).ReturnsAsync(true);
		_client.Setup(x => x.GetMetadata()).ReturnsAsync(() => read.Dequeue());
		var posted = new List<Metadata>();
		_client.Setup(x => x.SaveMetadata(It.IsAny<Metadata>())).Returns((Metadata m) => { posted.Add(m); return Task.CompletedTask; });
		var page = new LibrariesDisplayViewModel(_client.Object);
		await ((FluentFin.Contracts.ViewModels.INavigationAware)page).OnNavigatedTo(null!);

		// another setting on the page: the metadata configuration isn't touched
		var folderView = page.Items.OfType<JellyfinConfigItemViewModel<bool>>().First();
		folderView.Value = !folderView.Value;
		await page.SaveCommand.ExecuteAsync(null);
		Assert.Empty(posted);
		Assert.Equal("Saved", page.SaveStatus);

		// the date-added choice: posted into what the server has now
		var dateAdded = Assert.IsType<JellyfinSelectableConfigItemViewModel>(page.Items[0]);
		dateAdded.SelectedValue = page.DateAddedBehaviors[1];
		await page.SaveCommand.ExecuteAsync(null);

		var sent = Assert.Single(posted);
		Assert.False(sent.UseFileCreationTimeForDateAdded);
		Assert.Equal(2, sent.Other!.Count);
		Assert.Equal(2, sent.Other["KeyFrom2027"].GetProperty("A").GetInt32());
		Assert.Equal("x", sent.Other["Another"].GetString());
		Assert.Empty(read);

		// saved: a Save of another change doesn't post it again
		folderView.Value = !folderView.Value;
		await page.SaveCommand.ExecuteAsync(null);
		Assert.Single(posted);
	}

	/// <summary>
	/// 1.6.1 (BH-151-m7): the real client keeps the keys it doesn't know: what <c>GET /System/Configuration/metadata</c> gave is
	/// what <c>POST</c> sends back, with only the date-added choice changed.
	/// </summary>
	[Fact]
	public async Task TheClient_SendsBackEveryKeyOfTheMetadataConfiguration()
	{
		using var server = new FakeJellyfin();
		server.Start();
		server.Respond("/System/Configuration/metadata", 200, """{"UseFileCreationTimeForDateAdded":true,"KeyFrom2027":{"A":[1,2]},"Another":"x"}""");
		var client = new FluentFin.Core.Services.JellyfinClient(new CoreListLogger<FluentFin.Core.Services.JellyfinClient>(), Mock.Of<IObserver<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>>(),
			Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<FluentFin.Core.Settings.ISettings>(), TestHomeCache.Unused());
		await client.Initialize(server.BaseUrl, new AuthenticationResult { AccessToken = "token-161-metadata", ServerId = "fake-server", User = new UserDto { Id = Guid.NewGuid(), Name = "admin" } });
		try
		{
			var metadata = await client.GetMetadata();
			Assert.True(metadata.UseFileCreationTimeForDateAdded);
			metadata.UseFileCreationTimeForDateAdded = false;
			server.Respond("/System/Configuration/metadata", 204, "");

			await client.SaveMetadata(metadata);

			var post = Assert.Single(server.ApiRequests, r => r.Method == "POST" && r.Path.Equals("/System/Configuration/metadata", StringComparison.OrdinalIgnoreCase));
			using var body = System.Text.Json.JsonDocument.Parse(post.Body);
			var keys = body.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
			Assert.Equal(3, keys.Count);
			Assert.False(keys["UseFileCreationTimeForDateAdded"].GetBoolean());
			Assert.Equal("[1,2]", keys["KeyFrom2027"].GetProperty("A").GetRawText());
			Assert.Equal("x", keys["Another"].GetString());
		}
		finally
		{
			client.StopWebSocket();
		}
	}
}
