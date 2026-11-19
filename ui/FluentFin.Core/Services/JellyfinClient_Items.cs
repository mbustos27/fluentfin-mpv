using Flurl.Http;
using Jellyfin.Sdk.Generated.Library.VirtualFolders;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

public partial class JellyfinClient
{
	public async Task<BaseItemDtoQueryResult?> GetItems(BaseItemDto parent, bool recursive = false)
	{
		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.SortBy = [ItemSortBy.SortName];
				query.SortOrder = [SortOrder.Ascending];
				// Nixie 1.3.1: SortName too, which the library sorts and files its A-Z by
				query.Fields = [ItemFields.PrimaryImageAspectRatio, ItemFields.DateCreated, ItemFields.Overview, ItemFields.Tags, ItemFields.Genres, ItemFields.SortName];
				query.ImageTypeLimit = 1;
				query.EnableImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Thumb];
				query.ParentId = parent.Id;
				query.IncludeItemTypes = parent.CollectionType switch
				{
					BaseItemDto_CollectionType.Movies => [BaseItemKind.Movie],
					BaseItemDto_CollectionType.Tvshows => [BaseItemKind.Series],
					_ => null
				};
				// A movie or show can sit inside a plain folder, for example a folder holding
				// several unrelated videos. List these libraries recursively, like Jellyfin Web,
				// so those items show up instead of being hidden inside the folder.
				query.Recursive = recursive || query.IncludeItemTypes is not null;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<BaseItemDto?> GetItem(Guid id)
	{
		try
		{
			return await _jellyfinApiClient.Items[id].GetAsync(x => x.QueryParameters.UserId = UserId);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	// Nixie 1.4.0: the item's folders, for the top bar's eyebrow (the library an item is in)
	public async Task<IReadOnlyList<BaseItemDto>> GetAncestors(Guid id)
	{
		try
		{
			return await _jellyfinApiClient.Items[id].Ancestors.GetAsync(x => x.QueryParameters.UserId = UserId) ?? [];
		}
		catch (Exception ex)
		{
			logger.LogDebug("Ancestors of {Id}: {Error}", id, ex.Message);
			return [];
		}
	}

	// Seaglass 1.1.4: was a POST of the whole item to /Items/{id} (the administrators' metadata editor, which never reads the
	// user data: nothing was reset, and anyone else got a 403); now the user-data endpoint, as SetResumePosition
	public async Task ResetProgress(Guid id) => await SetResumePosition(id, 0);

	public async Task<bool> SetResumePosition(Guid id, long positionTicks)
	{
		try
		{
			await _jellyfinApiClient.UserItems[id].UserData.PostAsync(new UpdateUserItemDataDto { PlaybackPositionTicks = Math.Max(0, positionTicks) },
				x => x.QueryParameters.UserId = UserId);
			return true;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	public async Task<BaseItemDtoQueryResult?> GetSimilarItems(BaseItemDto dto)
	{
		if (dto.Id is not { } id)
		{
			return null;
		}

		try
		{
			return await _jellyfinApiClient.Items[id].Similar.GetAsync(x =>
			{
				x.QueryParameters.UserId = UserId;
				x.QueryParameters.Limit = 12;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	// Nixie 1.3.1: a playlist opens in the library page, in its own order (no sort asked of the server)
	public async Task<BaseItemDtoQueryResult?> GetPlaylistItems(Guid playlistId)
	{
		try
		{
			return await _jellyfinApiClient.Playlists[playlistId].Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.UserId = UserId;
				query.Fields = [ItemFields.PrimaryImageAspectRatio, ItemFields.DateCreated, ItemFields.Overview, ItemFields.Tags, ItemFields.Genres, ItemFields.SortName];
				query.ImageTypeLimit = 1;
				query.EnableImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Thumb];
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	/// <summary>
	/// Nixie 1.4.0 (the UX review's plan item 1): the collections a film is part of (GET /Items/{id}/Collections, Jellyfin
	/// 10.11's "Included in"), for the movie page's "Part of". An older server has no such request (404): none, quietly.
	/// </summary>
	public async Task<IReadOnlyList<BaseItemDto>> GetItemCollections(Guid itemId)
	{
		try
		{
			var result = await _jellyfinApiClient.Items[itemId].Collections.GetAsync(x => x.QueryParameters.UserId = UserId);
			return result?.Items is { } items ? [.. items.Where(x => x.Id is not null)] : [];
		}
		catch (Exception ex)
		{
			logger.LogDebug("The collections of {ItemId} could not be read (an older server has no such request): {Error}", itemId, ex.Message);
			return [];
		}
	}

	/// <summary>Nixie 1.4.0: a library's titles and its unwatched ones, two counting requests (no items) asked together.</summary>
	public async Task<FluentFin.Core.ViewModels.LibraryCounts?> GetLibraryCounts(BaseItemDto library)
	{
		if (library.Id is not { } id || FluentFin.Core.ViewModels.LibraryCounts.KindOf(library.CollectionType) is not { } kind)
		{
			return null;
		}

		Task<BaseItemDtoQueryResult?> Count(bool unwatched) => _jellyfinApiClient.Items.GetAsync(x =>
		{
			var query = x.QueryParameters;
			query.UserId = UserId;
			query.ParentId = id;
			query.Recursive = true;
			query.IncludeItemTypes = [kind];
			query.Limit = 0;
			query.EnableTotalRecordCount = true;
			query.EnableImages = false;
			query.EnableUserData = false;
			if (unwatched)
			{
				query.IsPlayed = false;
			}
		});

		try
		{
			var watchable = FluentFin.Core.ViewModels.LibraryCounts.HasWatchedState(kind);
			var all = Count(unwatched: false);
			var unseen = watchable ? Count(unwatched: true) : Task.FromResult<BaseItemDtoQueryResult?>(null);
			await Task.WhenAll(all, unseen);
			return (await all)?.TotalRecordCount is { } total
				? new FluentFin.Core.ViewModels.LibraryCounts(total, watchable ? (await unseen)?.TotalRecordCount : null)
				: null;
		}
		catch (Exception ex)
		{
			logger.LogDebug("The counts of library {Library} could not be read: {Error}", id, ex.Message);
			return null;
		}
	}

	// Nixie 1.3.1: the Trailer button plays a local trailer in the mpv player (Trailers.Choose)
	public async Task<List<BaseItemDto>> GetLocalTrailers(Guid id)
	{
		try
		{
			return await _jellyfinApiClient.Items[id].LocalTrailers.GetAsync(x => x.QueryParameters.UserId = UserId) ?? [];
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return [];
		}
	}

	public async Task<BaseItemDtoQueryResult?> GetMediaFolders()
	{
		try
		{
			return await _jellyfinApiClient.Library.MediaFolders.GetAsync(x => x.QueryParameters.IsHidden = false);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<UserDto?> CreateUser(string username, string password)
	{
		try
		{
			return await _jellyfinApiClient.Users.New.PostAsync(new CreateUserByName
			{
				Name = username,
				Password = password
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<List<VirtualFolderInfo>> GetVirtualFolders()
	{
		try
		{
			return await _jellyfinApiClient.Library.VirtualFolders.GetAsync() ?? [];
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return [];
		}
	}

	public async Task<bool> CreateLibrary(string name, CollectionTypeOptions collectionType, LibraryOptions options)
	{
		try
		{
			await _jellyfinApiClient.Library.VirtualFolders.PostAsync(new AddVirtualFolderDto
			{
				LibraryOptions = options,
			}, x =>
			{
				var query = x.QueryParameters;
				query.RefreshLibrary = true;
				query.Name = name;
				query.CollectionType = collectionType;
			});
			return true; // Nixie 1.2.0 (admin): the caller says whether it was created
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
		finally
		{
			ForgetUserViews(); // fluentfin-mpv: the views changed (GetUserViews)
		}
	}

	public async Task<bool> DeleteLibrary(string name)
	{
		try
		{
			await _jellyfinApiClient.Library.VirtualFolders.DeleteAsync(x =>
			{
				var query = x.QueryParameters;
				query.RefreshLibrary = true;
				query.Name = name;
			});
			return true; // Nixie 1.2.0 (admin): the list drops the library only when the server did
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
		finally
		{
			ForgetUserViews(); // fluentfin-mpv: the views changed (GetUserViews)
		}
	}

	public async Task RenameLibrary(string name, string newName)
	{
		try
		{
			await _jellyfinApiClient.Library.VirtualFolders.Name.PostAsync(x =>
			{
				var query = x.QueryParameters;
				query.RefreshLibrary = true;
				query.Name = name;
				query.NewName = newName;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return;
		}
		finally
		{
			ForgetUserViews(); // fluentfin-mpv: the views changed (GetUserViews)
		}
	}

	public async Task DeleteItem(BaseItemDto dto)
	{
		if (dto?.Id is not { } id)
		{
			return;
		}

		try
		{
			await _jellyfinApiClient.Items[id].DeleteAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return;
		}
	}

	public async Task<Uri?> GetSplashScreen()
	{
		var endoint = _jellyfinApiClient.System.Configuration["branding"].ToGetRequestInformation();
		var options =  await AddApiKey(endoint.URI).GetJsonAsync<BrandingOptionsDto>();

		if(options.SplashscreenEnabled is not true)
		{
			return null;
		}

		return AddApiKey(_jellyfinApiClient.Branding.Splashscreen.ToGetRequestInformation(x =>
		{
			var query = x.QueryParameters;
			query.Format = Jellyfin.Sdk.Generated.Branding.Splashscreen.ImageFormat.Jpg;
		}).URI);
	}
}
