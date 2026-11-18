using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core;
using FluentFin.Core.Admin;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using ReactiveUI;

namespace FluentFin.ViewModels;

/// <remarks>
/// Nixie 1.2.0 (admin, button review D85-D90): Delete asks first, naming the segment (Cancel the default), and the card goes
/// only when the server deleted it; "Save segment" keeps the segment's id after creating it, so a second save replaces it
/// instead of adding a copy. The buttons that read or drive the player here (Play, Set start / end to now) are hidden with the
/// mpv player, where they did nothing or wrote 0.
/// </remarks>
public partial class MediaSegmentsEditorViewModel(IJellyfinClient jellyfinClient, ISettings settings) : ObservableObject, INavigationAware
{
	/// <summary>Asks before a destructive action; true only when the user confirmed. Unset (nobody to ask), nothing happens.</summary>
	public Func<ConfirmPrompt, Task<bool>>? Confirm { get; set; }

	[ObservableProperty]
	public partial PlaylistViewModel Playlist { get; set; } = new();

	[ObservableProperty]
	public partial IMediaPlayerController? MediaPlayer { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PlayerButtonsWork), nameof(PlayerButtonsVisibility))]
	public partial MediaPlayerType MediaPlayerType { get; set; }

	/// <summary>D86-D88: the page's player answers Play and its position (not with mpv, which plays in its own window).</summary>
	public bool PlayerButtonsWork => MediaPlayerType != MediaPlayerType.Mpv;

	/// <summary>For the segment cards' {Binding} (a template bound to each segment cannot x:Bind the page).</summary>
	public Microsoft.UI.Xaml.Visibility PlayerButtonsVisibility => PlayerButtonsWork ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

	public ObservableCollection<MediaSegmentViewModel> Segments { get; } = [];
	public long CurrentTimeTicks { get; set; }
	public MediaSegmentViewModel? PlayingSegment { get; set; }

	public Task OnNavigatedFrom()
	{
		MediaPlayer?.Stop();
		return Task.CompletedTask;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		if (parameter is not BaseItemDto dto)
		{
			return;
		}

		MediaPlayerType = settings.MediaPlayer;

		Playlist = dto.Type switch
		{
			BaseItemDto_Type.Movie => PlaylistViewModel.FromMovie(dto),
			BaseItemDto_Type.Episode => await PlaylistViewModel.FromEpisode(jellyfinClient, dto),
			BaseItemDto_Type.Series => await PlaylistViewModel.FromSeries(jellyfinClient, dto),
			BaseItemDto_Type.Season => await PlaylistViewModel.FromSeason(jellyfinClient, dto),
			_ => new PlaylistViewModel()
		};

		if (Playlist.Items.Count == 0)
		{
			return;
		}

		Playlist.PropertyChanged += OnPlaylistPropertyChanged;

		this.WhenAnyValue(x => x.MediaPlayer)
			.WhereNotNull()
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(mp =>
			{
				mp.PositionChanged
				  .Subscribe(time =>
				  {
					  CurrentTimeTicks = time.Ticks;

					  if (PlayingSegment is { } segment && CurrentTimeTicks > segment.EndTicks)
					  {
						  mp.Pause();
						  PlayingSegment = null;
					  }
				  });

				if (dto.Type == BaseItemDto_Type.Episode)
				{
					Playlist.SelectedItem = Playlist.Items.FirstOrDefault(x => x.Dto.IndexNumber == dto.IndexNumber && x.Dto.ParentIndexNumber == dto.ParentIndexNumber);
				}
				else
				{
					Playlist.SelectedItem = Playlist.Items.FirstOrDefault();
				}
			});


	}

	private async void OnPlaylistPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(Playlist.SelectedItem))
		{
			return;
		}

		if (Playlist.SelectedItem is not { } selectedItem)
		{
			return;
		}

		if (MediaPlayer is null)
		{
			return;
		}

		MediaPlayer.Stop();

		var full = await jellyfinClient.GetItem(selectedItem.Dto.Id ?? Guid.Empty);

		if (full is null)
		{
			return;
		}

		var mediaResponse = await jellyfinClient.GetMediaUrl(full);

		if (mediaResponse is null)
		{
			return;
		}

		selectedItem.Media = mediaResponse;

		var success = MediaPlayer.Play(mediaResponse.Uri);
		if (!success)
		{
			return;
		}

		MediaPlayer.Pause();

		if (await jellyfinClient.GetMediaSegments(selectedItem.Dto) is { Items.Count: > 0 } segmentsResults)
		{
			Segments.Clear();
			Segments.AddRange(segmentsResults.Items.Select(MediaSegmentViewModel.FromDto));
		}
	}

	[RelayCommand]
	private void AddSegment()
	{
		Segments.Add(new() { ItemId = Playlist.SelectedItem?.Dto?.Id });
	}

	[RelayCommand]
	private async Task DeleteSegment(MediaSegmentViewModel? vm)
	{
		if (vm is null)
		{
			return;
		}

		// a segment never saved is only on this page: it goes without a question
		if (vm.Id is not { } id)
		{
			Segments.Remove(vm);
			return;
		}

		if (Confirm is not { } confirm || !await confirm(AdminPrompts.DeleteSegment(vm.Type?.ToString(), Playlist.SelectedItem?.Dto?.Name)))
		{
			return;
		}

		if (await jellyfinClient.DeleteMediaSegment(id))
		{
			Segments.Remove(vm);
		}
	}

	[RelayCommand]
	private async Task SubmitSegment(MediaSegmentViewModel vm)
	{
		if (vm.Id is { } id && !await jellyfinClient.DeleteMediaSegment(id))
		{
			return;
		}

		var dto = vm.ToDto();
		// D89: the id it was created with, so the next save replaces this segment instead of adding a copy
		vm.Id = await jellyfinClient.CreateMediaSegment(dto) ? dto.Id : null;
	}

	[RelayCommand]
	private void PlaySegment(MediaSegmentViewModel vm)
	{
		PlayingSegment = vm;
		MediaPlayer?.Play();
		MediaPlayer?.SeekTo(new TimeSpan(vm.StartTicks));
	}
}

public partial class MediaSegmentViewModel : ObservableObject
{
	[ObservableProperty]
	public partial long StartTicks { get; set; }

	[ObservableProperty]
	public partial long EndTicks { get; set; }

	[ObservableProperty]
	public partial Guid? ItemId { get; set; }

	[ObservableProperty]
	public partial MediaSegmentDto_Type? Type { get; set; }

	public Guid? Id { get; set; }

	public MediaSegmentDto ToDto() => new()
	{
		StartTicks = StartTicks,
		EndTicks = EndTicks,
		ItemId = ItemId ?? Guid.NewGuid(),
		Type = Type,
		Id = Id ?? Guid.NewGuid()
	};

	public static MediaSegmentViewModel FromDto(MediaSegmentDto dto) => new()
	{
		StartTicks = dto.StartTicks ?? 0,
		EndTicks = dto.EndTicks ?? 0,
		ItemId = dto.ItemId,
		Type = dto.Type,
		Id = dto.Id
	};
}
