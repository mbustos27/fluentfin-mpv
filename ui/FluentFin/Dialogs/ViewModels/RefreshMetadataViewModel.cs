using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Items.Item.Refresh;

namespace FluentFin.Dialogs.ViewModels;

public partial class RefreshMetadataViewModel(IJellyfinClient jellyfinClient) : ObservableObject, IItemDialogViewModel
{
	public Guid? Item { get; set; }

	public List<string> RefreshModes { get; } = ["Scan for new and updated files", "Search for missing metadata", "Replace all metadata"];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ReplaceWarning))]
	public partial string SelectedRefreshMode { get; set; } = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ReplaceWarning))]
	public partial bool ReplaceExistingImages { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ReplaceWarning))]
	public partial bool ReplaceExistingTrickplayImages { get; set; }

	/// <summary>
	/// Nixie 1.2.0 (admin, D77): what a replace-all choice overwrites, said before Refresh; empty when nothing is replaced. The
	/// image boxes count only where they show (not with "Scan for new and updated files").
	/// </summary>
	public string ReplaceWarning
	{
		get
		{
			var scanOnly = SelectedRefreshMode == RefreshModes.First();
			var lost = new List<string>();
			if (SelectedRefreshMode == RefreshModes.Last())
			{
				lost.Add("every metadata field, including edits made here");
			}

			if (!scanOnly && ReplaceExistingImages)
			{
				lost.Add("the current images");
			}

			if (!scanOnly && ReplaceExistingTrickplayImages)
			{
				lost.Add("the trickplay images");
			}

			return lost.Count == 0 ? "" : $"This replaces {string.Join(" and ", lost)}, from the internet sources. It can't be undone.";
		}
	}

	public Task Initialize(Guid? item)
	{
		Item = item;
		SelectedRefreshMode = RefreshModes.First();
		return Task.CompletedTask;
	}


	[RelayCommand]
	private async Task Refresh()
	{
		if (Item is null)
		{
			return;
		}

		var imageRefreshMode = SelectedRefreshMode == RefreshModes.First() ? MetadataRefreshMode.Default : MetadataRefreshMode.FullRefresh;
		var metadataRefreshModel = imageRefreshMode;
		var replaceAllMetadata = SelectedRefreshMode == RefreshModes.Last();

		var scanOnly = SelectedRefreshMode == RefreshModes.First();
		// the image boxes are hidden for a scan only: a box ticked before switching back to it is not applied
		var info = new RefreshMetadataInfo(imageRefreshMode, metadataRefreshModel, !scanOnly && ReplaceExistingImages, !scanOnly && ReplaceExistingTrickplayImages, replaceAllMetadata);

		await jellyfinClient.RefreshMetadata(Item.Value, info);
	}

}
