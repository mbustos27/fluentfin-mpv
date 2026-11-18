using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>
/// What a track label is made of: the facts of an audio or subtitle stream, from the server's <see cref="MediaStream"/>
/// (<see cref="From(MediaStream?)"/>) or from anything else that knows them (the player's tracks).
/// </summary>
/// <param name="IsNone">The "no subtitles" choice of a picker: labelled "Off".</param>
public sealed record TrackFacts(
	bool IsAudio,
	string? Language = null,
	string? LocalizedLanguage = null,
	string? Title = null,
	int? Channels = null,
	bool Forced = false,
	bool HearingImpaired = false,
	bool IsNone = false)
{
	public static TrackFacts From(MediaStream? stream) => stream?.Type switch
	{
		MediaStream_Type.Audio or MediaStream_Type.Subtitle => new TrackFacts(
			stream.Type == MediaStream_Type.Audio,
			stream.Language,
			stream.LocalizedLanguage,
			stream.Title,
			stream.Channels,
			stream.IsForced == true,
			stream.IsHearingImpaired == true),
		_ => new TrackFacts(false, IsNone: true),
	};
}

/// <summary>
/// 1.2.0 (1.1.3 UX audit, layout item 16): the one way the app names an audio or subtitle track, as streaming apps do:
/// "English", "English (5.1)", "English (CC)", "Spanish (Forced)", "English – Director's Commentary", "Off". The language's
/// name, then only what tells two tracks apart; never the codec or the container ("English - AAC - Stereo - Default" was the
/// server's DisplayTitle, which Media info keeps). The item pages' Audio and Subtitle pickers use it; the player can too, from
/// its own track facts (<see cref="TrackFacts"/>).
/// </summary>
public static partial class TrackLabels
{
	/// <summary>One track's label, without regard to the others (<see cref="Labels"/> keeps a list's labels apart).</summary>
	public static string Name(TrackFacts track)
	{
		if (track.IsNone)
		{
			return "Off";
		}

		var title = Informative(track.Title, out var titleForced, out var titleCaptions);
		var name = LanguageName(track.LocalizedLanguage, track.Language) ?? title ?? "Unknown language";
		if (ReferenceEquals(name, title))
		{
			title = null;
		}

		var notes = new List<string>();
		if (track.IsAudio && Surround(track.Channels) is { } layout)
		{
			notes.Add(layout);
		}

		if (!track.IsAudio && (track.Forced || titleForced))
		{
			notes.Add("Forced");
		}

		if (!track.IsAudio && (track.HearingImpaired || titleCaptions))
		{
			notes.Add("CC");
		}

		var label = notes.Count > 0 ? $"{name} ({string.Join(", ", notes)})" : name;
		return title is null ? label : $"{label} – {title}";
	}

	/// <summary>
	/// A picker's labels, in order, each different from the others: where two would read the same, the channels tell them
	/// apart ("English (Stereo)" and "English (Stereo, 2)" stay distinct by number at worst).
	/// </summary>
	public static IReadOnlyList<string> Labels(IReadOnlyList<TrackFacts> tracks)
	{
		var labels = tracks.Select(Name).ToList();
		foreach (var group in Duplicates(labels))
		{
			foreach (var i in group)
			{
				if (tracks[i].IsAudio && Channels(tracks[i].Channels) is { } channels && Surround(tracks[i].Channels) is null)
				{
					labels[i] = Insert(labels[i], channels);
				}
			}
		}

		foreach (var group in Duplicates(labels))
		{
			var n = 1;
			foreach (var i in group.Skip(1))
			{
				labels[i] = Insert(labels[i], (++n).ToString(CultureInfo.InvariantCulture));
			}
		}

		return labels;
	}

	/// <summary>The labels of the server's streams (a null or typeless entry is the "Off" choice).</summary>
	public static IReadOnlyList<string> Labels(IEnumerable<MediaStream?>? streams) => Labels([.. (streams ?? []).Select(TrackFacts.From)]);

	/// <summary>"English" from the server's localized name or from the code ("eng", "en", "fre"); null when neither says.</summary>
	public static string? LanguageName(string? localized, string? code)
	{
		if (!string.IsNullOrWhiteSpace(localized) && !Undefined(localized))
		{
			return localized.Trim();
		}

		if (string.IsNullOrWhiteSpace(code) || Undefined(code))
		{
			return null;
		}

		var key = code.Trim().ToLowerInvariant();
		key = BibliographicCodes.GetValueOrDefault(key, key);
		return Languages.Value.TryGetValue(key, out var name) ? name : code.Trim();
	}

	private static bool Undefined(string text) => text.Trim().ToLowerInvariant() is "und" or "undefined" or "unknown" or "mis" or "zxx" or "mul";

	private static string? Surround(int? channels) => channels switch
	{
		6 => "5.1",
		8 => "7.1",
		> 2 and var n => $"{n} channels",
		_ => null,
	};

	private static string? Channels(int? channels) => channels switch
	{
		1 => "Mono",
		2 => "Stereo",
		_ => null,
	};

	private static string Insert(string label, string note)
	{
		var dash = label.IndexOf(" – ", StringComparison.Ordinal);
		var head = dash < 0 ? label : label[..dash];
		var tail = dash < 0 ? "" : label[dash..];
		head = head.EndsWith(')') ? $"{head[..^1]}, {note})" : $"{head} ({note})";
		return head + tail;
	}

	private static IEnumerable<List<int>> Duplicates(List<string> labels) =>
		labels.Select((label, i) => (label, i)).GroupBy(x => x.label, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Select(x => x.i).ToList()).ToList();

	/// <summary>
	/// The stream's own title when it says something a person reads ("Director's Commentary"), else null: a title of codec
	/// and channel words ("AAC Stereo", "English SRT", "Full", "SDH") says nothing the label does not. Whether it says forced or
	/// captions is kept apart.
	/// </summary>
	private static string? Informative(string? title, out bool forced, out bool captions)
	{
		forced = captions = false;
		if (string.IsNullOrWhiteSpace(title))
		{
			return null;
		}

		forced = ForcedWord().IsMatch(title);
		captions = CaptionsWord().IsMatch(title);
		var rest = TechnicalWords().Replace(title, " ");
		rest = Regex.Replace(rest, @"[\s\-_.,:;/|()\[\]]+", " ").Trim();
		if (rest.Length == 0 || Languages.Value.Values.Contains(rest, StringComparer.OrdinalIgnoreCase))
		{
			return null;
		}

		return title.Trim();
	}

	[GeneratedRegex(@"\bforced\b", RegexOptions.IgnoreCase)]
	private static partial Regex ForcedWord();

	[GeneratedRegex(@"\b(sdh|cc|hi|hearing impaired)\b", RegexOptions.IgnoreCase)]
	private static partial Regex CaptionsWord();

	[GeneratedRegex(@"\b(aac|he-?aac|ac-?3|e-?ac-?3|dd\+?|dolby( digital)?( plus)?|dts(-?hd)?( ma)?|dts-?x|truehd|atmos|flac|opus|vorbis|mp3|pcm|lpcm|srt|subrip|ass|ssa|pgs|pgssub|vobsub|dvdsub|dvb|webvtt|vtt|mov_text|stereo|mono|surround|[1-9]\.[01](ch)?|[1-9] ?ch|channels?|default|full|complete|forced|sdh|cc|hi|external|kbps|\d+ ?k)\b", RegexOptions.IgnoreCase)]
	private static partial Regex TechnicalWords();

	// ISO 639-2/B codes some servers use, to the /T codes .NET knows
	private static readonly Dictionary<string, string> BibliographicCodes = new(StringComparer.Ordinal)
	{
		["fre"] = "fra", ["ger"] = "deu", ["chi"] = "zho", ["cze"] = "ces", ["dut"] = "nld", ["gre"] = "ell", ["per"] = "fas",
		["rum"] = "ron", ["slo"] = "slk", ["wel"] = "cym", ["ice"] = "isl", ["mac"] = "mkd", ["alb"] = "sqi", ["arm"] = "hye",
		["baq"] = "eus", ["bur"] = "mya", ["geo"] = "kat", ["may"] = "msa", ["tib"] = "bod",
	};

	private static readonly Lazy<Dictionary<string, string>> Languages = new(() =>
	{
		var names = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
		{
			if (string.IsNullOrEmpty(culture.Name) || string.IsNullOrEmpty(culture.EnglishName))
			{
				continue;
			}

			names.TryAdd(culture.ThreeLetterISOLanguageName.ToLowerInvariant(), culture.EnglishName);
			names.TryAdd(culture.TwoLetterISOLanguageName.ToLowerInvariant(), culture.EnglishName);
		}

		return names;
	});
}
