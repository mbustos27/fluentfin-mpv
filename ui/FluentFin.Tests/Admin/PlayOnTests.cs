using FluentFin.Core;
using FluentFin.Dialogs.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, button review A71 / A72, worst offender 5): Play On's "Select" was always enabled and threw when nothing
/// was selected. Now its primary ("Play there") waits for a device; invoked anyway it sends nothing; a pick sends exactly one
/// play request to that device. An empty list says so; this app's own session is not offered.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class PlayOnTests : IAsyncLifetime
{
	private static readonly Guid MovieId = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");

	private const string SessionsJson = """
		[{"Id":"own-session","Client":"Nixie","DeviceName":"This PC","UserName":"admin"},
		 {"Id":"tv-session","Client":"Jellyfin Android TV","DeviceName":"Living room TV","UserName":"admin"}]
		""";

	private readonly AdminFakeServer _server = new();
	private readonly string _ownSession = SessionInfo.SessionId;
	private SessionPickerViewModel _vm = null!;

	static PlayOnTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public async Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("GET", "/Sessions", 200, SessionsJson);
		_server.Respond("POST", "/Sessions/tv-session/Playing", 204);
		_vm = new SessionPickerViewModel(await _server.SignIn());
		SessionInfo.SessionId = "own-session";
		await _vm.Initialize(new BaseItemDto { Id = MovieId, Type = BaseItemDto_Type.Movie, Name = "Night Train" });
	}

	public Task DisposeAsync()
	{
		SessionInfo.SessionId = _ownSession;
		_server.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task NothingPicked_ThePrimaryIsDisabled_AndInvokedAnyway_SendsNothing()
	{
		Assert.Null(_vm.SelectedSesion);
		Assert.False(_vm.CanPick);
		Assert.Equal("Play there", _vm.PickText);

		await _vm.PlayOnSessionCommand.ExecuteAsync(null);

		Assert.Empty(_server.Changes);
	}

	[Fact]
	public async Task APick_SendsOnePlayRequest_ToThatDevice_WithTheItem()
	{
		_vm.SelectedSesion = _vm.Sessions.Single();
		Assert.True(_vm.CanPick);

		await _vm.PlayOnSessionCommand.ExecuteAsync(null);

		var play = Assert.Single(_server.Changes);
		Assert.Equal("POST /Sessions/tv-session/Playing", $"{play.Method} {play.Path}");
		Assert.Contains(MovieId.ToString(), play.Query["itemIds"], StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ThePlayRequestRefused_DoesNotThrow()
	{
		_server.Respond("POST", "/Sessions/tv-session/Playing", 500);
		_vm.SelectedSesion = _vm.Sessions.Single();

		await _vm.PlayOnSessionCommand.ExecuteAsync(null);

		Assert.Single(_server.Changes);
	}

	[Fact]
	public void ThisAppsOwnSession_IsNotOffered_AndEachDeviceIsNamed()
	{
		Assert.Equal(["tv-session"], _vm.Sessions.Select(x => x.Id));
		Assert.False(_vm.IsEmpty);
		Assert.Equal("Jellyfin Android TV on Living room TV, admin", SessionPickerViewModel.Describe(_vm.Sessions[0]));
	}

	[Fact]
	public async Task NoOtherDevice_IsAnEmptyList_ThatSaysSo()
	{
		_server.Respond("GET", "/Sessions", 200, "[]");
		var empty = new SessionPickerViewModel(await _server.SignIn());
		await empty.Initialize();

		Assert.True(empty.IsEmpty);
		Assert.False(empty.CanPick);
		Assert.Equal("Use this device", empty.PickText);
	}
}
