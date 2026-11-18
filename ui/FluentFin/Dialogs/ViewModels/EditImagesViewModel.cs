using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using FluentFin.Core;
using FluentFin.Core.Admin;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using ReactiveUI;

namespace FluentFin.Dialogs.ViewModels;

/// <remarks>
/// Nixie 1.2.0 (admin, button review D50 / D53): Delete image deleted on the server at once; it now asks first (<see cref="Confirm"/>,
/// a flyout at the button: Cancel has the focus) and the tile goes only when the server deleted it. Back returns to the image
/// list (it searched again and stayed in the results).
/// </remarks>
public partial class EditImagesViewModel(IJellyfinClient jellyfinClient) : ObservableObject, IHandleClose, IBaseItemDialogViewModel
{
	/// <summary>Asks before a destructive action; true only when the user confirmed. Unset (nobody to ask), nothing happens.</summary>
	public Func<ConfirmPrompt, Task<bool>>? Confirm { get; set; }

	/// <summary>Why the last change did not go through; empty otherwise.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; set; } = "";

	/// <summary>The error bar opens from this bool (no x:Bind function on IsOpen).</summary>
	public bool HasError => !string.IsNullOrEmpty(Error);

	[ObservableProperty]
	public partial ObservableCollection<ImageInfoEx> Images { get; set; } = [];

	[ObservableProperty]
	public partial BaseItemDto? Item { get; set; }

	[ObservableProperty]
	public partial EditImagesViewModelState State { get; set; } = EditImagesViewModelState.Display;

	[ObservableProperty]
	public partial List<ImageProviderInfo> ImageProviders { get; set; } = [];

	[ObservableProperty]
	public partial ImageProviderInfo? SelectedProvider { get; set; }

	[ObservableProperty]
	public partial ImageType? SelectedImageType { get; set; }

	[ObservableProperty]
	public partial List<RemoteImageInfo> SearchResults { get; set; } = [];

	[ObservableProperty]
	public partial bool IncludeAllLanguages { get; set; }

	[ObservableProperty]
	public partial string BackButtonText { get; set; } = "";

	public ObservableCollection<ImageType?> ImageTypes { get; } = [];

	public bool CanClose { get; set; }

	public async Task Initialize(BaseItemDto item)
	{
		if (item is null)
		{
			return;
		}

		State = EditImagesViewModelState.Display;

		Item = item;

		Images = [.. await LoadImages(item)];


		var providers = await jellyfinClient.GetImageProviders(item);
		var supportedImages = GetAllSupportedImages(providers);
		ImageProviders = [new() { Name = "All", SupportedImages = supportedImages }, .. providers];
		SelectedProvider = ImageProviders.FirstOrDefault();
		SelectedImageType = supportedImages.FirstOrDefault();

		this.WhenAnyValue(x => x.SelectedProvider)
			.Where(x => x is { SupportedImages.Count: > 0 })
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(provider =>
			{
				var newTypes = provider!.SupportedImages!.Except(ImageTypes).ToList();
				var removeTypes = ImageTypes.Except(provider.SupportedImages!).ToList();
				var selectedImageType = SelectedImageType;

				foreach (var type in removeTypes)
				{
					ImageTypes.Remove(type);
				}

				ImageTypes.AddRange(newTypes);

				if (ImageTypes.Contains(selectedImageType) == false)
				{
					SelectedImageType = ImageTypes.FirstOrDefault();
				}
			});

		this.WhenAnyValue(x => x.SelectedProvider, x => x.SelectedImageType, x => x.IncludeAllLanguages)
			.Where(x => x is { Item1: not null, Item2: not null })
			.Where(_ => State == EditImagesViewModelState.Search)
			.Subscribe(async _ => await SearchImages());

		this.WhenAnyValue(x => x.State)
			.Subscribe(state => BackButtonText = state == EditImagesViewModelState.Search ? "Back" : "");
	}

	private async Task SearchImages()
	{
		if (Item is null || SelectedImageType is null || SelectedProvider is null)
		{
			return;
		}

		State = EditImagesViewModelState.Loading;

		var providerName = SelectedProvider.Name == "All" ? null : SelectedProvider.Name;
		var result = await jellyfinClient.SearchImages(Item, SelectedImageType.Value, providerName, IncludeAllLanguages);

		if (result is null or { Images: null })
		{
			return;
		}

		SearchResults = result.Images;

		State = EditImagesViewModelState.Search;
	}

	[RelayCommand]
	private async Task SwitchView(EditImagesViewModelState state)
	{
		State = state;
		// D50: only the search view searches (Back to the list searched again, and the search put the results back)
		if (state == EditImagesViewModelState.Search)
		{
			await SearchImages();
		}
	}

	[RelayCommand]
	private async Task Search(ImageInfo info)
	{
		if (info.ImageType is { })
		{
			SelectedImageType = (ImageType?)(int)info.ImageType;
		}

		await SearchImages();
	}

	[RelayCommand]
	private async Task UpdateImage(RemoteImageInfo info)
	{
		if (Item is null)
		{
			return;
		}
		await jellyfinClient.UpdateImage(Item, info);
		await Initialize(Item);
	}

	/// <summary>The item's images as the server has them now (their indexes are the server's current ones).</summary>
	private async Task<List<ImageInfoEx>> LoadImages(BaseItemDto item) => [.. (await jellyfinClient.GetImages(item)).Select(info => new ImageInfoEx
	{
		ImageIndex = info.ImageIndex,
		ImageTag = info.ImageTag,
		Size = info.Size,
		ImageType = info.ImageType,
		Height = info.Height,
		Width = info.Width,
		Uri = jellyfinClient.GetImage(item, info)
	})];

	/// <summary>
	/// Review fix: the server deletes by type and index, and renumbers the rest after a delete, so the tiles' indexes go stale
	/// (with backdrops A, B and C, deleting A then B sent Backdrop/1: C). The image is found again by its tag (what identifies
	/// it) in the server's list just before the delete, and the list is read again afterwards.
	/// </summary>
	[RelayCommand]
	private async Task DeleteImage(ImageInfoEx? info)
	{
		if (Item is null || info is null || Confirm is not { } confirm || !await confirm(AdminPrompts.DeleteImage(Item.Name, info.ImageType?.ToString())))
		{
			return;
		}

		Error = "";
		var current = (await LoadImages(Item)).FirstOrDefault(x => x.ImageType == info.ImageType && x.ImageTag == info.ImageTag);
		if (current is null)
		{
			Error = "That image is no longer on the server. The list shows what is there now.";
		}
		else if (!await jellyfinClient.DeleteImage(Item, current))
		{
			Error = "The server didn't delete the image. Nothing changed.";
		}

		Images = [.. await LoadImages(Item)];
	}

	private static List<ImageType?> GetAllSupportedImages(IEnumerable<ImageProviderInfo> providers)
	{
		HashSet<ImageType?> supportedImages = [];
		foreach (var p in providers)
		{
			if (p.SupportedImages is null)
			{
				continue;
			}

			foreach (var imageType in p.SupportedImages)
			{
				if (imageType is null)
				{
					continue;
				}

				supportedImages.Add(imageType);
			}
		}

		return [.. supportedImages];
	}
}

public enum EditImagesViewModelState
{
	Display,
	Search,
	Loading
}

public class ImageInfoEx : ImageInfo
{
	public Uri? Uri { get; set; }
}
