using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Dialogs.ViewModels;

/// <summary>
/// The Edit metadata dialog. Nixie 1.2.0 (admin, button review D56): "Revert" puts back the values as they were loaded (it
/// used to empty every field, so a Save afterwards wiped the item), and Save sends only what changed. The server's
/// <c>POST /Items/{id}</c> replaces every field it reads with the one sent, so "only what changed" means: the item exactly as
/// the server gave it, with the changed fields replaced; nothing at all when nothing changed.
/// </summary>
public partial class EditMetadataViewModel : ObservableObject, IHandleClose, IBaseItemDialogViewModel
{
	private readonly IJellyfinClient _jellyfinClient;
	private MetadataForm? _loaded;
	private bool _applying;

	public EditMetadataViewModel(IJellyfinClient jellyfinClient)
	{
		_jellyfinClient = jellyfinClient;
		Genres.CollectionChanged += OnGenresChanged;
	}

	[ObservableProperty]
	public partial string? Path { get; set; }

	[ObservableProperty]
	public partial string? Title { get; set; }

	[ObservableProperty]
	public partial string? OriginalTitle { get; set; }

	[ObservableProperty]
	public partial string? SortTitle { get; set; }

	[ObservableProperty]
	public partial DateTimeOffset? DateAdded { get; set; }

	[ObservableProperty]
	public partial string? Status { get; set; }

	[ObservableProperty]
	public partial double? CommunityRating { get; set; }

	[ObservableProperty]
	public partial string? Overview { get; set; }

	[ObservableProperty]
	public partial DateTimeOffset? ReleaseDate { get; set; }

	[ObservableProperty]
	public partial double? Year { get; set; }

	[ObservableProperty]
	public partial DateTimeOffset? EndDate { get; set; }

	[ObservableProperty]
	public partial string? ParentalRating { get; set; }

	[ObservableProperty]
	public partial string? CustomRating { get; set; }

	[ObservableProperty]
	public partial string? OriginalAspectRatio { get; set; }

	[ObservableProperty]
	public partial BaseItemDto_Video3DFormat? Video3DFormat { get; set; }

	[ObservableProperty]
	public partial List<string?> RatingValues { get; set; } = [];

	[ObservableProperty]
	public partial List<BaseItemDto_Video3DFormat?> Video3DFormatValues { get; set; } = [];

	[ObservableProperty]
	public partial List<KeyValueViewModel> ExternalIds { get; set; } = [];

	/// <summary>Something differs from the values as loaded: Save and Revert do something only then.</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RevertCommand))]
	public partial bool HasChanges { get; set; }

	/// <summary>Why the last Save did not go through; empty otherwise.</summary>
	[ObservableProperty]
	public partial string Error { get; set; } = "";

	public ObservableCollection<string> Genres { get; } = [];

	/// <summary>The server's series statuses (SeriesStatus), and the item's own if it is another one, so the box can show it.</summary>
	[ObservableProperty]
	public partial List<string?> StatusValues { get; set; } = [null, "Continuing", "Ended", "Unreleased"];

	public MetadataEditorInfo? MetadataEditorInfo { get; set; }

	public bool CanClose { get; set; }

	public BaseItemDto? Item { get; set; }

	public async Task Initialize(BaseItemDto dto)
	{
		if (dto.Id is not { } id)
		{
			return;
		}

		Item = await _jellyfinClient.GetItem(id);

		if (Item is null)
		{
			return;
		}

		MetadataEditorInfo = await _jellyfinClient.GetMetadataEditorInfo(dto);
		// the item's own values are always in the lists: a ComboBox whose item is not in its list shows nothing and writes
		// null back, which a Save would have sent
		RatingValues = [.. new List<string?> { null }
			.Concat(MetadataEditorInfo?.ParentalRatingOptions?.Select(x => x.Name) ?? [])
			.Append(Item.OfficialRating)
			.Append(Item.CustomRating)
			.Where((x, i) => i == 0 || !string.IsNullOrEmpty(x))
			.Distinct()];
		if (!string.IsNullOrEmpty(Item.Status) && !StatusValues.Contains(Item.Status))
		{
			StatusValues = [.. StatusValues, Item.Status];
		}

		Video3DFormatValues = [null, .. Enum.GetValues<BaseItemDto_Video3DFormat>()];

		if (MetadataEditorInfo is { ExternalIdInfos: not null })
		{
			ExternalIds = MetadataEditorInfo.ExternalIdInfos
				.Where(x => !string.IsNullOrEmpty(x.Name) || !string.IsNullOrEmpty(x.Key))
				.Select(x => new KeyValueViewModel(x.Name!, x.Key!, x.Type) { Value = GetValue(Item.ProviderIds, x) }).ToList();
		}

		foreach (var externalId in ExternalIds)
		{
			externalId.PropertyChanged += (_, _) => UpdateHasChanges();
		}

		Path = Item.Path;
		_loaded = new MetadataForm(
			Title: Item.Name,
			OriginalTitle: Item.OriginalTitle,
			SortTitle: Item.ForcedSortName ?? Item.SortName,
			DateAdded: Item.DateCreated,
			Status: Item.Status,
			CommunityRating: Item.CommunityRating,
			Overview: Item.Overview,
			ReleaseDate: Item.PremiereDate ?? Item.StartDate,
			Year: Item.ProductionYear,
			EndDate: Item.EndDate,
			ParentalRating: Item.OfficialRating,
			CustomRating: Item.CustomRating,
			OriginalAspectRatio: Item.AspectRatio,
			Video3DFormat: Item.Video3DFormat,
			Genres: [.. Item.Genres ?? []],
			ExternalIds: ExternalIds.ToDictionary(x => x.Key, x => x.Value));
		Apply(_loaded);
	}

	[RelayCommand]
	public void DeleteGenre(string genre)
	{
		Genres.Remove(genre);
	}

	/// <summary>Adds a genre once; a blank one is not a genre.</summary>
	[RelayCommand]
	public void AddGenre(string? genre)
	{
		genre = genre?.Trim();
		if (string.IsNullOrEmpty(genre) || Genres.Contains(genre))
		{
			return;
		}

		Genres.Add(genre);
	}

	/// <summary>Puts back every field as it was loaded (D56: this used to empty them). Nothing goes to the server.</summary>
	[RelayCommand(CanExecute = nameof(HasChanges))]
	public void Revert()
	{
		if (_loaded is { } loaded)
		{
			Apply(loaded);
		}
	}

	/// <summary>
	/// Sends the changed fields (<see cref="BuildUpdate"/>); nothing when nothing changed. True when there was nothing to send
	/// or the server took it; false (and <see cref="Error"/>) when it did not, and the form keeps the edits.
	/// </summary>
	public async Task<bool> Save()
	{
		Error = "";
		if (BuildUpdate() is not { } update)
		{
			return true;
		}

		if (!await _jellyfinClient.UpdateMetadata(update))
		{
			Error = "The server didn't save the changes. Try again, or Cancel to leave the item as it was.";
			return false;
		}

		_loaded = Capture();
		UpdateHasChanges();
		return true;
	}

	/// <summary>
	/// The item to send: the one the server gave, with each field that differs from its loaded value replaced; null when none
	/// does. A blank text clears its field; dates compare by the calendar day the date picker shows.
	/// </summary>
	internal BaseItemDto? BuildUpdate()
	{
		if (Item is null || _loaded is not { } loaded)
		{
			return null;
		}

		var now = Capture();
		var changed = false;
		void Set(bool differs, Action apply)
		{
			if (differs)
			{
				apply();
				changed = true;
			}
		}

		Set(!SameText(now.Title, loaded.Title), () => Item.Name = Blank(now.Title));
		Set(!SameText(now.OriginalTitle, loaded.OriginalTitle), () => Item.OriginalTitle = Blank(now.OriginalTitle));
		// the server reads ForcedSortName (SortName is the one it works out)
		Set(!SameText(now.SortTitle, loaded.SortTitle), () => Item.ForcedSortName = Blank(now.SortTitle));
		Set(!SameDay(now.DateAdded, loaded.DateAdded), () => Item.DateCreated = now.DateAdded);
		Set(!SameText(now.Status, loaded.Status), () => Item.Status = Blank(now.Status));
		Set(!SameNumber(now.CommunityRating, loaded.CommunityRating), () => Item.CommunityRating = (float?)Number(now.CommunityRating));
		Set(!SameText(now.Overview, loaded.Overview), () => Item.Overview = Blank(now.Overview));
		// the release date used to be shown and never sent
		Set(!SameDay(now.ReleaseDate, loaded.ReleaseDate), () => Item.PremiereDate = now.ReleaseDate);
		Set(Whole(now.Year) != Whole(loaded.Year), () => Item.ProductionYear = Whole(now.Year));
		Set(!SameDay(now.EndDate, loaded.EndDate), () => Item.EndDate = now.EndDate);
		Set(!SameText(now.ParentalRating, loaded.ParentalRating), () => Item.OfficialRating = Blank(now.ParentalRating));
		Set(!SameText(now.CustomRating, loaded.CustomRating), () => Item.CustomRating = Blank(now.CustomRating));
		Set(!SameText(now.OriginalAspectRatio, loaded.OriginalAspectRatio), () => Item.AspectRatio = Blank(now.OriginalAspectRatio));
		Set(now.Video3DFormat != loaded.Video3DFormat, () => Item.Video3DFormat = now.Video3DFormat);
		Set(!now.Genres.SequenceEqual(loaded.Genres), () => Item.Genres = [.. now.Genres]);

		foreach (var (key, value) in now.ExternalIds)
		{
			loaded.ExternalIds.TryGetValue(key, out var before);
			Set(!SameText(value, before), () =>
			{
				Item.ProviderIds ??= new BaseItemDto_ProviderIds();
				if (Blank(value) is { } id)
				{
					Item.ProviderIds.AdditionalData[key] = id;
				}
				else
				{
					Item.ProviderIds.AdditionalData.Remove(key);
				}
			});
		}

		return changed ? Item : null;
	}

	private MetadataForm Capture() => new(Title, OriginalTitle, SortTitle, DateAdded, Status, CommunityRating, Overview, ReleaseDate, Year,
		EndDate, ParentalRating, CustomRating, OriginalAspectRatio, Video3DFormat, [.. Genres], ExternalIds.ToDictionary(x => x.Key, x => x.Value));

	private void Apply(MetadataForm form)
	{
		_applying = true;
		try
		{
			Title = form.Title;
			OriginalTitle = form.OriginalTitle;
			SortTitle = form.SortTitle;
			DateAdded = form.DateAdded;
			Status = form.Status;
			CommunityRating = form.CommunityRating;
			Overview = form.Overview;
			ReleaseDate = form.ReleaseDate;
			Year = form.Year;
			EndDate = form.EndDate;
			ParentalRating = form.ParentalRating;
			CustomRating = form.CustomRating;
			OriginalAspectRatio = form.OriginalAspectRatio;
			Video3DFormat = form.Video3DFormat;

			Genres.Clear();
			foreach (var genre in form.Genres)
			{
				Genres.Add(genre);
			}

			foreach (var externalId in ExternalIds)
			{
				externalId.Value = form.ExternalIds.TryGetValue(externalId.Key, out var value) ? value : null;
			}
		}
		finally
		{
			_applying = false;
		}

		UpdateHasChanges();
	}

	protected override void OnPropertyChanged(PropertyChangedEventArgs e)
	{
		base.OnPropertyChanged(e);
		if (e.PropertyName is nameof(HasChanges) or nameof(Error) or nameof(RatingValues) or nameof(Video3DFormatValues) or nameof(StatusValues) or nameof(Path))
		{
			return;
		}

		UpdateHasChanges();
	}

	private void OnGenresChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateHasChanges();

	private void UpdateHasChanges()
	{
		if (_applying)
		{
			return;
		}

		HasChanges = _loaded is { } loaded && !Capture().SameAs(loaded);
	}

	private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

	private static bool SameText(string? a, string? b) => string.Equals(Blank(a), Blank(b), StringComparison.Ordinal);

	/// <summary>The date picker shows (and sets) a calendar day in local time: the time of day it drops is not a change.</summary>
	private static bool SameDay(DateTimeOffset? a, DateTimeOffset? b) =>
		a is null ? b is null : b is not null && a.Value.ToLocalTime().Date == b.Value.ToLocalTime().Date;

	/// <summary>An empty number box holds NaN: no value.</summary>
	private static double? Number(double? value) => value is { } v && !double.IsNaN(v) ? v : null;

	private static bool SameNumber(double? a, double? b) => (float?)Number(a) == (float?)Number(b);

	private static int? Whole(double? value) => Number(value) is { } v ? (int)Math.Round(v) : null;

	private static string? GetValue(BaseItemDto_ProviderIds? ids, ExternalIdInfo info)
	{
		if (ids is null || info.Key is null)
		{
			return null;
		}

		return ids.AdditionalData.TryGetValue(info.Key, out var value) ? $"{value}" : null;
	}

	private sealed record MetadataForm(string? Title, string? OriginalTitle, string? SortTitle, DateTimeOffset? DateAdded, string? Status,
		double? CommunityRating, string? Overview, DateTimeOffset? ReleaseDate, double? Year, DateTimeOffset? EndDate, string? ParentalRating,
		string? CustomRating, string? OriginalAspectRatio, BaseItemDto_Video3DFormat? Video3DFormat, IReadOnlyList<string> Genres,
		IReadOnlyDictionary<string, string?> ExternalIds)
	{
		public bool SameAs(MetadataForm other) =>
			SameText(Title, other.Title) && SameText(OriginalTitle, other.OriginalTitle) && SameText(SortTitle, other.SortTitle) &&
			SameDay(DateAdded, other.DateAdded) && SameText(Status, other.Status) && SameNumber(CommunityRating, other.CommunityRating) &&
			SameText(Overview, other.Overview) && SameDay(ReleaseDate, other.ReleaseDate) && Whole(Year) == Whole(other.Year) &&
			SameDay(EndDate, other.EndDate) && SameText(ParentalRating, other.ParentalRating) && SameText(CustomRating, other.CustomRating) &&
			SameText(OriginalAspectRatio, other.OriginalAspectRatio) && Video3DFormat == other.Video3DFormat && Genres.SequenceEqual(other.Genres) &&
			ExternalIds.All(x => SameText(x.Value, other.ExternalIds.TryGetValue(x.Key, out var v) ? v : null));
	}
}
