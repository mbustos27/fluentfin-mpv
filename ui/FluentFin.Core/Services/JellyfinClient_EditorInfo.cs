
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

public partial class JellyfinClient
{
	public async Task<LibraryOptionsResultDto?> GetAvailableInfo(Jellyfin.Sdk.Generated.Libraries.AvailableOptions.CollectionType libraryContentType, bool isNewLibrary)
	{
		try
		{
			return await _jellyfinApiClient.Libraries.AvailableOptions.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.LibraryContentType = libraryContentType;
				query.IsNewLibrary = isNewLibrary;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Unhandled Exception");
			return null;
		}
	}

	public async Task<bool> SaveLibraryOptions(Guid folderId, LibraryOptions options)
	{
		try
		{
			await _jellyfinApiClient.Library.VirtualFolders.LibraryOptions.PostAsync(new UpdateLibraryOptionsDto
			{
				Id = folderId,
				LibraryOptions = options
			});
			return true; // Nixie 1.2.0 (admin): the dialog closes only when the server took it
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Unhandled Exception");
			return false;
		}
	}
}
