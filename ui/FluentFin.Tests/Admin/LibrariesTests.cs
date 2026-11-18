using FluentFin.Core;
using FluentFin.Core.ViewModels;
using FluentFin.Dialogs.ViewModels;
using CollectionType = Jellyfin.Sdk.Generated.Libraries.AvailableOptions.CollectionType;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, button review D17 / D18 / D69 / D83, worst offenders 3 and 7): "Add library" could never create one
/// (Save returned at once in create mode) and "Scall All Libraries" had no action. Now Add library sends one
/// POST /Library/VirtualFolders with the name, the type, the folders and the options shown, and the list shows the new one;
/// Scan all libraries sends one POST /Library/Refresh. Delete library asks first; a "no" sends nothing.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class LibrariesTests : IAsyncLifetime
{
	private const string FoldersJson = """
		[{"Name":"Movies","Locations":["/media/movies"],"CollectionType":"movies","ItemId":"aaaaaaaabbbbccccddddeeeeeeeeeeee","LibraryOptions":{}},
		 {"Name":"Kids","Locations":["/media/kids","/media/cartoons"],"CollectionType":"tvshows","ItemId":"bbbbbbbbbbbbccccddddeeeeeeeeeeee","LibraryOptions":{}}]
		""";

	private const string AvailableJson = """
		{"MetadataSavers":[],"MetadataReaders":[],"SubtitleFetchers":[{"Name":"Open Subtitles","DefaultEnabled":true}],
		 "TypeOptions":[{"Type":"Movie","MetadataFetchers":[{"Name":"TheMovieDb","DefaultEnabled":true},{"Name":"The Open Movie Database","DefaultEnabled":false}],
		   "ImageFetchers":[{"Name":"TheMovieDb","DefaultEnabled":true},{"Name":"Fanart","DefaultEnabled":false}],"SupportedImageTypes":["Primary"],"DefaultImageOptions":[]}]}
		""";

	private readonly AdminFakeServer _server = new();
	private readonly List<ConfirmPrompt> _asked = [];
	private Core.Services.JellyfinClient _client = null!;
	private LibrariesSettingsViewModel _page = null!;

	static LibrariesTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public async Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("GET", "/Library/VirtualFolders", 200, FoldersJson);
		_server.Respond("POST", "/Library/VirtualFolders", 204);
		_server.Respond("DELETE", "/Library/VirtualFolders", 204);
		_server.Respond("POST", "/Library/Refresh", 204);
		_server.Respond("GET", "/Localization/Cultures", 200, "[]");
		_server.Respond("GET", "/Localization/Countries", 200, "[]");
		_server.Respond("GET", "/Libraries/AvailableOptions", 200, AvailableJson);
		_client = await _server.SignIn();
		_page = new LibrariesSettingsViewModel(_client);
		await _page.OnNavigatedTo(new());
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	private void Answer(bool yes) => _page.Confirm = prompt =>
	{
		_asked.Add(prompt);
		return Task.FromResult(yes);
	};

	private async Task<ManageLibraryViewModel> NewLibraryDialog(CollectionType type)
	{
		var dialog = new ManageLibraryViewModel(_client);
		await dialog.Initialize();
		dialog.FolderCollectionType = type;
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (dialog.MovieMetadataFetchers.Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(20);
		}

		Assert.NotEmpty(dialog.MovieMetadataFetchers);
		return dialog;
	}

	[Fact]
	public async Task ScanAll_SendsOneRefresh_AndSaysItStarted()
	{
		await _page.ScanAllCommand.ExecuteAsync(null);

		var scan = Assert.Single(_server.Changes);
		Assert.Equal("POST /Library/Refresh", $"{scan.Method} {scan.Path}");
		Assert.Equal("Scan started", _page.Notice!.Title);
	}

	[Fact]
	public async Task AddLibrary_CreatesIt_WithTheNameTypeFoldersAndOptionsShown()
	{
		var dialog = await NewLibraryDialog(CollectionType.Movies);
		Assert.Equal("Add library", dialog.DialogTitle);
		Assert.Equal("Create library", dialog.SaveText);
		dialog.Name = " Documentaries ";
		dialog.Locations.Add("/media/docs");
		dialog.MovieMetadataFetchers[1].IsSelected = true;
		dialog.MovieMetadataFetchers[1].MoveUpCommand.Execute(null);

		Assert.True(await dialog.Save());

		var create = Assert.Single(_server.Changes);
		Assert.Equal("POST /Library/VirtualFolders", $"{create.Method} {create.Path}");
		Assert.Equal("Documentaries", create.Query["name"]);
		Assert.Equal("movies", create.Query["collectionType"]);
		Assert.Equal("true", create.Query["refreshLibrary"], ignoreCase: true);
		var options = create.Json.GetProperty("LibraryOptions");
		Assert.True(options.GetProperty("Enabled").GetBoolean());
		Assert.True(options.GetProperty("EnableRealtimeMonitor").GetBoolean());
		Assert.Equal(["/media/docs"], options.GetProperty("PathInfos").EnumerateArray().Select(x => x.GetProperty("Path").GetString()));
		var movie = Assert.Single(options.GetProperty("TypeOptions").EnumerateArray());
		Assert.Equal("Movie", movie.GetProperty("Type").GetString());
		Assert.Equal(["The Open Movie Database", "TheMovieDb"], movie.GetProperty("MetadataFetcherOrder").EnumerateArray().Select(x => x.GetString()));
		Assert.Equal(["The Open Movie Database", "TheMovieDb"], movie.GetProperty("MetadataFetchers").EnumerateArray().Select(x => x.GetString()));
		Assert.Equal(["TheMovieDb"], movie.GetProperty("ImageFetchers").EnumerateArray().Select(x => x.GetString()));
		Assert.Equal(["Open Subtitles"], options.GetProperty("SubtitleFetcherOrder").EnumerateArray().Select(x => x.GetString()));
	}

	[Fact]
	public async Task AddLibrary_WithoutAName_SaysSo_AndSendsNothing()
	{
		var dialog = await NewLibraryDialog(CollectionType.Movies);

		Assert.False(await dialog.Save());

		Assert.Equal("Give the library a name.", dialog.Error);
		Assert.Empty(_server.Changes);
	}

	[Fact]
	public async Task AddLibrary_WithoutAType_SaysSo_AndSendsNothing()
	{
		var dialog = new ManageLibraryViewModel(_client);
		await dialog.Initialize();
		dialog.Name = "Documentaries";

		Assert.False(await dialog.Save());

		Assert.Equal("Choose what the library holds.", dialog.Error);
		Assert.Empty(_server.Changes);
	}

	[Fact]
	public async Task AddLibrary_TheServerRefuses_KeepsTheDialog_AndSaysSo()
	{
		_server.Respond("POST", "/Library/VirtualFolders", 400);
		var dialog = await NewLibraryDialog(CollectionType.Movies);
		dialog.Name = "Movies";

		Assert.False(await dialog.Save());

		Assert.Single(_server.Changes);
		Assert.StartsWith("The server didn't create the library", dialog.Error);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task AddLibrary_TheListIsReadAgain_OnlyWhenOneWasCreated(bool created)
	{
		var reads = _server.Requests.Count(x => x.Path == "/Library/VirtualFolders" && x.Method == "GET");
		_page.AddLibraryDialog = () => Task.FromResult(created);

		await _page.AddLibraryCommand.ExecuteAsync(null);

		Assert.Equal(reads + (created ? 1 : 0), _server.Requests.Count(x => x.Path == "/Library/VirtualFolders" && x.Method == "GET"));
	}

	[Fact]
	public async Task DeleteLibrary_ANo_SendsNothing_AndKeepsTheCard()
	{
		Answer(yes: false);
		var kids = _page.VirtualFolders[1];

		await _page.DeleteLibraryCommand.ExecuteAsync(kids);

		var prompt = Assert.Single(_asked);
		Assert.Equal("Delete Kids?", prompt.Title);
		Assert.Equal("Delete library", prompt.ConfirmText);
		Assert.Empty(_server.Changes);
		Assert.Contains(kids, _page.VirtualFolders);
	}

	[Fact]
	public async Task DeleteLibrary_AYes_SendsOneDelete_ForThatLibrary_AndTheCardGoes()
	{
		Answer(yes: true);
		var kids = _page.VirtualFolders[1];

		await _page.DeleteLibraryCommand.ExecuteAsync(kids);

		var delete = Assert.Single(_server.Changes);
		Assert.Equal("DELETE /Library/VirtualFolders", $"{delete.Method} {delete.Path}");
		Assert.Equal("Kids", delete.Query["name"]);
		Assert.DoesNotContain(kids, _page.VirtualFolders);
		Assert.Single(_page.VirtualFolders);
	}

	[Fact]
	public async Task DeleteLibrary_TheServerRefuses_TheCardStays_AndItSaysSo()
	{
		Answer(yes: true);
		_server.Respond("DELETE", "/Library/VirtualFolders", 500);
		var kids = _page.VirtualFolders[1];

		await _page.DeleteLibraryCommand.ExecuteAsync(kids);

		Assert.Single(_server.Changes);
		Assert.Contains(kids, _page.VirtualFolders);
		Assert.Equal("Kids wasn't deleted", _page.Notice!.Title);
	}

	[Fact]
	public void MovingAFetcher_UpdatesItsNeighbours_Too()
	{
		var list = new System.Collections.ObjectModel.ObservableCollection<MetadataFetcher>();
		foreach (var name in new[] { "A", "B", "C" })
		{
			list.Add(new MetadataFetcher(name, list));
		}

		MetadataFetcher.UpdateMoves(list);
		list[0].MoveDownCommand.Execute(null);

		Assert.Equal(["B", "A", "C"], list.Select(x => x.Name));
		Assert.Equal([false, true, true], list.Select(x => x.CanMoveUp));
		Assert.Equal([true, true, false], list.Select(x => x.CanMoveDown));
		Assert.False(list[0].MoveUpCommand.CanExecute(null));
		Assert.True(list[1].MoveUpCommand.CanExecute(null));
	}
}
