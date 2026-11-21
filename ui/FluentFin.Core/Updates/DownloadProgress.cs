using System.Globalization;

namespace FluentFin.Core.Updates;

/// <summary>
/// Nixie 1.5: how far a download is: the bytes done, the total (when known), the speed over the last few seconds and the time
/// left at that speed (null until there is a speed to go by).
/// </summary>
public readonly record struct DownloadProgress(long Done, long? Total, double? BytesPerSecond, TimeSpan? Left)
{
	/// <summary>The part done, 0 to 1; null without a total.</summary>
	public double? Fraction => Total is > 0 ? Math.Clamp((double)Done / Total.Value, 0, 1) : null;
}

/// <summary>
/// Nixie 1.5: the download's speed and time left, from samples of the bytes done (one every ~250 ms). The speed is the bytes
/// over the last <see cref="Window"/> (steady, not one sample's jitter), and only once the samples span
/// <see cref="MinimumSpan"/>; the time left is what remains at that speed. The clock is injected for the tests.
/// </summary>
public sealed class ProgressMeter(Func<DateTimeOffset> clock)
{
	public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan MinimumSpan = TimeSpan.FromSeconds(1);

	private readonly Queue<(DateTimeOffset At, long Done)> _samples = new();

	public DownloadProgress Sample(long done, long? total)
	{
		var now = clock();
		if (_samples.Count > 0 && (done < _samples.Peek().Done || now < _samples.Peek().At))
		{
			_samples.Clear();   // started over (a checksum mismatch) or the clock went back: a new measure
		}

		_samples.Enqueue((now, done));
		while (_samples.Count > 2 && now - _samples.Peek().At > Window)
		{
			_samples.Dequeue();
		}

		var (firstAt, firstDone) = _samples.Peek();
		var span = now - firstAt;
		double? speed = span >= MinimumSpan ? (done - firstDone) / span.TotalSeconds : null;
		TimeSpan? left = speed is > 0 && total is { } t && t >= done ? TimeSpan.FromSeconds(Math.Ceiling((t - done) / speed.Value)) : null;
		return new DownloadProgress(done, total, speed, left);
	}
}

/// <summary>Nixie 1.5: sizes, speeds and times as the updater says them ("84 MB of 187 MB · 6.2 MB/s · about 20 s left").</summary>
public static class UpdateText
{
	public const string Separator = " · ";

	private const double KB = 1024, MB = KB * 1024, GB = MB * 1024;

	/// <summary>"850 KB", "9.5 MB", "187 MB", "1.2 GB" (powers of 1024, as Windows counts them).</summary>
	public static string Size(long bytes) => bytes switch
	{
		< (long)MB => $"{Math.Max(1, (long)Math.Round(bytes / KB))} KB",
		< (long)(10 * MB) => $"{(bytes / MB).ToString("0.0", CultureInfo.InvariantCulture)} MB",
		< (long)GB => $"{Math.Round(bytes / MB).ToString("0", CultureInfo.InvariantCulture)} MB",
		_ => $"{(bytes / GB).ToString("0.0", CultureInfo.InvariantCulture)} GB",
	};

	/// <summary>"6.2 MB/s", "850 KB/s".</summary>
	public static string Speed(double bytesPerSecond) => bytesPerSecond < MB
		? $"{Math.Round(bytesPerSecond / KB).ToString("0", CultureInfo.InvariantCulture)} KB/s"
		: $"{(bytesPerSecond / MB).ToString(bytesPerSecond < 100 * MB ? "0.0" : "0", CultureInfo.InvariantCulture)} MB/s";

	/// <summary>"a few seconds left", "about 20 s left" (in fives), "about 3 min left", "about 1 h 10 min left".</summary>
	public static string TimeLeft(TimeSpan left)
	{
		if (left <= TimeSpan.FromSeconds(5))
		{
			return "a few seconds left";
		}

		if (left < TimeSpan.FromSeconds(57.5))
		{
			return $"about {(int)(Math.Ceiling(left.TotalSeconds / 5) * 5)} s left";
		}

		var minutes = (int)Math.Round(left.TotalMinutes, MidpointRounding.AwayFromZero);
		return minutes < 60 ? $"about {Math.Max(1, minutes)} min left" : $"about {minutes / 60} h {minutes % 60} min left";
	}

	/// <summary>The progress line: what is known of "84 MB of 187 MB · 6.2 MB/s · about 20 s left".</summary>
	public static string Progress(DownloadProgress progress)
	{
		var parts = new List<string> { progress.Total is > 0 ? $"{Size(progress.Done)} of {Size(progress.Total.Value)}" : Size(progress.Done) };
		if (progress.BytesPerSecond is { } speed)
		{
			parts.Add(Speed(speed));
		}

		if (progress.Left is { } left)
		{
			parts.Add(TimeLeft(left));
		}

		return string.Join(Separator, parts);
	}

	/// <summary>
	/// "just now", "5 min ago", "3 h ago", "yesterday at 14:05", "Fri 27 Nov at 14:05": when the last check was, in the user's time
	/// zone. A time in the future (the clock went back) is "just now".
	/// </summary>
	public static string Ago(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
	{
		var age = now - at;
		if (age < TimeSpan.FromMinutes(1))
		{
			return "just now";
		}

		if (age < TimeSpan.FromHours(1))
		{
			return $"{(int)age.TotalMinutes} min ago";
		}

		var local = TimeZoneInfo.ConvertTime(at, zone);
		var today = TimeZoneInfo.ConvertTime(now, zone).Date;
		if (local.Date == today)
		{
			return $"{(int)age.TotalHours} h ago";
		}

		var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
		return local.Date == today.AddDays(-1) ? $"yesterday at {time}" : $"{local.ToString("ddd d MMM", CultureInfo.InvariantCulture)} at {time}";
	}
}
