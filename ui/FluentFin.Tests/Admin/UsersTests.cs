using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Dialogs.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, button review D31 / D38-D40 / D46 / D81, worst offenders 4 and 7): "Reset Password" cleared a user's
/// password in one click, and Delete user's confirmation made an accent "Yes" the Enter key and left the deleted card. Now
/// each asks, naming the user; a "no" (Cancel, Escape, Enter) sends nothing, a "yes" exactly one request; the user list is
/// read again after a delete. Add user waits for a name and closes only once the user exists. Fake passwords, fake server.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class UsersTests : IAsyncLifetime
{
	private static readonly Guid BobId = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

	private static readonly string UsersJson = $$$"""
		[{"Name":"admin","Id":"{{{AdminFakeServer.AdminId:N}}}","Policy":{"IsAdministrator":true}},
		 {"Name":"bob","Id":"{{{BobId:N}}}","Policy":{"IsAdministrator":false}}]
		""";

	private readonly AdminFakeServer _server = new();
	private readonly List<ConfirmPrompt> _asked = [];
	private Core.Services.JellyfinClient _client = null!;

	static UsersTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public async Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("GET", "/Users", 200, UsersJson);
		_server.Respond("DELETE", $"/Users/{BobId}", 204);
		_server.Respond("POST", "/Users/Password", 204);
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

	private async Task<UsersViewModel> UsersPage(bool yes)
	{
		var page = new UsersViewModel(_client, Mock.Of<INavigationServiceCore>()) { Confirm = Answer(yes) };
		await page.OnNavigatedTo(new());
		return page;
	}

	private async Task<UserPasswordEditorViewModel> PasswordPage(bool yes)
	{
		var page = new UserPasswordEditorViewModel(_client) { Confirm = Answer(yes) };
		await page.OnNavigatedTo(new UserDto { Id = BobId, Name = "bob" });
		return page;
	}

	[Fact]
	public async Task DeleteUser_ANo_SendsNothing_AndKeepsTheCard()
	{
		var page = await UsersPage(yes: false);
		var bob = page.Users.Single(x => x.Name == "bob");

		await page.DeleteUserCommand.ExecuteAsync(bob);

		var prompt = Assert.Single(_asked);
		Assert.Equal("Delete bob?", prompt.Title);
		Assert.Equal("Delete user", prompt.ConfirmText);
		Assert.Empty(_server.Changes);
		Assert.Equal(2, page.Users.Count);
	}

	[Fact]
	public async Task DeleteUser_AYes_SendsOneDelete_ForThatUser_ThenReadsTheListAgain()
	{
		var page = await UsersPage(yes: true);
		var reads = _server.Requests.Count(x => x.Method == "GET" && x.Path == "/Users");
		_server.Respond("GET", "/Users", 200, $$$"""[{"Name":"admin","Id":"{{{AdminFakeServer.AdminId:N}}}"}]""");

		await page.DeleteUserCommand.ExecuteAsync(page.Users.Single(x => x.Name == "bob"));

		var delete = Assert.Single(_server.Changes);
		Assert.Equal($"DELETE /Users/{BobId}", $"{delete.Method} {delete.Path}");
		Assert.Equal(reads + 1, _server.Requests.Count(x => x.Method == "GET" && x.Path == "/Users"));
		Assert.Equal(["admin"], page.Users.Select(x => x.Name));
	}

	[Fact]
	public async Task DeleteUser_TheServerRefuses_SaysSo_AndTheCardStays()
	{
		_server.Respond("DELETE", $"/Users/{BobId}", 500);
		var page = await UsersPage(yes: true);

		await page.DeleteUserCommand.ExecuteAsync(page.Users.Single(x => x.Name == "bob"));

		Assert.Single(_server.Changes);
		Assert.Equal(2, page.Users.Count);
		Assert.Equal("bob wasn't deleted", page.Notice!.Title);
	}

	[Fact]
	public async Task RemovePassword_ANo_SendsNothing()
	{
		var page = await PasswordPage(yes: false);

		await page.RemovePassword();

		var prompt = Assert.Single(_asked);
		Assert.Equal("Remove bob's password?", prompt.Title);
		Assert.Equal("Remove password", prompt.ConfirmText);
		Assert.Empty(_server.Changes);
		Assert.Null(page.Notice);
	}

	[Fact]
	public async Task RemovePassword_AYes_SendsOneReset_ForThatUser()
	{
		var page = await PasswordPage(yes: true);

		await page.RemovePassword();

		var reset = Assert.Single(_server.Changes);
		Assert.Equal("POST /Users/Password", $"{reset.Method} {reset.Path}");
		Assert.Equal(BobId, Guid.Parse(reset.Query["userId"]!));
		Assert.True(reset.Json.GetProperty("ResetPassword").GetBoolean());
		Assert.Equal("Password removed", page.Notice!.Title);
	}

	[Fact]
	public async Task ChangePassword_TwoDifferentNewPasswords_SendNothing_AndSaySo()
	{
		var page = await PasswordPage(yes: true);

		Assert.False(await page.ChangePassword("fake-old", "fake-new-1", "fake-new-2"));

		Assert.Empty(_server.Changes);
		Assert.Equal("The passwords don't match", page.Notice!.Title);
	}

	[Fact]
	public async Task ChangePassword_Matching_SendsOneChange_AndAsksNothing()
	{
		var page = await PasswordPage(yes: false);

		Assert.True(await page.ChangePassword("fake-old", "fake-new", "fake-new"));

		var change = Assert.Single(_server.Changes);
		Assert.Equal("POST /Users/Password", $"{change.Method} {change.Path}");
		Assert.Equal("fake-new", change.Json.GetProperty("NewPw").GetString());
		Assert.Empty(_asked);
		Assert.Equal("Password changed", page.Notice!.Title);
	}

	[Fact]
	public async Task AddUser_WithoutAName_CannotCreate_AndSendsNothing()
	{
		var dialog = new AddUserViewModel(_client, Mock.Of<INavigationServiceCore>());

		Assert.False(dialog.CanCreate);
		Assert.False(await dialog.AddUser());

		Assert.Empty(_server.Changes);
	}

	[Fact]
	public async Task AddUser_WithAName_CreatesIt_WithTheLibrariesPicked()
	{
		var carol = Guid.NewGuid();
		var kids = Guid.NewGuid();
		_server.Respond("POST", "/Users/New", 200, $$$"""{"Name":"carol","Id":"{{{carol:N}}}","Policy":{"EnableAllFolders":true,"EnabledFolders":[]}}""");
		_server.Respond("POST", $"/Users/{carol}/Policy", 204);
		var dialog = new AddUserViewModel(_client, Mock.Of<INavigationServiceCore>()) { Username = " carol ", EnableAllFolders = false };
		dialog.SelectedItems = [new BaseItemDto { Id = kids, Name = "Kids" }];

		Assert.True(dialog.CanCreate);
		Assert.True(await dialog.AddUser());

		Assert.Equal(["POST /Users/New", $"POST /Users/{carol}/Policy"], _server.Changes.Select(x => $"{x.Method} {x.Path}"));
		Assert.Equal("carol", _server.Changes[0].Json.GetProperty("Name").GetString());
		var policy = _server.Changes[1].Json;
		Assert.False(policy.GetProperty("EnableAllFolders").GetBoolean());
		Assert.Equal([kids], policy.GetProperty("EnabledFolders").EnumerateArray().Select(x => x.GetGuid()));
	}

	[Fact]
	public async Task AddUser_TheServerRefuses_KeepsTheDialog_AndSaysSo()
	{
		_server.Respond("POST", "/Users/New", 400);
		var dialog = new AddUserViewModel(_client, Mock.Of<INavigationServiceCore>()) { Username = "admin" };

		Assert.False(await dialog.AddUser());

		Assert.Single(_server.Changes);
		Assert.StartsWith("The server didn't create the user", dialog.Error);
	}
}
