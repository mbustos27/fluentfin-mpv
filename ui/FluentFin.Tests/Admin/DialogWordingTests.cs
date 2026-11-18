using FluentFin.Core.ViewModels;
using FluentFin.Dialogs.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, button review D43 / D65 / D77 / D79): the dialogs' and pages' smaller verdicts that change behaviour:
/// Identify's second step is "Apply" and waits for a pick, Refresh metadata says what a replace-all choice overwrites before
/// it runs, the rename dialog's primary says "Rename" and waits for a name, and a scheduled task's card says it started.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class DialogWordingTests
{
	static DialogWordingTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	[Fact]
	public void Identify_Apply_WaitsForAPickedMatch()
	{
		var vm = new IdentifyViewModel(Mock.Of<Core.Contracts.Services.IJellyfinClient>());
		Assert.Equal("Search", vm.PrimaryButtonText);
		Assert.True(vm.CanUsePrimary);

		vm.ViewState = IdentifyViewModelState.Loading;
		Assert.False(vm.CanUsePrimary);

		vm.ViewState = IdentifyViewModelState.Result;
		Assert.Equal("Apply", vm.PrimaryButtonText);
		Assert.Equal("Back", vm.SecondaryButtonText);
		Assert.False(vm.CanUsePrimary);

		vm.SelectedResult = new RemoteSearchResult { Name = "Night Train" };
		Assert.True(vm.CanUsePrimary);
	}

	[Fact]
	public async Task RefreshMetadata_SaysWhatAReplaceAllChoiceOverwrites()
	{
		var vm = new RefreshMetadataViewModel(Mock.Of<Core.Contracts.Services.IJellyfinClient>());
		await vm.Initialize(Guid.NewGuid());
		Assert.Equal("", vm.ReplaceWarning);

		vm.ReplaceExistingImages = true;
		Assert.Equal("", vm.ReplaceWarning); // hidden with "Scan for new and updated files", so not applied

		vm.SelectedRefreshMode = vm.RefreshModes[1];
		Assert.Contains("the current images", vm.ReplaceWarning);
		Assert.DoesNotContain("every metadata field", vm.ReplaceWarning);

		vm.SelectedRefreshMode = vm.RefreshModes[2];
		Assert.Contains("every metadata field", vm.ReplaceWarning);
		Assert.Contains("can't be undone", vm.ReplaceWarning);
	}

	[Fact]
	public void StringPicker_ThePrimarySaysWhatItDoes_AndWaitsForText()
	{
		var vm = new StringPickerViewModel { ConfirmText = "Rename" };
		Assert.False(vm.CanConfirm);
		vm.Name = "  ";
		Assert.False(vm.CanConfirm);
		vm.Name = "Children";
		Assert.True(vm.CanConfirm);
	}
}

/// <summary>Nixie 1.2.0 (admin, D43 / D36): Run now and the user editor's Save say how it went, against a fake server.</summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class AdminSaveNoticeTests : IAsyncLifetime
{
	private readonly AdminFakeServer _server = new();
	private Core.Services.JellyfinClient _client = null!;

	static AdminSaveNoticeTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public async Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("POST", "/ScheduledTasks/Running/task-1", 204);
		_client = await _server.SignIn();
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	[Theory]
	[InlineData(204, "Started: Scan Media Library")]
	[InlineData(500, "Scan Media Library didn't start")]
	public async Task RunNow_SendsOneRequest_AndSaysHowItWent(int status, string title)
	{
		_server.Respond("POST", "/ScheduledTasks/Running/task-1", status);
		var page = new ScheduledTasksViewModel(_client);

		await page.RunCommand.ExecuteAsync(new TaskInfo { Id = "task-1", Name = "Scan Media Library" });

		var run = Assert.Single(_server.Changes);
		Assert.Equal("POST /ScheduledTasks/Running/task-1", $"{run.Method} {run.Path}");
		Assert.Equal(title, page.Notice!.Title);
	}

	[Theory]
	[InlineData(204, "Saved")]
	[InlineData(500, "Not saved")]
	public async Task UserEditor_Save_SaysHowItWent(int status, string title)
	{
		var bob = Guid.NewGuid();
		_server.Respond("POST", $"/Users/{bob}/Policy", status);
		var editor = new UserEditorViewModel(Mock.Of<Core.Services.INavigationServiceCore>(), _client)
		{
			User = new UserDto { Id = bob, Name = "bob", Policy = new UserPolicy { IsAdministrator = false } },
		};

		await editor.SaveCommand.ExecuteAsync(null);

		Assert.Equal($"POST /Users/{bob}/Policy", $"{_server.Changes.Single().Method} {_server.Changes.Single().Path}");
		Assert.Equal(title, editor.Notice!.Title);
	}
}
