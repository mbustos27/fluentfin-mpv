using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

public partial class EpisodeViewModel(IJellyfinClient jellyfinClient, GlobalCommands commands) : ObservableObject, INavigationAware
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PlayLabel), nameof(HasResumePoint))]
	public partial BaseItemDto Dto { get; set; }

	/// <summary>fluentfin-mpv: the play button's text, "Resume at 12:34" when the episode has a resume point.</summary>
	public string PlayLabel => ItemLabels.PlayLabel(Dto);

	/// <summary>fluentfin-mpv: shows "Play from beginning" next to Resume.</summary>
	public bool HasResumePoint => ItemLabels.ResumePosition(Dto) is not null;

	[ObservableProperty]
	public partial IList<MediaStream> AudioStreams { get; set; }

	[ObservableProperty]
	public partial MediaStream? SelectedAudio { get; set; }

	[ObservableProperty]
	public partial IList<MediaStream> SubtitleStreams { get; set; } = [];

	[ObservableProperty]
	public partial MediaStream? SelectedSubtitle { get; set; }

	[ObservableProperty]
	public partial IEnumerable<BaseItemPerson> GuestStars { get; set; }

	[ObservableProperty]
	public partial IEnumerable<BaseItemPerson> CastAndCrew { get; set; }

	[ObservableProperty]
	public partial string VideoTitle { get; set; }

	[ObservableProperty]
	public partial bool HasSubtitles { get; set; }

	public Task OnNavigatedFrom() => Task.CompletedTask;

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

		await UpdateEpisode(id);
	}

	private async Task UpdateEpisode(Guid id)
	{
		_streamsPreselected = _streamsPicked = false;
		var episode = await jellyfinClient.GetItem(id);
		if (episode is null)
		{
			return;
		}
		Dto = episode;

		if (Dto.People is { Count: > 0 } people)
		{
			CastAndCrew = people.Where(x => x.Type != BaseItemPerson_Type.GuestStar);
			GuestStars = people.Where(x => x.Type == BaseItemPerson_Type.GuestStar);
		}

		if (Dto.MediaStreams is { Count: > 0 } streams)
		{
			VideoTitle = streams.FirstOrDefault(x => x.Type == MediaStream_Type.Video)?.DisplayTitle ?? string.Empty;

			AudioStreams = streams.Where(x => x.Type == MediaStream_Type.Audio).ToList();
			SelectedAudio = AudioStreams.FirstOrDefault(x => x.IsDefault == true) ?? AudioStreams.FirstOrDefault();

			var defaultItem = new MediaStream();
			typeof(MediaStream).GetProperty(nameof(MediaStream.DisplayTitle))!.SetValue(defaultItem, "None");

			SubtitleStreams = [defaultItem, .. streams.Where(x => x.Type == MediaStream_Type.Subtitle).ToList()];
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
				SelectedSubtitle = SubtitleStreams.FirstOrDefault();
			}
		}

		_streamsPreselected = true;
	}
}
