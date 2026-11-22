// Nixie 1.6: the collections stream's side of the colpage stream's seams (wip/150-colpage, e6d55cd: its
// FluentFin.Core.ViewModels.LibraryPresets.cs defines ILibraryPresetSource, ISavedFilters, LibraryPreset and
// LibrarySavedFilter). 1.6 integration: on with the merge of the two branches (the #if NIXIE_COLPAGE_SEAMS removed here,
// in App.xaml.cs, the registrations, and in CollectionViewModel.Nixie.cs, the header hook).
// The logic is NixieLibraryPresets' (tested on this branch); this is the mapping, nothing more.
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// The library page's presets strip (colpage's <see cref="ILibraryPresetSource"/>): a library's Nixie collections as presets
/// after the four built in; and its Save as collection (<see cref="ISavedFilters"/>): the filter kept in
/// <see cref="SmartCollectionStore"/>, a Nixie preset that was on with it.
/// </summary>
public sealed class NixieLibraryPresetSource(NixieLibraryPresets presets) : ILibraryPresetSource, ISavedFilters
{
	public async Task<IReadOnlyList<LibraryPreset>> PresetsForAsync(BaseItemDto library, IReadOnlyList<BaseItemDto> titles, CancellationToken cancellationToken) =>
		[.. (await presets.PresetsForAsync(library, titles, cancellationToken)).Select(p => LibraryPreset.ForCollection(p.Id, p.Label, p.Items, p.AutomationName))];

	/// <summary>New presets were made (the index came in or was read again): a library shown asks again.</summary>
	public event EventHandler? Changed
	{
		add => presets.Changed += value;
		remove => presets.Changed -= value;
	}

	public Task<bool> SaveFilter(string name, LibrarySavedFilter filter) =>
		presets.SaveFilter(name, new SavedFilter(filter.LibraryId, CollectionNames.DisplayLibrary(filter.LibraryName), filter.Unwatched, filter.Favorites,
			filter.Genres, filter.Tags, filter.Ratings, filter.Years, filter.PresetId,
			filter.PresetId is { } id ? presets.LabelOf(id) : null));
}
