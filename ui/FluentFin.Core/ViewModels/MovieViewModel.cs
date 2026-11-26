using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

public partial class MovieViewModel(IJellyfinClient jellyfinClient, GlobalCommands commands) : ObservableObject, INavigationAware, IKeptOnBack
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PlayLabel), nameof(HasResumePoint))]
	public partial BaseItemDto Dto { get; set; }

	/// <summary>fluentfin-mpv: the play button's text, "Resume at 12:34" when the movie has a resume point.</summary>
	public string PlayLabel => ItemLabels.PlayLabel(Dto);

	/// <summary>fluentfin-mpv: shows "Play from beginning" next to Resume.</summary>
	public bool HasResumePoint => ItemLabels.ResumePosition(Dto) is not null;

	[ObservableProperty]
	public partial ObservableCollection<BaseItemViewModel> Similar { get; set; }

	/// <summary>Nixie 1.4.0: the collections the film is part of, for "Part of: Copper and Glass" (Controls.PartOfLink); none on an older server.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<BaseItemDto> PartOf { get; set; } = [];

	[ObservableProperty]
	public partial IList<MediaStream> AudioStreams { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectedAudioIndex), nameof(SelectedAudioLabel))]
	public partial MediaStream? SelectedAudio { get; set; }

	[ObservableProperty]
	public partial IList<MediaStream> SubtitleStreams { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectedSubtitleIndex))]
	public partial MediaStream? SelectedSubtitle { get; set; }

	// 1.2.0 (1.1.3 UX audit, layout item 16): the pickers show plain labels ("English (5.1)", "English (CC)", "Off";
	// TrackLabels), strings so Narrator reads them, chosen by their place; the streams stay what Play sends
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectedAudioLabel))]
	public partial IReadOnlyList<string> AudioLabels { get; set; } = [];

	[ObservableProperty]
	public partial IReadOnlyList<string> SubtitleLabels { get; set; } = [];

	public int SelectedAudioIndex
	{
		get => AudioStreams?.IndexOf(SelectedAudio!) ?? -1;
		set => SelectedAudio = Pick(AudioStreams, value, SelectedAudio);
	}

	public int SelectedSubtitleIndex
	{
		get => SubtitleStreams?.IndexOf(SelectedSubtitle!) ?? -1;
		set => SelectedSubtitle = Pick(SubtitleStreams, value, SelectedSubtitle);
	}

	/// <summary>The one audio track's label, when there is no choice to make.</summary>
	public string SelectedAudioLabel => SelectedAudioIndex is >= 0 and var i && i < AudioLabels.Count ? AudioLabels[i] : "";

	// a picker that lost its items (-1) keeps the choice
	private static MediaStream? Pick(IList<MediaStream>? streams, int index, MediaStream? current) =>
		streams is not null && index >= 0 && index < streams.Count ? streams[index] : current;

	[ObservableProperty]
	public partial bool HasSubtitles { get; set; }

	[ObservableProperty]
	public partial string VideoTitle { get; set; }

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	/// <summary>fluentfin-mpv: this movie could not be loaded: said on the page, with Retry (docs/research/ux-review.md §3.11).</summary>
	public LoadState State { get; } = new("this movie");

	public Task OnNavigatedFrom() => Task.CompletedTask;

	// ---- Nixie 1.7.4: Back shows the film's page as it was (IKeptOnBack) ------------------------------------------------------

	public bool CanKeep => State.IsLoaded && Dto is not null;

	/// <summary>
	/// Back or Forward shows the page as it was (the tracks picked stay picked). When something may have changed since (a play,
	/// a watch state), the film and More like this are asked again behind it: its watch state, favourite and resume point (Play's
	/// label) in place, the same cards kept.
	/// </summary>
	public async Task Returned(object parameter, bool stale)
	{
		if (!stale || Dto?.Id is not { } id)
		{
			return;
		}

		var shown = Dto;
		// 1.8.1 (BH-175-q2): each write only while this refresh is the newest (an older one whose answers came last overwrote a
		// newer one's: Back, Forward and Back while the server was slow)
		await State.RefreshBehindAsync(async newest =>
		{
			var similar = JellyfinClient.GetSimilarItems(shown);
			var fresh = await JellyfinClient.GetItem(id);
			if (fresh is null)
			{
				return LoadMessages.CouldNotLoad("this movie");
			}

			if (newest() && KeptCards.RefreshItem(shown, fresh))
			{
				OnPropertyChanged(nameof(PlayLabel));
				OnPropertyChanged(nameof(HasResumePoint));
			}

			if (await similar is { Items: { } items } && newest() && !(Similar is { } cards && KeptCards.Refresh([.. cards], items)))
			{
				Similar = [.. items.Select(BaseItemViewModel.FromDto)];
			}

			return LoadOutcome.Loaded;
		});
	}

	// fluentfin-mpv: whether the user changed a picker after the page chose its defaults. The mpv player sends a track pick
	// only then; otherwise the backend's remembered tracks and language defaults apply. The other players are unchanged.
	private bool _streamsPreselected;
	private bool _streamsPicked;

	partial void OnSelectedAudioChanged(MediaStream? value) => _streamsPicked |= _streamsPreselected && value is not null;

	partial void OnSelectedSubtitleChanged(MediaStream? value) => _streamsPicked |= _streamsPreselected && value is not null;

	[RelayCommand]
	private Task Play() => commands.Play(Dto, PlaybackSelection.From(SelectedAudio, SelectedSubtitle), _streamsPicked);

	// fluentfin-mpv: the secondary action next to Resume (docs/research/ux-review.md I2)
	[RelayCommand]
	private Task PlayFromBeginning() => commands.Play(Dto, PlaybackSelection.From(SelectedAudio, SelectedSubtitle), _streamsPicked, fromBeginning: true);

	public async Task OnNavigatedTo(object parameter)
	{
		if (parameter is not BaseItemDto dto)
		{
			return;
		}

		if (dto.Id is not { } id)
		{
			return;
		}

		await State.RunAsync(async () =>
		{
			await UpdateMovie(id);
			if (Dto?.Id != id)
			{
				return LoadMessages.CouldNotLoad("this movie");
			}

			await Task.WhenAll(UpdateSimilar(dto), UpdatePartOf(id));
			return LoadOutcome.Loaded;
		});
	}

	private async Task UpdateMovie(Guid id)
	{
		_streamsPreselected = _streamsPicked = false;
		var movie = await JellyfinClient.GetItem(id);
		if (movie is null)
		{
			return;
		}

		Dto = movie;

		if (Dto.MediaStreams is { Count: > 0 } streams)
		{
			VideoTitle = streams.FirstOrDefault(x => x.Type == MediaStream_Type.Video)?.DisplayTitle ?? string.Empty;

			AudioStreams = streams.Where(x => x.Type == MediaStream_Type.Audio).ToList();
			AudioLabels = TrackLabels.Labels(AudioStreams);
			SelectedAudio = AudioStreams.FirstOrDefault(x => x.IsDefault == true) ?? AudioStreams.FirstOrDefault();

			var defaultItem = new MediaStream();
			typeof(MediaStream).GetProperty(nameof(MediaStream.DisplayTitle))!.SetValue(defaultItem, "None");

			SubtitleStreams = [defaultItem, .. streams.Where(x => x.Type == MediaStream_Type.Subtitle).ToList()];
			SubtitleLabels = TrackLabels.Labels(SubtitleStreams);
			HasSubtitles = SubtitleStreams.Count > 1;
		}

		if (Dto.MediaSources is { Count: 1 } sources)
		{
			if (sources[0].DefaultSubtitleStreamIndex > 0)
			{
				SelectedSubtitle = SubtitleStreams.FirstOrDefault(x => x.Index == sources[0].DefaultSubtitleStreamIndex);
			}
			else
			{
				SelectedSubtitle = SubtitleStreams?.FirstOrDefault();
			}
		}

		_streamsPreselected = true;
	}

	private async Task UpdatePartOf(Guid id) => PartOf = await (JellyfinClient.GetItemCollections(id) ?? Task.FromResult<IReadOnlyList<BaseItemDto>>([])) ?? [];

	private async Task UpdateSimilar(BaseItemDto dto)
	{
		var response = await JellyfinClient.GetSimilarItems(dto);

		if (response is null or { Items: null })
		{
			return;
		}

		Similar = [.. response.Items.Select(BaseItemViewModel.FromDto)];
	}
}
