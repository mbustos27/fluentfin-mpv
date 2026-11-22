using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// 1.6 colpage (LB2, LB3, LB5): the library's presets strip, one model for every theme: All · Unwatched · Favorites · Recently
/// added, then the library's Nixie collections (<see cref="PresetSource"/>, the collections stream's). Unwatched and Favorites
/// are the filters' twins (lit whenever on, a click turns them on or off and keeps the rest), Recently added is the sort (a
/// click again puts the sort before it back), All takes every filter off, and a Nixie collection narrows the library to its
/// titles (one at a time; its chip in the applied-filter row takes it off too). Filter's count leaves them out (LB3).
/// </summary>
public partial class LibraryViewModel
{
	/// <summary>The library's Nixie collections as presets (the collections stream registers one); null: the four built in.</summary>
	public ILibraryPresetSource? PresetSource { get; set; }

	/// <summary>Where Save as collection saves (the collections stream's SmartCollectionStore); null: no Save as collection.</summary>
	public ISavedFilters? SavedFilters { get; set; }

	private IReadOnlyList<LibraryPreset> _presets = LibraryPreset.Builtin;

	// the sort before Recently added put its own: a click again (or All) puts it back
	private (ItemSortBy SortBy, SortOrder Order)? _sortBeforeRecent;

	// 1.6.1 (colpage queue): a playlist was in its own order before Recently added: that order comes back, not titles A to Z
	private bool _playlistOrderBeforeRecent;

	/// <summary>The strip: each preset, lit or not, with its Narrator name.</summary>
	[CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
	public partial IReadOnlyList<LibraryPresetState> Presets { get; private set; } = [];

	/// <summary>Sorted by date added, newest first (Recently added's sort), and not in a playlist's own order.</summary>
	public bool IsRecentSort => SortBy == ItemSortBy.DateCreated && Order == SortOrder.Descending && !InPlaylistOrder;

	/// <summary>Save as collection shows: somewhere to save, and a filter on to save.</summary>
	public bool CanSaveFilter => SavedFilters is not null && ActiveFilters > 0;

	/// <summary>A click (or Enter, Space) on a preset.</summary>
	public void TogglePreset(LibraryPreset? preset)
	{
		if (preset is null)
		{
			return;
		}

		switch (preset.Kind)
		{
			case LibraryPresetKind.All:
				ClearFilters();
				if (IsRecentSort)
				{
					RestoreSortBeforeRecent();
				}

				break;
			case LibraryPresetKind.Unwatched:
				Filter.Unwatched = !Filter.Unwatched;
				break;
			case LibraryPresetKind.Favorites:
				Filter.Favorites = !Filter.Favorites;
				break;
			case LibraryPresetKind.RecentlyAdded:
				if (IsRecentSort)
				{
					RestoreSortBeforeRecent();
				}
				else
				{
					_playlistOrderBeforeRecent = InPlaylistOrder;
					_sortBeforeRecent = InPlaylistOrder ? null : (SortBy, Order);
					SetSort(ItemSortBy.DateCreated, SortOrder.Descending);
				}

				break;
			case LibraryPresetKind.Collection when preset.Filter.Items is { } items:
				var on = Filter.Preset?.Id == items.Id;
				Filter.Preset = on ? null : items;
				if (!on && preset.Filter.Unwatched)
				{
					Filter.Unwatched = true;
				}

				break;
		}

		UpdatePresets();
	}

	private void RestoreSortBeforeRecent()
	{
		if (_playlistOrderBeforeRecent && _playlistPlaces.Count > 0)
		{
			_playlistOrderBeforeRecent = false;
			_sortBeforeRecent = null;
			InPlaylistOrder = true; // the playlist's own order again (its comparer follows InPlaylistOrder)
			return;
		}

		_playlistOrderBeforeRecent = false;
		var (sortBy, order) = _sortBeforeRecent ?? (ItemSortBy.Name, SortOrder.Ascending);
		_sortBeforeRecent = null;
		// never back to Recently added itself (it was picked from the Sort menu first): titles A to Z
		if (sortBy == ItemSortBy.DateCreated && order == SortOrder.Descending)
		{
			(sortBy, order) = (ItemSortBy.Name, SortOrder.Ascending);
		}

		SetSort(sortBy, order);
	}

	/// <summary>The strip as the filters and the sort are now (a new list only when something changed).</summary>
	private void UpdatePresets()
	{
		var recent = IsRecentSort;
		var states = _presets.Select(p => new LibraryPresetState(p, LibraryPresets.IsOn(p, Filter, recent), LibraryPresets.NameOf(p, _all.Count, UnwatchedCount))).ToList();
		if (!states.SequenceEqual(Presets))
		{
			Presets = states;
		}

		OnPropertyChanged(nameof(CanSaveFilter));
	}

	// review Major 4: the source answers late, and says when it has more (its Changed, held while the library is shown)
	private ILibraryPresetSource? _listening;
	private SynchronizationContext? _presetsContext;
	private CancellationTokenSource? _presetsAsk;
	private int _presetsAsked;

	/// <summary>The last ask of the source (tests wait for it); done at once when there is none.</summary>
	public Task PresetsLoaded { get; private set; } = Task.CompletedTask;

	/// <summary>Every title is in: the library's Nixie collections join the strip, when the source has them (it may answer late).</summary>
	private void LoadNixiePresets()
	{
		Listen(PresetSource);
		PresetsLoaded = AskNixiePresetsAsync();
	}

	private async Task AskNixiePresetsAsync()
	{
		var asked = ++_presetsAsked;
		_presetsAsk?.Cancel();
		var cancel = _presetsAsk = new CancellationTokenSource();
		IReadOnlyList<LibraryPreset> nixie = [];
		if (PresetSource is { } source && _library is { } library)
		{
			try
			{
				nixie = await source.PresetsForAsync(library, [.. _all.Select(x => x.Dto)], cancel.Token) ?? [];
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				_logger?.LogPresetsFailed(ex);
			}
		}

		if (asked != _presetsAsked || cancel.IsCancellationRequested)
		{
			return; // a later ask (or leaving the library) took over
		}

		_presets = [.. LibraryPreset.Builtin, .. nixie.Where(p => p.Kind == LibraryPresetKind.Collection)];
		if (Filter.Preset is { } picked && !_presets.Any(p => p.Filter.Items?.Id == picked.Id))
		{
			Filter.Preset = null;
		}

		UpdatePresets();
	}

	/// <summary>Holds the source's Changed while the library is shown (one source at a time; null lets go).</summary>
	private void Listen(ILibraryPresetSource? source)
	{
		if (ReferenceEquals(_listening, source))
		{
			return;
		}

		if (_listening is not null)
		{
			_listening.Changed -= OnPresetSourceChanged;
		}

		_listening = source;
		if (source is not null)
		{
			_presetsContext = SynchronizationContext.Current;
			source.Changed += OnPresetSourceChanged;
		}
	}

	/// <summary>The source has more (its index came in): asked again, on the thread the library loaded on.</summary>
	private void OnPresetSourceChanged(object? sender, EventArgs e)
	{
		if (_presetsContext is { } context && context != SynchronizationContext.Current)
		{
			context.Post(_ => Ask(), null);
		}
		else
		{
			Ask();
		}

		void Ask()
		{
			if (_listening is not null)
			{
				PresetsLoaded = AskNixiePresetsAsync();
			}
		}
	}

	/// <summary>The library was left: no more asks, and the source's Changed let go.</summary>
	private void StopPresets()
	{
		_presetsAsk?.Cancel();
		Listen(null);
	}

	/// <summary>Save as collection: the filters on, under <paramref name="name"/>; false when it could not be saved.</summary>
	public async Task<bool> SaveFilterAs(string? name)
	{
		// 1.6.1 (colpage queue): one save at a time. The flyout's own guard was reset each time it opened, so closing it mid-save
		// and saving again made two collections
		if (IsSavingFilter || SavedFilters is not { } saved || !LibraryPresets.IsValidName(name) || _library is not { Id: { } id } library)
		{
			return false;
		}

		var filter = new LibrarySavedFilter(id, library.Name ?? "", Filter.Unwatched, Filter.Favorites, [.. Filter.Genres], [.. Filter.Years],
			[.. Filter.OfficialRatings], [.. Filter.Tags], Filter.Preset?.Id, SortBy, Order);
		var saving = SaveFilterAsync(saved, name!.Trim(), filter);
		_savingFilter = saving;
		return await saving;
	}

	private Task<bool>? _savingFilter;

	/// <summary>1.6.1: a Save as collection is on its way (a second one saves nothing until it is done).</summary>
	public bool IsSavingFilter => _savingFilter is { IsCompleted: false };

	private async Task<bool> SaveFilterAsync(ISavedFilters saved, string name, LibrarySavedFilter filter)
	{
		try
		{
			return await saved.SaveFilter(name, filter);
		}
		catch (Exception ex)
		{
			_logger?.LogPresetsFailed(ex);
			return false;
		}
	}
}

internal static partial class LibraryPresetLog
{
	[Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "The library's presets or Save as collection failed")]
	public static partial void LogPresetsFailed(this Microsoft.Extensions.Logging.ILogger logger, Exception ex);
}
