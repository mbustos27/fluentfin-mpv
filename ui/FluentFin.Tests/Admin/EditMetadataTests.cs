using System.Text.Json;
using FluentFin.Dialogs.ViewModels;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, button review D56, the worst offender): Edit metadata's "Reset" emptied every field and genre, so a
/// Save afterwards wiped the item. Now "Revert" puts back the values as loaded, and Save sends the item as the server gave it
/// with only the changed fields replaced (POST /Items/{id} replaces every field it reads), or nothing when nothing changed.
/// Against a fake server, through the app's real client.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class EditMetadataTests : IAsyncLifetime
{
	private static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
	private static readonly string ItemPath = $"/Items/{ItemId}";

	private const string ItemJson = """
		{"Id":"11111111222233334444555555555555","Name":"Night Train","OriginalTitle":"Nachtzug","SortName":"night train",
		 "ForcedSortName":"Train, Night","Type":"Movie","Overview":"A long night on a train.","Genres":["Drama","Mystery"],
		 "Tags":["festival"],"Studios":[{"Name":"North Line","Id":"99999999222233334444555555555555"}],
		 "ProviderIds":{"Imdb":"tt0000001","Tmdb":"101"},"PremiereDate":"2004-05-14T00:00:00.0000000Z","ProductionYear":2004,
		 "CommunityRating":7.3,"OfficialRating":"PG-13","CustomRating":"Family","AspectRatio":"2.35:1",
		 "DateCreated":"2020-01-02T03:04:05.0000000Z","Path":"/media/movies/Night Train (2004).mkv","LockedFields":["Name"],"IsLocked":false}
		""";

	private const string EditorJson = """
		{"ParentalRatingOptions":[{"Name":"G","Value":0},{"Name":"PG-13","Value":13},{"Name":"R","Value":17}],
		 "ExternalIdInfos":[{"Name":"IMDb","Key":"Imdb","Type":"Movie"},{"Name":"TheMovieDb","Key":"Tmdb","Type":"Movie"},{"Name":"TheTVDB","Key":"Tvdb","Type":"Movie"}]}
		""";

	private readonly AdminFakeServer _server = new();
	private EditMetadataViewModel _vm = null!;

	public async Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("GET", ItemPath, 200, ItemJson);
		_server.Respond("GET", $"{ItemPath}/MetadataEditor", 200, EditorJson);
		_server.Respond("POST", ItemPath, 204);
		var client = await _server.SignIn();
		_vm = new EditMetadataViewModel(client);
		await _vm.Initialize(new Jellyfin.Sdk.Generated.Models.BaseItemDto { Id = ItemId });
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	private void EditEverything()
	{
		_vm.Title = "Changed";
		_vm.OriginalTitle = "";
		_vm.Overview = null;
		_vm.Year = 1999;
		_vm.CommunityRating = 1;
		_vm.ParentalRating = "R";
		_vm.CustomRating = null;
		_vm.ReleaseDate = new DateTimeOffset(1999, 1, 1, 12, 0, 0, TimeSpan.Zero);
		_vm.Genres.Clear();
		_vm.ExternalIds.Single(x => x.Key == "Imdb").Value = "";
	}

	[Fact]
	public void Loaded_FillsTheForm_AndNothingHasChanged()
	{
		Assert.Equal("Night Train", _vm.Title);
		Assert.Equal("Train, Night", _vm.SortTitle);
		Assert.Equal(["Drama", "Mystery"], _vm.Genres);
		Assert.Equal("tt0000001", _vm.ExternalIds.Single(x => x.Key == "Imdb").Value);
		Assert.False(_vm.HasChanges);
		Assert.False(_vm.RevertCommand.CanExecute(null));
		// the item's own ratings can be shown (a ComboBox without its value writes null back)
		Assert.Contains("PG-13", _vm.RatingValues);
		Assert.Contains("Family", _vm.RatingValues);
	}

	[Fact]
	public void Revert_PutsBackTheLoadedValues_NotBlanks()
	{
		EditEverything();
		Assert.True(_vm.HasChanges);

		_vm.RevertCommand.Execute(null);

		Assert.Equal("Night Train", _vm.Title);
		Assert.Equal("Nachtzug", _vm.OriginalTitle);
		Assert.Equal("A long night on a train.", _vm.Overview);
		Assert.Equal(2004, _vm.Year);
		Assert.Equal(7.3, _vm.CommunityRating!.Value, 3);
		Assert.Equal("PG-13", _vm.ParentalRating);
		Assert.Equal("Family", _vm.CustomRating);
		Assert.Equal(2004, _vm.ReleaseDate!.Value.Year);
		Assert.Equal(["Drama", "Mystery"], _vm.Genres);
		Assert.Equal("tt0000001", _vm.ExternalIds.Single(x => x.Key == "Imdb").Value);
		Assert.False(_vm.HasChanges);
		Assert.Empty(_server.Changes);
	}

	[Fact]
	public async Task SaveAfterRevert_SendsNothing_SoNothingIsBlanked()
	{
		EditEverything();
		_vm.RevertCommand.Execute(null);

		Assert.True(await _vm.Save());

		Assert.Empty(_server.Changes);
	}

	[Fact]
	public async Task SaveAfterRevertAndOneEdit_SendsOneRequest_WithEveryOtherFieldAsTheServerHadIt()
	{
		EditEverything();
		_vm.RevertCommand.Execute(null);
		_vm.Overview = "A shorter night.";

		Assert.True(await _vm.Save());

		var request = Assert.Single(_server.Changes);
		Assert.Equal("POST", request.Method);
		Assert.Equal(ItemPath, request.Path, ignoreCase: true);
		var sent = request.Json;
		var before = JsonDocument.Parse(ItemJson).RootElement;
		Assert.Equal("A shorter night.", sent.GetProperty("Overview").GetString());
		// everything else is the server's own value, untouched
		foreach (var field in new[] { "Name", "OriginalTitle", "ForcedSortName", "OfficialRating", "CustomRating", "AspectRatio", "Path" })
		{
			Assert.Equal(before.GetProperty(field).GetString(), sent.GetProperty(field).GetString());
		}

		Assert.Equal(2004, sent.GetProperty("ProductionYear").GetInt32());
		Assert.Equal(7.3, sent.GetProperty("CommunityRating").GetDouble(), 3);
		Assert.Equal(["Drama", "Mystery"], sent.GetProperty("Genres").EnumerateArray().Select(x => x.GetString()));
		Assert.Equal(["festival"], sent.GetProperty("Tags").EnumerateArray().Select(x => x.GetString()));
		Assert.Equal("North Line", sent.GetProperty("Studios")[0].GetProperty("Name").GetString());
		Assert.Equal(["Name"], sent.GetProperty("LockedFields").EnumerateArray().Select(x => x.GetString()));
		Assert.Equal("tt0000001", sent.GetProperty("ProviderIds").GetProperty("Imdb").GetString());
		Assert.Equal("101", sent.GetProperty("ProviderIds").GetProperty("Tmdb").GetString());
		Assert.Equal(2004, sent.GetProperty("PremiereDate").GetDateTimeOffset().Year);
		Assert.Equal(2020, sent.GetProperty("DateCreated").GetDateTimeOffset().Year);
		Assert.False(_vm.HasChanges);
	}

	[Fact]
	public async Task Save_WithNothingChanged_SendsNothing()
	{
		Assert.True(await _vm.Save());

		Assert.Empty(_server.Changes);
	}

	[Fact]
	public async Task Save_SendsTheChangedFields_TheReleaseDate_AndTheExternalIds()
	{
		_vm.Title = "Night Train (Director's Cut)";
		_vm.SortTitle = "Night Train 2";
		_vm.ReleaseDate = new DateTimeOffset(2005, 6, 1, 12, 0, 0, TimeSpan.Zero);
		_vm.ExternalIds.Single(x => x.Key == "Imdb").Value = "";
		_vm.ExternalIds.Single(x => x.Key == "Tvdb").Value = "777";
		_vm.AddGenre("Thriller");
		_vm.AddGenre("  ");

		Assert.True(await _vm.Save());

		var sent = Assert.Single(_server.Changes).Json;
		Assert.Equal("Night Train (Director's Cut)", sent.GetProperty("Name").GetString());
		Assert.Equal("Night Train 2", sent.GetProperty("ForcedSortName").GetString());
		Assert.Equal(2005, sent.GetProperty("PremiereDate").GetDateTimeOffset().Year);
		Assert.Equal(["Drama", "Mystery", "Thriller"], sent.GetProperty("Genres").EnumerateArray().Select(x => x.GetString()));
		var ids = sent.GetProperty("ProviderIds");
		Assert.False(ids.TryGetProperty("Imdb", out _));
		Assert.Equal("101", ids.GetProperty("Tmdb").GetString());
		Assert.Equal("777", ids.GetProperty("Tvdb").GetString());
		Assert.Equal("A long night on a train.", sent.GetProperty("Overview").GetString());
	}

	[Fact]
	public async Task Save_TheServerRefuses_KeepsTheEdits_AndSaysSo()
	{
		_server.Respond("POST", ItemPath, 500);
		_vm.Title = "Changed";

		Assert.False(await _vm.Save());

		Assert.Single(_server.Changes);
		Assert.Equal("Changed", _vm.Title);
		Assert.True(_vm.HasChanges);
		Assert.NotEmpty(_vm.Error);
	}

	[Fact]
	public void AnEmptyNumberBox_IsNoValue_NotAChange()
	{
		_vm.Year = double.NaN;
		Assert.True(_vm.HasChanges);
		_vm.Year = 2004;
		Assert.False(_vm.HasChanges);
	}

	/// <summary>Review fix: a failed Save wrote its edits into the loaded item, so after Revert the next Save sent them anyway.</summary>
	[Fact]
	public async Task AFailedSave_ThenRevert_ThenAnotherEdit_SendsTheLoadedTitle()
	{
		_server.Respond("POST", ItemPath, 500);
		_vm.Title = "Changed";
		Assert.False(await _vm.Save());
		_vm.RevertCommand.Execute(null);
		_server.Respond("POST", ItemPath, 204);
		_vm.Overview = "A shorter night.";

		Assert.True(await _vm.Save());

		var sent = _server.Changes[^1].Json;
		Assert.Equal("Night Train", sent.GetProperty("Name").GetString());
		Assert.Equal("A shorter night.", sent.GetProperty("Overview").GetString());
	}

	/// <summary>Review fix: Save sent the item as loaded, overwriting what the server changed while the dialog was open.</summary>
	[Fact]
	public async Task Save_KeepsWhatTheServerChanged_WhileTheDialogWasOpen()
	{
		_server.Respond("GET", ItemPath, 200, ItemJson.Replace("\"Tags\":[\"festival\"]", "\"Tags\":[\"festival\",\"restored\"]").Replace("\"OfficialRating\":\"PG-13\"", "\"OfficialRating\":\"R\""));
		_vm.Overview = "A shorter night.";

		Assert.True(await _vm.Save());

		var sent = _server.Changes.Single().Json;
		Assert.Equal(["festival", "restored"], sent.GetProperty("Tags").EnumerateArray().Select(x => x.GetString()));
		Assert.Equal("R", sent.GetProperty("OfficialRating").GetString());
		Assert.Equal("A shorter night.", sent.GetProperty("Overview").GetString());
	}

	/// <summary>Review fix: clearing the title and saving sent Name: null.</summary>
	[Fact]
	public async Task ABlankTitle_CannotBeSaved_AndTheDialogSaysWhy()
	{
		_vm.Title = "  ";

		Assert.True(_vm.HasChanges);
		Assert.True(_vm.TitleIsBlank);
		Assert.False(_vm.CanSave);
		Assert.False(await _vm.Save());
		Assert.Equal(EditMetadataViewModel.BlankTitleMessage, _vm.Error);
		Assert.Empty(_server.Changes);

		_vm.Title = "Night Train";
		Assert.False(_vm.TitleIsBlank);
	}
}
