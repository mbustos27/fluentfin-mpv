using System.Reactive.Subjects;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Journal;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Plugins.Journal;
using FluentFin.Plugins.Journal.Views;
using FluentFin.UI.Core.Contracts.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// fluentfin-mpv: the journal plugin's wiring, with fakes for everything outside it and journal.db in a temp folder (never
/// the app's data folder): what it registers, the Diary entry in the main pane only, and that signing in starts the
/// auto-logger and a sync.
/// </summary>
public sealed class JournalPluginTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinJournalPluginTests", Guid.NewGuid().ToString("N"));
	private readonly Subject<BridgeEvent> _events = new();
	private readonly Mock<IJournalServer> _server = new();
	private ServiceProvider? _provider;

	public void Dispose()
	{
		_provider?.Dispose();
		SqliteConnection.ClearAllPools();
		try
		{
			Directory.Delete(_root, true);
		}
		catch (IOException)
		{
		}
	}

	private ServiceProvider Build()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton(new KnownFolders(_root));
		var client = new Mock<IJellyfinClient>();
		client.As<IJournalServer>();
		services.AddSingleton(sp => client.Object);
		var backend = new Mock<IMpvBackend>();
		backend.SetupGet(x => x.Events).Returns(_events);
		services.AddSingleton(backend.Object);
		services.AddSingleton(Mock.Of<INavigationServiceCore>());
		services.AddSingleton(Mock.Of<ISettings>());
		new JournalPlugin().ConfigureServices(services);
		// the tests' own Jellyfin, in place of FluentFin's client
		services.AddSingleton(_server.Object);
		_provider = services.BuildServiceProvider();
		return _provider;
	}

	[Fact]
	public void TheJournalsServices_AreRegistered_WithJournalDbInTheAppsDataFolder()
	{
		var provider = Build();

		var store = provider.GetRequiredService<JournalStore>();
		Assert.Equal(Path.Combine(_root, "journal.db"), store.Path);
		Assert.Same(provider.GetRequiredService<JournalService>(), provider.GetRequiredService<JournalService>());
		Assert.IsType<SessionInfoJournalSession>(provider.GetRequiredService<IJournalSession>());
		Assert.NotNull(provider.GetRequiredService<JournalSync>());
		Assert.NotNull(provider.GetRequiredService<JournalAutoLogger>());
		Assert.NotSame(provider.GetRequiredService<DiaryViewModel>(), provider.GetRequiredService<DiaryViewModel>());
		Assert.NotNull(provider.GetRequiredService<JournalRatingViewModel>());
		// nothing is opened or written before it is used
		Assert.False(File.Exists(store.Path));
	}

	[Fact]
	public void TheJournalServer_IsFluentFinsOwnClient()
	{
		var services = new ServiceCollection();
		var client = new Mock<IJellyfinClient>();
		var asServer = client.As<IJournalServer>();
		services.AddSingleton(client.Object);
		new JournalPlugin().ConfigureServices(services);

		using var provider = services.BuildServiceProvider();

		Assert.Same(client.Object, provider.GetRequiredService<IJournalServer>());
	}

	[Fact]
	public void TheDiaryPage_IsRegistered()
	{
		var pages = new RecordingPages();

		new JournalPlugin().ConfigurePages(pages);

		Assert.Equal([(typeof(DiaryViewModel), typeof(DiaryPage))], pages.Pages);
	}

	[Fact]
	public void TheDiary_IsInTheMainPaneOnly_AndAJournalThatCannotStart_StopsNothing()
	{
		var main = new RecordingPane(null);
		var settings = new RecordingPane("Settings");

		// no journal registered with the app's locator here: the pane is still built
		new JournalPlugin().AddNavigationViewItem(main);
		new JournalPlugin().AddNavigationViewItem(settings);

		var item = Assert.Single(main.Items);
		Assert.Equal(("Diary", typeof(DiaryViewModel).FullName, "", false), (item.Name, item.Key, item.Glyph, item.Persistent));
		Assert.Empty(settings.Items);
	}

	[Fact]
	public async Task SigningIn_StartsTheAutoLogger_AndASync()
	{
		var provider = Build();
		var syncAsked = new TaskCompletionSource();
		_server.Setup(x => x.GetPlaybackHistoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.Returns(() =>
			{
				syncAsked.TrySetResult();
				return Task.FromResult(PlaybackHistory.Unavailable("HTTP 403"));
			});
		var session = new Mock<IJournalSession>();
		var alice = new JournalProfile("server-a", Guid.NewGuid(), "Home", "alice");
		session.SetupGet(x => x.Current).Returns(alice);
		var journal = new JournalService(provider.GetRequiredService<JournalStore>(), _server.Object, session.Object);
		var sync = new JournalSync(journal, Microsoft.Extensions.Logging.Abstractions.NullLogger<JournalSync>.Instance);
		var autoLogger = new JournalAutoLogger(_events, journal, Microsoft.Extensions.Logging.Abstractions.NullLogger<JournalAutoLogger>.Instance);
		var opened = new TaskCompletionSource();
		_server.Setup(x => x.GetUserDataAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(() =>
		{
			opened.TrySetResult();
			return Task.FromResult<JournalUserData?>(null);
		});

		JournalPlugin.OnSignedIn(autoLogger, sync);
		JournalPlugin.OnSignedIn(autoLogger, sync);

		await syncAsked.Task.WaitAsync(TimeSpan.FromSeconds(10));
		// listening: a play session that opens is followed (its "before" user data is read)
		_events.OnNext(new BridgeEvent(Bridge.Events.State, System.Text.Json.Nodes.JsonNode.Parse($$"""{"state":"playing","itemId":"{{Guid.NewGuid():N}}","playSessionId":"ps-1"}""")));
		await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
		autoLogger.Dispose();
	}

	private sealed class RecordingPages : IPageRegistration
	{
		public List<(Type ViewModel, Type Page)> Pages { get; } = [];

		public void Configure<VM, V>() where VM : ObservableObject where V : Page => Pages.Add((typeof(VM), typeof(V)));
	}

	private sealed class RecordingPane(object? key) : INavigationViewServiceCore
	{
		public object? Key { get; } = key;

		public List<CustomNavigationViewItem> Items { get; } = [];

		public void AddNavigationItem(CustomNavigationViewItem item) => Items.Add(item);

		public void RemoveNavigationItem(CustomNavigationViewItem item) => Items.Remove(item);

		public void SaveCustomViews()
		{
		}
	}
}
