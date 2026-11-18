using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Dialogs.ViewModels;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, button review D50 / D53 / D62 / D89 / D90, worst offender 7): Delete image, Delete subtitle and Delete
/// segment deleted on the server at once. Now each asks first, naming what goes; a "no" (Cancel, Escape, Enter) sends
/// nothing, a "yes" exactly one DELETE for that one thing, and the row goes only when the server deleted it. Edit images'
/// Back returns to the list; Save segment replaces the segment it created instead of adding a copy.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class ItemDeletesTests : IAsyncLifetime
{
	private static readonly Guid ItemId = Guid.Parse("dddddddd-0000-0000-0000-00000000000d");
	private static readonly BaseItemDto Movie = new() { Id = ItemId, Name = "Night Train", Type = BaseItemDto_Type.Movie };

	private readonly AdminFakeServer _server = new();
	private readonly List<ConfirmPrompt> _asked = [];
	private Core.Services.JellyfinClient _client = null!;

	static ItemDeletesTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public async Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("GET", $"/Items/{ItemId}/Images", 200, """[{"ImageType":"Primary","ImageIndex":0,"ImageTag":"p1"},{"ImageType":"Backdrop","ImageIndex":0,"ImageTag":"b1"}]""");
		_server.Respond("GET", $"/Items/{ItemId}/RemoteImages/Providers", 200, """[{"Name":"TheMovieDb","SupportedImages":["Primary","Backdrop"]}]""");
		_server.Respond("GET", $"/Items/{ItemId}/RemoteImages", 200, """{"Images":[],"TotalRecordCount":0,"Providers":["TheMovieDb"]}""");
		_server.Respond("DELETE", $"/Items/{ItemId}/Images/Backdrop/0", 204);
		_server.Respond("GET", $"/Items/{ItemId}", 200, $$$"""
			{"Id":"{{{ItemId:N}}}","Name":"Night Train","Type":"Movie","MediaStreams":[
			 {"Type":"Subtitle","Index":2,"IsExternal":false,"DisplayTitle":"English - SUBRIP"},
			 {"Type":"Subtitle","Index":3,"IsExternal":true,"DisplayTitle":"Spanish - SRT - External"}]}
			""");
		_server.Respond("DELETE", $"/Videos/{ItemId}/Subtitles/3", 204);
		_client = await _server.SignIn();
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	private Func<ConfirmPrompt, Task<bool>> Answer(bool yes) => prompt =>
	{
		_asked.Add(prompt);
		return Task.FromResult(yes);
	};

	private async Task<EditImagesViewModel> Images(bool yes)
	{
		var dialog = new EditImagesViewModel(_client) { Confirm = Answer(yes) };
		await dialog.Initialize(Movie);
		return dialog;
	}

	private async Task<EditSubtitlesViewModel> Subtitles(bool yes)
	{
		var dialog = new EditSubtitlesViewModel(_client) { Confirm = Answer(yes) };
		await dialog.Initialize(Movie);
		return dialog;
	}

	private MediaSegmentsEditorViewModel Segments(bool yes) => new(_client, Mock.Of<ISettings>()) { Confirm = Answer(yes) };

	[Fact]
	public async Task DeleteImage_ANo_SendsNothing_AndKeepsTheTile()
	{
		var dialog = await Images(yes: false);
		var backdrop = dialog.Images.Single(x => x.ImageType == ImageInfo_ImageType.Backdrop);

		await dialog.DeleteImageCommand.ExecuteAsync(backdrop);

		var prompt = Assert.Single(_asked);
		Assert.Equal("Delete the backdrop image?", prompt.Title);
		Assert.Contains("Night Train", prompt.Message);
		Assert.Equal("Delete image", prompt.ConfirmText);
		Assert.Empty(_server.Changes);
		Assert.Equal(2, dialog.Images.Count);
	}

	[Fact]
	public async Task DeleteImage_AYes_SendsOneDelete_ForThatImage_AndTheTileGoes()
	{
		var dialog = await Images(yes: true);

		await dialog.DeleteImageCommand.ExecuteAsync(dialog.Images.Single(x => x.ImageType == ImageInfo_ImageType.Backdrop));

		var delete = Assert.Single(_server.Changes);
		Assert.Equal($"DELETE /Items/{ItemId}/Images/Backdrop/0", $"{delete.Method} {delete.Path}");
		Assert.Equal([ImageInfo_ImageType.Primary], dialog.Images.Select(x => x.ImageType));
	}

	[Fact]
	public async Task DeleteImage_TheServerRefuses_TheTileStays_AndItSaysSo()
	{
		_server.Respond("DELETE", $"/Items/{ItemId}/Images/Backdrop/0", 500);
		var dialog = await Images(yes: true);

		await dialog.DeleteImageCommand.ExecuteAsync(dialog.Images.Single(x => x.ImageType == ImageInfo_ImageType.Backdrop));

		Assert.Single(_server.Changes);
		Assert.Equal(2, dialog.Images.Count);
		Assert.NotEmpty(dialog.Error);
	}

	[Fact]
	public async Task EditImages_Back_ReturnsToTheImages()
	{
		var dialog = await Images(yes: false);
		await dialog.SwitchViewCommand.ExecuteAsync(EditImagesViewModelState.Search);
		var searches = _server.Requests.Count(x => x.Path.EndsWith("/RemoteImages", StringComparison.OrdinalIgnoreCase));

		await dialog.SwitchViewCommand.ExecuteAsync(EditImagesViewModelState.Display);

		Assert.Equal(EditImagesViewModelState.Display, dialog.State);
		Assert.Equal(searches, _server.Requests.Count(x => x.Path.EndsWith("/RemoteImages", StringComparison.OrdinalIgnoreCase)));
	}

	[Fact]
	public async Task DeleteSubtitle_ANo_SendsNothing_AndKeepsTheRow()
	{
		var dialog = await Subtitles(yes: false);
		var spanish = dialog.Subtitles.Single(x => x.IsExternal == true);

		await dialog.DeleteSubtitleCommand.ExecuteAsync(spanish);

		var prompt = Assert.Single(_asked);
		Assert.Equal("Delete the subtitle \"Spanish - SRT - External\"?", prompt.Title);
		Assert.Equal("Delete subtitle", prompt.ConfirmText);
		Assert.Empty(_server.Changes);
		Assert.Contains(spanish, dialog.Subtitles);
	}

	[Fact]
	public async Task DeleteSubtitle_AYes_SendsOneDelete_ForThatSubtitle_AndTheRowGoes()
	{
		var dialog = await Subtitles(yes: true);
		var spanish = dialog.Subtitles.Single(x => x.IsExternal == true);

		await dialog.DeleteSubtitleCommand.ExecuteAsync(spanish);

		var delete = Assert.Single(_server.Changes);
		Assert.Equal($"DELETE /Videos/{ItemId}/Subtitles/3", $"{delete.Method} {delete.Path}");
		Assert.DoesNotContain(spanish, dialog.Subtitles);
	}

	[Fact]
	public async Task DeleteSubtitle_TheServerRefuses_TheRowStays()
	{
		_server.Respond("DELETE", $"/Videos/{ItemId}/Subtitles/3", 500);
		var dialog = await Subtitles(yes: true);
		var spanish = dialog.Subtitles.Single(x => x.IsExternal == true);

		await dialog.DeleteSubtitleCommand.ExecuteAsync(spanish);

		Assert.Single(_server.Changes);
		Assert.Contains(spanish, dialog.Subtitles);
		Assert.NotEmpty(dialog.Error);
	}

	[Fact]
	public async Task DeleteSubtitle_AnEmbeddedOne_IsNeverAskedOrSent()
	{
		var dialog = await Subtitles(yes: true);

		await dialog.DeleteSubtitleCommand.ExecuteAsync(dialog.Subtitles.Single(x => x.IsExternal != true));

		Assert.Empty(_asked);
		Assert.Empty(_server.Changes);
	}

	[Fact]
	public async Task DeleteSegment_ANo_SendsNothing_AndKeepsTheCard()
	{
		var editor = Segments(yes: false);
		var intro = new MediaSegmentViewModel { Id = Guid.NewGuid(), ItemId = ItemId, Type = MediaSegmentDto_Type.Intro };
		editor.Segments.Add(intro);

		await editor.DeleteSegmentCommand.ExecuteAsync(intro);

		var prompt = Assert.Single(_asked);
		Assert.Equal("Delete the intro segment?", prompt.Title);
		Assert.Equal("Delete segment", prompt.ConfirmText);
		Assert.Empty(_server.Changes);
		Assert.Contains(intro, editor.Segments);
	}

	[Fact]
	public async Task DeleteSegment_AYes_SendsOneDelete_ForThatSegment_AndTheCardGoes()
	{
		var editor = Segments(yes: true);
		var id = Guid.NewGuid();
		_server.Respond("DELETE", $"/MediaSegmentsApi/{id:N}", 204);
		var intro = new MediaSegmentViewModel { Id = id, ItemId = ItemId, Type = MediaSegmentDto_Type.Intro };
		editor.Segments.Add(intro);

		await editor.DeleteSegmentCommand.ExecuteAsync(intro);

		var delete = Assert.Single(_server.Changes);
		Assert.Equal($"DELETE /MediaSegmentsApi/{id:N}", $"{delete.Method} {delete.Path}");
		Assert.Empty(editor.Segments);
	}

	[Fact]
	public async Task DeleteSegment_NeverSaved_GoesWithoutAQuestion_OrARequest()
	{
		var editor = Segments(yes: false);
		var draft = new MediaSegmentViewModel { ItemId = ItemId };
		editor.Segments.Add(draft);

		await editor.DeleteSegmentCommand.ExecuteAsync(draft);

		Assert.Empty(_asked);
		Assert.Empty(_server.Changes);
		Assert.Empty(editor.Segments);
	}

	[Fact]
	public async Task SaveSegment_Twice_ReplacesTheOneItCreated_InsteadOfAddingACopy()
	{
		var editor = Segments(yes: false);
		_server.Respond("POST", $"/MediaSegmentsApi/{ItemId:N}", 200, "{}");
		var draft = new MediaSegmentViewModel { ItemId = ItemId, Type = MediaSegmentDto_Type.Outro, StartTicks = 10, EndTicks = 20 };

		await editor.SubmitSegmentCommand.ExecuteAsync(draft);
		var created = Assert.Single(_server.Changes);
		Assert.NotNull(draft.Id);
		_server.Respond("DELETE", $"/MediaSegmentsApi/{draft.Id:N}", 204);
		await editor.SubmitSegmentCommand.ExecuteAsync(draft);

		Assert.Equal(
			[$"POST /MediaSegmentsApi/{ItemId:N}", $"DELETE /MediaSegmentsApi/{draft.Id:N}", $"POST /MediaSegmentsApi/{ItemId:N}"],
			_server.Changes.Select(x => $"{x.Method} {x.Path}"));
		Assert.Equal(draft.Id!.Value.ToString("N"), created.Json.GetProperty("Id").GetString());
	}
}
