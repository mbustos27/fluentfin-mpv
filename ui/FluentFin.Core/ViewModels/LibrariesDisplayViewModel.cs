using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

public partial class LibrariesDisplayViewModel(IJellyfinClient jellyfinClient) : ServerConfigurationViewModel(jellyfinClient), INavigationAware
{
	private Metadata? _metadata;

	/// <summary>1.6.1 (BH-151-m7): the date-added choice as the server had it when the page was read or last saved.</summary>
	private bool _dateAddedAsRead;

	public List<string> DateAddedBehaviors { get; set; } = ["User file creation date", "Use date scanned into the library"];

	protected override List<JellyfinConfigItemViewModel> CreateItems(ServerConfiguration config)
	{

		List<JellyfinConfigItemViewModel> items =
		[
			new JellyfinConfigItemViewModel<bool>(() => config.EnableFolderView ?? false, value => config.EnableFolderView = value)
			{
				DisplayName = "Display a folder view to show plain media folders",
				Description = "Display folders alongside your other media libraries. This can be useful if you'd like to have a plain folder view."
			},
			new JellyfinConfigItemViewModel<bool>(() => config.DisplaySpecialsWithinSeasons ?? false, value => config.DisplaySpecialsWithinSeasons = value)
			{
				DisplayName = "Display specials within seasons they aired in",
			},
			new JellyfinConfigItemViewModel<bool>(() => config.EnableExternalContentInSuggestions ?? false, value => config.EnableExternalContentInSuggestions = value)
			{
				DisplayName = "Enable external content in suggestions",
				Description = "Allow internet trailers and live TV programs to be included within suggested content."
			}
		];

		if (_metadata is not null)
		{
			var item = new JellyfinSelectableConfigItemViewModel(() => _metadata.UseFileCreationTimeForDateAdded ? DateAddedBehaviors[0] : DateAddedBehaviors[1],
																 value => _metadata.UseFileCreationTimeForDateAdded = value?.Equals(DateAddedBehaviors[0]) == true,
																 DateAddedBehaviors)
			{
				DisplayName = "Date added behavior for new content",
				Description = "If a metadata value is present, it will always be used before either of these options."
			};

			items.Insert(0, item);
		}

		return items;
	}

	/// <summary>
	/// 1.5.1 fixes: the date-added choice is the metadata configuration's: saved with the page (it never was). 1.6.1 (BH-151-m7):
	/// only when the choice changed, and into the configuration as the server has it now (read again just before, every key
	/// kept): Jellyfin replaces the whole configuration with what it is sent, and a Save of another setting on this page posted
	/// a one-key copy over it every time.
	/// </summary>
	protected override async Task<bool> SaveMoreAsync()
	{
		if (_metadata is null || _metadata.UseFileCreationTimeForDateAdded == _dateAddedAsRead)
		{
			return true;
		}

		try
		{
			var now = await _jellyfinClient.GetMetadata();
			now.UseFileCreationTimeForDateAdded = _metadata.UseFileCreationTimeForDateAdded;
			await _jellyfinClient.SaveMetadata(now);
			_dateAddedAsRead = now.UseFileCreationTimeForDateAdded;
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	async Task INavigationAware.OnNavigatedTo(object parameter)
	{
		_metadata = await _jellyfinClient.GetMetadata();
		_dateAddedAsRead = _metadata?.UseFileCreationTimeForDateAdded ?? false;

		await OnNavigatedTo(parameter);
	}
}
