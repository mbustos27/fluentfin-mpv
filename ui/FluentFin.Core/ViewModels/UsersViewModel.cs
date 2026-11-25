using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Admin;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FluentFin.Core.ViewModels;

/// <remarks>
/// Nixie 1.2.0 (admin, button review D31 / D81): Delete user asks first, naming the user (Cancel the default, "Delete user" the
/// verb; it was an accent "Yes" as the Enter key), and the list is read again afterwards (the deleted user's card stayed).
/// </remarks>
public partial class UsersViewModel(IJellyfinClient jellyfinClient,
									[FromKeyedServices(NavigationRegions.Settings)] INavigationServiceCore navigationService) : ObservableObject, INavigationAware
{
	[ObservableProperty]
	public partial List<UserDto> Users { get; set; } = [];

	/// <summary>Asks before a destructive action; true only when the user confirmed. Unset (nobody to ask), nothing happens.</summary>
	public Func<ConfirmPrompt, Task<bool>>? Confirm { get; set; }

	/// <summary>What the last action here did, when there is something to say.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNotice))]
	public partial ServerNotice? Notice { get; set; }

	/// <summary>The notice bar opens from this bool (x:Bind does not call a function whose argument is null, so a bar opened through one never closed).</summary>
	public bool HasNotice => Notice is not null;

	[RelayCommand]
	private async Task DeleteUser(UserDto? user)
	{
		if (user is null || Confirm is not { } confirm || !await confirm(AdminPrompts.DeleteUser(user.Name)))
		{
			return;
		}

		if (!await jellyfinClient.DeleteUser(user))
		{
			Notice = new ServerNotice(ServerPowerState.Failed, $"{user.Name} wasn't deleted", Admin.ServerPower.NotTaken("The server didn't accept the request. Nothing changed."), NoticeKind.Error);
			return;
		}

		Notice = null;
		Users = await jellyfinClient.GetUsers();
	}

	public Task OnNavigatedFrom() => Task.CompletedTask;

	public async Task OnNavigatedTo(object parameter)
	{
		Users = await jellyfinClient.GetUsers();
	}

	public void Navigate(UserDto user, UserEditorSection section)
	{
		navigationService.NavigateTo("FluentFin.Dialogs.ViewModels.UserEditorViewModel", new UserEditorViewModelNavigationParameter(user, section));
	}
}

public record UserEditorViewModelNavigationParameter(UserDto User, UserEditorSection Section);

public enum UserEditorSection
{
	Profile,
	Access,
	ParentalControl,
	Password
}