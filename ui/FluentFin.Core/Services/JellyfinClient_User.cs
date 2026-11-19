using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

public partial class JellyfinClient
{
	public async Task<BaseItemDtoQueryResult?> GetContinueWatching()
	{
		try
		{
			return await _jellyfinApiClient.UserItems.Resume.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.Limit = 12;
				// Seaglass: the hero shows the overview and the logo of what it features
				query.Fields = [ItemFields.PrimaryImageAspectRatio, ItemFields.Overview];
				query.ImageTypeLimit = 1;
				query.EnableImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Thumb, ImageType.Logo];
				query.EnableTotalRecordCount = false;
				query.MediaTypes = [MediaType.Video];
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	internal const int NextUpDays = 365;

	public async Task<BaseItemDtoQueryResult?> GetNextUp()
	{
		try
		{
			return await _jellyfinApiClient.Shows.NextUp.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.Limit = 24;
				query.Fields = [ItemFields.PrimaryImageAspectRatio, ItemFields.DateCreated, ItemFields.Path, ItemFields.MediaSourceCount];
				query.UserId = UserId;
				query.ImageTypeLimit = 1;
				query.EnableImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Thumb];
				query.EnableTotalRecordCount = false;
				// fluentfin-mpv: the oldest last-watched date of the shows to list, as Jellyfin web sends it (now minus its
				// maxDaysForNextUp, 365 by default); "now" left out every show watched before now, so Next Up stayed empty
				query.NextUpDateCutoff = Time.GetUtcNow().AddDays(-NextUpDays);
				query.EnableResumable = false;
				query.EnableRewatching = false;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	// fluentfin-mpv: Home used to read the views and then each library's Latest one after another (one round trip per
	// library before anything showed); it now asks for every row at once (GetLatestItems) and reads the views from here,
	// where sign-in already put them.
	public async Task<List<BaseItemDto>?> GetLatestItems(BaseItemDto library)
	{
		try
		{
			return await _jellyfinApiClient.Items.Latest.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.UserId = UserId;
				query.Limit = 16;
				// Seaglass: the hero also features the latest items: their overview and logo
				query.Fields = [ItemFields.Overview];
				query.ImageTypeLimit = 1;
				query.EnableImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Thumb, ImageType.Logo];
				query.ParentId = library.Id;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	// fluentfin-mpv: the views are one request that sign-in (jump list), every Home visit and the Libraries page each made
	// again. Kept for UserViewsLifetime (a library added from another client shows up within minutes, or at once when the
	// server's LibraryChanged message comes through the websocket), dropped on a new sign-in, a library added, removed or
	// renamed here, a UserUpdated message for this user (library access, view order) or a LibraryChanged message; a failed
	// read is not kept. Concurrent callers share one request.
	internal static readonly TimeSpan UserViewsLifetime = TimeSpan.FromMinutes(5);
	internal TimeProvider Time { get; set; } = TimeProvider.System;
	private readonly Lock _userViewsLock = new();
	private Task<IReadOnlyList<BaseItemDto>?>? _userViews;
	private long _userViewsReadAt;

	public async Task<IReadOnlyList<BaseItemDto>> GetUserViews()
	{
		Task<IReadOnlyList<BaseItemDto>?> views;
		lock (_userViewsLock)
		{
			if (_userViews is null || _userViews is { IsCompleted: true, Result: null } || Time.GetElapsedTime(_userViewsReadAt) > UserViewsLifetime)
			{
				_userViewsReadAt = Time.GetTimestamp();
				_userViews = ReadUserViews();
			}
			views = _userViews;
		}
		return await views ?? [];
	}

	public async Task<IReadOnlyList<BaseItemDto>?> GetUserViewsOrNull()
	{
		var views = await GetUserViews();
		if (views.Count > 0)
		{
			return views;
		}

		// none: an empty server, or a read that failed (GetUserViews answers both with none); asked once more to tell them apart
		// (and kept, as GetUserViews keeps its answer)
		Task<IReadOnlyList<BaseItemDto>?> again;
		lock (_userViewsLock)
		{
			_userViewsReadAt = Time.GetTimestamp();
			_userViews = again = ReadUserViews();
		}

		return await again;
	}

	internal void ForgetUserViews()
	{
		lock (_userViewsLock)
		{
			_userViews = null;
		}

		UserViewsChanged?.Invoke(this, EventArgs.Empty);
	}

	public event EventHandler? UserViewsChanged;

	private async Task<IReadOnlyList<BaseItemDto>?> ReadUserViews()
	{
		try
		{
			var views = await _jellyfinApiClient.UserViews.GetAsync(x => x.QueryParameters.UserId = UserId);
			return views?.Items is { } items ? [.. items.OfType<BaseItemDto>()] : null;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async IAsyncEnumerable<BaseItemDto> GetUserLibraries()
	{
		foreach (var item in await GetUserViews())
		{
			yield return item;
		}
	}

	// 1.2.0: both return the server's answer (the item's user data after the change), so the app shows what the
	// server has (ItemUserData); null when the request failed
	public async Task<UserItemDataDto?> SetIsFavorite(BaseItemDto dto, bool isFavorite)
	{
		try
		{
			if (dto.Id is null)
			{
				return null;
			}

			if (isFavorite)
			{
				return await _jellyfinApiClient.UserFavoriteItems[dto.Id.Value].PostAsync();
			}
			else
			{
				return await _jellyfinApiClient.UserFavoriteItems[dto.Id.Value].DeleteAsync();
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<UserItemDataDto?> SetUserData(Guid id, bool played, long positionTicks)
	{
		try
		{
			return await _jellyfinApiClient.UserItems[id].UserData.PostAsync(
				new UpdateUserItemDataDto { Played = played, PlaybackPositionTicks = Math.Max(0, positionTicks) },
				x => x.QueryParameters.UserId = UserId);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Setting the user data of {ItemId} failed", id);
			return null;
		}
	}

	public async Task<UserItemDataDto?> SetPlayed(BaseItemDto dto, bool played)
	{
		try
		{
			if (dto.Id is null)
			{
				return null;
			}

			if (played)
			{
				return await _jellyfinApiClient.UserPlayedItems[dto.Id.Value].PostAsync(x => x.QueryParameters.DatePlayed = TimeProvider.System.GetUtcNow());
			}
			else
			{
				return await _jellyfinApiClient.UserPlayedItems[dto.Id.Value].DeleteAsync();
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<List<UserDto>> GetUsers()
	{
		try
		{
			return await _jellyfinApiClient.Users.GetAsync() ?? [];
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return [];
		}
	}

	public async Task<UserDto?> GetUser(Guid id)
	{
		try
		{
			return await _jellyfinApiClient.Users[id].GetAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<UserDto?> GetCurrentUser()
	{
		try
		{
			return await _jellyfinApiClient.Users.Me.GetAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<bool> SaveUserConfiguration(Guid userId, UserConfiguration configuration)
	{
		try
		{
			await _jellyfinApiClient.Users.Configuration.PostAsync(configuration, x => x.QueryParameters.UserId = userId);
			return true;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	public async Task<bool> DeleteUser(UserDto user)
	{
		if (user.Id is not { } id)
		{
			return false;
		}

		try
		{
			await _jellyfinApiClient.Users[id].DeleteAsync();
			return true; // Nixie 1.2.0 (admin): the card goes only when the server deleted the user
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	public async Task<bool> UpdatePolicy(UserDto user, UserPolicy policy)
	{
		if (user.Id is not { } id)
		{
			return false;
		}

		try
		{
			await _jellyfinApiClient.Users[id].Policy.PostAsync(policy);
			return true; // Nixie 1.2.0 (admin): the user editor says "Saved" only then
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	// Nixie 1.2.0 (admin): ChangePassword and ResetPassword say whether the server took it (the page shows it)
	public async Task<bool> ChangePassword(UserDto user, string currentPassword, string newPassword)
	{
		if (user.Id is not { } id)
		{
			return false;
		}

		try
		{
			await _jellyfinApiClient.Users.Password.PostAsync(new UpdateUserPassword
			{
				CurrentPassword = currentPassword,
				NewPw = newPassword,
			}, x => x.QueryParameters.UserId = id);
			return true;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	public async Task<bool> ResetPassword(UserDto user)
	{
		if (user.Id is not { } id)
		{
			return false;
		}

		try
		{
			await _jellyfinApiClient.Users.Password.PostAsync(new UpdateUserPassword
			{
				ResetPassword = true,
			}, x => x.QueryParameters.UserId = id);
			return true;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	public async Task<bool> Authenticate(string code) => await ApproveQuickConnect(code) == QuickConnectApproval.Approved;

	/// <summary>Whether Quick Connect is on at the server (GET /QuickConnect/Enabled); null when it did not say.</summary>
	private async Task<bool?> IsQuickConnectEnabled()
	{
		try
		{
			return await _jellyfinApiClient.QuickConnect.Enabled.GetAsync();
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// Seaglass 1.1.4 (review): approves another device's Quick Connect code, and how it went: a code the server has no request
	/// for (404: mistyped, expired), Quick Connect turned off on the server (403: it read as "That code didn't work"), or no
	/// answer at all.
	/// </summary>
	public async Task<QuickConnectApproval> ApproveQuickConnect(string code)
	{
		try
		{
			var approved = await _jellyfinApiClient.QuickConnect.Authorize.PostAsync(x =>
			{
				var query = x.QueryParameters;
				query.UserId = UserId;
				query.Code = code;
			}) ?? false;
			return approved ? QuickConnectApproval.Approved : QuickConnectApproval.NoSuchCode;
		}
		catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
		{
			// Seaglass 1.1.4: a code that is not waiting (mistyped, expired) is the user's to fix, said in the dialog
			logger.LogInformation("Quick Connect: the server has no request waiting with that code");
			return QuickConnectApproval.NoSuchCode;
		}
		catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is 401 or 403)
		{
			// review: a server with Quick Connect off answers 401 (not only 403): ask it whether Quick Connect is on
			var enabled = await IsQuickConnectEnabled();
			logger.LogInformation("Quick Connect: the server refused the code ({Status}); Quick Connect on: {Enabled}", ex.ResponseStatusCode, enabled?.ToString() ?? "unknown");
			return enabled == true ? QuickConnectApproval.Failed : QuickConnectApproval.Disabled;
		}
		catch (Exception ex)
		{
			logger.LogWarning("Quick Connect: approving the code failed: {Error}", ex.Message);
			return QuickConnectApproval.Failed;
		}
	}
}
