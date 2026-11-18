using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FluentFin.Dialogs.ViewModels
{
	public partial class AddUserViewModel(IJellyfinClient jellyfinClient,
										  [FromKeyedServices(NavigationRegions.Settings)] INavigationServiceCore navigationService) : ObservableObject, IHandleClose
	{
		/// <summary>Nixie 1.2.0 (admin, D46): "Create user" waits for a name; a password is optional, as on the server.</summary>
		[ObservableProperty]
		[NotifyPropertyChangedFor(nameof(CanCreate))]
		public partial string? Username { get; set; }

		public bool CanCreate => !string.IsNullOrWhiteSpace(Username);

		/// <summary>Why the user wasn't created; empty otherwise.</summary>
		[ObservableProperty]
		[NotifyPropertyChangedFor(nameof(HasError))]
		public partial string Error { get; set; } = "";

		/// <summary>The error bar opens from this bool (no x:Bind function on IsOpen).</summary>
		public bool HasError => !string.IsNullOrEmpty(Error);

		[ObservableProperty]
		public partial string? Password { get; set; }

		[ObservableProperty]
		public partial bool EnableAllFolders { get; set; }

		[ObservableProperty]
		public partial List<BaseItemDto> MediaFolders { get; set; } = [];

		[ObservableProperty]
		public partial List<BaseItemDto> SelectedItems { get; set; } = [];

		public bool CanClose { get; set; }

		public async Task Initialize()
		{
			var response = await jellyfinClient.GetMediaFolders();

			if (response is { Items.Count: > 0 })
			{
				MediaFolders = response.Items;
			}
		}


		/// <summary>
		/// Creates the user; true when the server did (the dialog closes only then, D46: it closed on an empty form and created
		/// nothing). The password is sent once and not kept.
		/// </summary>
		public async Task<bool> AddUser()
		{
			Error = "";
			if (!CanCreate)
			{
				Error = "Type a name for the new user.";
				return false;
			}

			var user = await jellyfinClient.CreateUser(Username!.Trim(), Password ?? "");
			Password = null;

			if (user is null)
			{
				Error = "The server didn't create the user. The name may already be taken.";
				return false;
			}

			if (user.Policy is { } policy)
			{
				if (EnableAllFolders)
				{
					policy.EnableAllFolders = true;
				}
				else
				{
					policy.EnableAllFolders = false;
					policy.EnabledFolders = SelectedItems.Select(x => x.Id).ToList();
				}

				await jellyfinClient.UpdatePolicy(user, policy);
			}

			navigationService.NavigateTo<UserEditorViewModel>(new UserEditorViewModelNavigationParameter(user, UserEditorSection.Profile));
			return true;
		}
	}
}
