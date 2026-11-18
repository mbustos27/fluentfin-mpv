using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>One small pill of an item page's chip row: <see cref="Text"/> is what it shows, <see cref="Name"/> what Narrator reads.</summary>
public sealed record ItemChip(string Text, string Name);

/// <summary>
/// Seaglass: the chip row at the top of the movie, series, season and episode pages: year, official rating, community rating,
/// runtime, resolution, HDR or Dolby Vision, audio codec and channels. A chip is left out when the item does not say.
/// </summary>
public static class ItemChips
{
	public static IReadOnlyList<ItemChip> For(BaseItemDto? dto)
	{
		if (dto is null)
		{
			return [];
		}

		var chips = new List<ItemChip>();
		if (dto.Type != BaseItemDto_Type.Episode && dto.ProductionYear is > 0 and var year)
		{
			var text = year.ToString(CultureInfo.InvariantCulture);
			chips.Add(new(text, $"Year {text}"));
		}

		if (dto.Type == BaseItemDto_Type.Episode && dto.PremiereDate is { } aired)
		{
			var text = aired.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
			chips.Add(new(text, $"Aired {text}"));
		}

		if (!string.IsNullOrWhiteSpace(dto.OfficialRating))
		{
			chips.Add(new(dto.OfficialRating.Trim(), $"Rated {dto.OfficialRating.Trim()}"));
		}

		if (dto.CommunityRating is > 0 and var community)
		{
			var text = community.ToString("0.0", CultureInfo.InvariantCulture);
			chips.Add(new($"★ {text}", $"Community rating {text} out of 10"));
		}

		if (dto.RunTimeTicks is > 0 and var ticks && dto.Type is not (BaseItemDto_Type.Series or BaseItemDto_Type.Season))
		{
			var text = Runtime(ticks);
			chips.Add(new(text, $"Runtime {text}"));
		}

		var video = Streams(dto, MediaStream_Type.Video).FirstOrDefault();
		if (Resolution(video?.Width ?? dto.Width, video?.Height ?? dto.Height) is { } resolution)
		{
			chips.Add(new(resolution, $"Resolution {resolution}"));
		}

		if (video is not null && DynamicRange(video) is { } range)
		{
			chips.Add(new(range, $"Video range {range}"));
		}

		var audioStreams = Streams(dto, MediaStream_Type.Audio).ToList();
		var audio = audioStreams.FirstOrDefault(s => s.IsDefault == true) ?? audioStreams.FirstOrDefault();
		if (audio is not null && Audio(audio) is { } sound)
		{
			chips.Add(new(sound, $"Audio {sound}"));
		}

		return chips;
	}

	/// <summary>
	/// 1.2.0 (1.1.3 UX audit, layout items 8 and 21): the hero's facts as one line of plain text, "2008 · R · ★ 8.3 ·
	/// 1h 52m · 4K", not a row of bordered chips; an episode's starts with its number, "S1 E1 · Sep 1, 1991 · TV-Y7 · 10m".
	/// Plain stereo or mono sound is left out (only surround and spatial sound tell anything).
	/// </summary>
	public static string MetaLine(BaseItemDto? dto) => string.Join(" · ", MetaParts(dto).Select(p => p.Text));

	/// <summary>What Narrator reads for <see cref="MetaLine"/>: "Season 1, episode 1, Aired Sep 1, 1991, Rated TV-Y7, Runtime 10m".</summary>
	public static string MetaName(BaseItemDto? dto) => string.Join(", ", MetaParts(dto).Select(p => p.Name));

	private static IEnumerable<ItemChip> MetaParts(BaseItemDto? dto)
	{
		if (dto?.Type == BaseItemDto_Type.Episode && ItemLabels.EpisodeCode(dto) is { Length: > 0 } code)
		{
			var spoken = (dto.ParentIndexNumber, dto.IndexNumber) switch
			{
				({ } s, { } e) => $"Season {s}, episode {e}",
				(null, { } e) => $"Episode {e}",
				_ => code,
			};
			yield return new ItemChip(code, spoken);
		}

		foreach (var chip in For(dto))
		{
			if (chip.Name.StartsWith("Audio ", StringComparison.Ordinal) && (chip.Text.EndsWith(" Stereo", StringComparison.Ordinal) || chip.Text.EndsWith(" Mono", StringComparison.Ordinal)))
			{
				continue;
			}

			yield return chip;
		}
	}

	/// <summary>"1h 32m", "45m" (the server counts in ticks of 100 ns).</summary>
	public static string Runtime(long ticks)
	{
		var span = TimeSpan.FromTicks(ticks);
		var minutes = (int)Math.Round(span.TotalMinutes);
		return minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{Math.Max(1, minutes)}m";
	}

	/// <summary>"4K", "1080p", "720p" or "SD" from the frame size (a wide, cropped picture counts by its width); null when unknown.</summary>
	public static string? Resolution(int? width, int? height)
	{
		if (width is not > 0 && height is not > 0)
		{
			return null;
		}

		int w = width ?? 0, h = height ?? 0;
		return (w, h) switch
		{
			_ when w >= 3400 || h >= 2000 => "4K",
			_ when w >= 2500 || h >= 1400 => "1440p",
			_ when w >= 1800 || h >= 1000 => "1080p",
			_ when w >= 1200 || h >= 700 => "720p",
			_ => "SD",
		};
	}

	/// <summary>"Dolby Vision", "HDR10+", "HDR10", "HLG"; null for plain SDR or when the server does not say.</summary>
	public static string? DynamicRange(MediaStream video) => video.VideoRangeType switch
	{
		MediaStream_VideoRangeType.DOVI or MediaStream_VideoRangeType.DOVIWithHDR10 or MediaStream_VideoRangeType.DOVIWithHLG
			or MediaStream_VideoRangeType.DOVIWithSDR or MediaStream_VideoRangeType.DOVIWithEL
			or MediaStream_VideoRangeType.DOVIWithHDR10Plus or MediaStream_VideoRangeType.DOVIWithELHDR10Plus => "Dolby Vision",
		MediaStream_VideoRangeType.HDR10Plus => "HDR10+",
		MediaStream_VideoRangeType.HDR10 => "HDR10",
		MediaStream_VideoRangeType.HLG => "HLG",
		_ => null,
	};

	/// <summary>"Dolby Atmos 7.1", "Dolby Digital 5.1", "AAC Stereo": the codec (or the spatial format) and the channels.</summary>
	public static string? Audio(MediaStream audio)
	{
		var codec = audio.AudioSpatialFormat switch
		{
			MediaStream_AudioSpatialFormat.DolbyAtmos => "Dolby Atmos",
			MediaStream_AudioSpatialFormat.DTSX => "DTS:X",
			_ => CodecName(audio.Codec),
		};
		if (codec is null)
		{
			return null;
		}

		var channels = audio.Channels switch
		{
			1 => "Mono",
			2 => "Stereo",
			6 => "5.1",
			8 => "7.1",
			> 0 and var n => $"{n} ch",
			_ => null,
		};
		return channels is null ? codec : $"{codec} {channels}";
	}

	private static string? CodecName(string? codec) => codec?.Trim().ToLowerInvariant() switch
	{
		null or "" => null,
		"ac3" => "Dolby Digital",
		"eac3" => "Dolby Digital Plus",
		"truehd" => "Dolby TrueHD",
		"dts" => "DTS",
		"dca" => "DTS",
		"aac" => "AAC",
		"opus" => "Opus",
		"flac" => "FLAC",
		"vorbis" => "Vorbis",
		"mp3" => "MP3",
		var other => other.ToUpperInvariant(),
	};

	/// <summary>The item's own streams, else its first media source's (the server lists them in one place or the other).</summary>
	private static IEnumerable<MediaStream> Streams(BaseItemDto dto, MediaStream_Type type)
	{
		var streams = dto.MediaStreams is { Count: > 0 } own ? own : dto.MediaSources?.FirstOrDefault()?.MediaStreams;
		return streams?.Where(s => s.Type == type) ?? [];
	}
}
