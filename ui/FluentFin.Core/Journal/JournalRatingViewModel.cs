using CommunityToolkit.Mvvm.ComponentModel;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Journal;

/// <summary>
/// "Your rating" on the episode, season and series pages: half-stars stored 1-10 in the journal of whoever is signed in
/// (sent to Jellyfin as well only with that setting on). Hidden for anything else (movies come later) and when nobody is
/// signed in or the journal is off.
/// </summary>
public sealed partial class JournalRatingViewModel(JournalService journal, ILogger<JournalRatingViewModel> logger) : ObservableObject
{
	private RatedItem? _item;
	private int _loads;
	private bool _loading;

	[ObservableProperty]
	public partial int Value { get; set; }

	[ObservableProperty]
	public partial bool IsAvailable { get; set; }

	public RatedItem? Item => _item;

	/// <summary>The save the latest change started (the control does not wait for it; the tests do).</summary>
	public Task LastSave { get; private set; } = Task.CompletedTask;

	/// <summary>Null for items the journal does not rate.</summary>
	public static RatedItem? From(BaseItemDto? dto) => dto switch
	{
		{ Id: { } id, Type: BaseItemDto_Type.Episode } => new RatedItem(id, RatedItemKind.Episode, dto.SeriesId, dto.Name),
		{ Id: { } id, Type: BaseItemDto_Type.Season } => new RatedItem(id, RatedItemKind.Season, dto.SeriesId, dto.Name),
		{ Id: { } id, Type: BaseItemDto_Type.Series } => new RatedItem(id, RatedItemKind.Series, id, dto.Name),
		_ => null,
	};

	/// <summary>Shows the rating of this item (a newer call wins over one still reading).</summary>
	public async Task LoadAsync(BaseItemDto? dto)
	{
		var load = ++_loads;
		var item = journal.CurrentProfile is null ? null : From(dto);
		var value = 0;
		if (item is not null)
		{
			try
			{
				value = await Task.Run(() => journal.GetRating(item.Id));
			}
			catch (JournalUnavailableException ex)
			{
				logger.LogDebug("Journal: no rating shown: {Message}", ex.Message);
				item = null;
			}
		}

		if (load != _loads)
		{
			return;
		}

		_item = item;
		_loading = true;
		Value = value;
		_loading = false;
		IsAvailable = item is not null;
	}

	partial void OnValueChanged(int value)
	{
		if (!_loading && _item is { } item)
		{
			LastSave = SaveAsync(item, value);
		}
	}

	private async Task SaveAsync(RatedItem item, int value)
	{
		try
		{
			// the write (and a mirror to Jellyfin) never on the UI thread a click came from
			await Task.Run(() => journal.SetRatingAsync(item, value));
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Journal: the rating of {ItemId} could not be saved", item.Id);
		}
	}
}
