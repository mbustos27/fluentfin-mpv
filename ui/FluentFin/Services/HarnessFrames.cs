using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.9 (the scroll stream; the owner: "scrolling the collections page takes a longgg time"), dev builds only: the
/// off-screen harness's frame clock. The pointer hook's <see cref="HarnessPointer.Kind.Frames"/> starts it (high word 1) and
/// stops it (2): from the start every frame the UI thread draws (<c>CompositionTarget.Rendering</c>) is timed, with the page's
/// scroll offset at that frame; the stop logs one line ("[harness] frames: ..."): the frames, how many took over 50 ms and how
/// long those took together, the longest, the 95th percentile, and when the page first reached its end. A release package
/// never listens (the hook is HarnessPointer's).
/// </summary>
public static class HarnessFrames
{
	/// <summary>A frame that took longer than this is a stall the user sees (three frames missed at 60 Hz).</summary>
	public const double SlowFrameMs = 50;

	private static readonly List<(long At, double Offset)> s_frames = [];

	// the pictures (LoadingImage) made while the hook listens, held weakly: how many are alive, and how many in the window
	private static readonly List<WeakReference<Controls.LoadingImage>> s_images = [];
	private static readonly bool s_listens = HarnessPointer.Listens(Environment.GetEnvironmentVariable(HarnessPointer.Variable), Core.AppVersion.IsReleasePackage);
	private static UIElement? s_content;
	private static int s_layouts;

	// the collector's pauses and its gen-2 collections at the start (the stretch's own are the difference)
	private static TimeSpan s_gcPause;
	private static int s_gen2;
	private static FrameworkElement? s_page;
	private static bool s_running;
	private static long s_startedAt;

	/// <summary>Starts timing frames (any timing before is dropped); <paramref name="content"/> is the window's content.</summary>
	public static void Start(UIElement content)
	{
		if (s_running)
		{
			CompositionTarget.Rendering -= OnFrame;
		}

		s_frames.Clear();
		s_layouts = 0;
		s_content = content;
		if (content is FrameworkElement root)
		{
			root.LayoutUpdated -= OnLayout;
			root.LayoutUpdated += OnLayout;
		}

		s_page = PageScroller(content);
		s_gcPause = GC.GetTotalPauseDuration();
		s_gen2 = GC.CollectionCount(2);
		s_startedAt = Stopwatch.GetTimestamp();
		s_running = true;
		CompositionTarget.Rendering += OnFrame;
	}

	/// <summary>Stops timing and returns what the frames were, or null when it never started.</summary>
	public static FrameStats? Stop()
	{
		if (!s_running)
		{
			return null;
		}

		CompositionTarget.Rendering -= OnFrame;
		if (s_content is FrameworkElement root)
		{
			root.LayoutUpdated -= OnLayout;
		}

		s_running = false;
		var end = Stopwatch.GetTimestamp();
		var gcMs = (GC.GetTotalPauseDuration() - s_gcPause).TotalMilliseconds;
		var gen2 = GC.CollectionCount(2) - s_gen2;
		var (extent, offset) = Scroll(s_page);
		var stats = FrameStats.Of([.. s_frames.Select(x => x.At)], s_startedAt, end, Stopwatch.Frequency, [.. s_frames.Select(x => x.Offset)], extent);
		s_frames.Clear();
		s_page = null;
		s_content = null;
		// the pictures still alive once the collector has run, and those in the window (each hears every layout pass: ImageEx's
		// lazy loading)
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		s_images.RemoveAll(x => !x.TryGetTarget(out _));
		var loaded = s_images.Count(x => x.TryGetTarget(out var image) && image.IsLoaded);
		return stats with { EndOffset = offset, Layouts = s_layouts, Images = s_images.Count, ImagesLoaded = loaded, GcPauseMs = gcMs, Gen2 = gen2, WorkingSetMb = Environment.WorkingSet / 1048576 };
	}

	/// <summary>A picture made (LoadingImage's constructor): counted while the hook listens (dev builds under the harness).</summary>
	public static void Track(Controls.LoadingImage image)
	{
		if (s_listens)
		{
			s_images.Add(new WeakReference<Controls.LoadingImage>(image));
		}
	}

	private static void OnLayout(object? sender, object e) => s_layouts++;

	private static void OnFrame(object? sender, object e)
	{
		if (App.IsClosing)
		{
			return;
		}

		s_frames.Add((Stopwatch.GetTimestamp(), Scroll(s_page).Offset));
	}

	// the page's vertical scroll: its scrollable height and where it is (NaN, NaN without one)
	private static (double Scrollable, double Offset) Scroll(FrameworkElement? page) => page switch
	{
		ScrollView view => (view.ScrollableHeight, view.VerticalOffset),
		ScrollViewer viewer => (viewer.ScrollableHeight, viewer.VerticalOffset),
		_ => (double.NaN, double.NaN),
	};

	// the page's scroller: the largest vertically scrollable ScrollView or ScrollViewer shown in the window
	private static FrameworkElement? PageScroller(UIElement content)
	{
		FrameworkElement? best = null;
		var area = 0.0;
		var queue = new Queue<DependencyObject>([content]);
		while (queue.Count > 0)
		{
			var node = queue.Dequeue();
			if (node is UIElement { Visibility: Visibility.Collapsed })
			{
				continue;
			}

			if (node is FrameworkElement element && Scroll(element) is { Scrollable: > 0 } && element.ActualWidth * element.ActualHeight > area)
			{
				best = element;
				area = element.ActualWidth * element.ActualHeight;
			}

			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
			{
				queue.Enqueue(VisualTreeHelper.GetChild(node, i));
			}
		}

		return best;
	}

	/// <summary>The line the stop logs (the harness reads it from the app's log).</summary>
	public static void Log(ILogger log, FrameStats? stats)
	{
		if (stats is not { } s)
		{
			log.LogInformation("[harness] frames: not started");
			return;
		}

		log.LogInformation(
			"[harness] frames: {Frames} in {Ms:0} ms, {Slow} over 50 ms ({SlowMs:0} ms together), longest {Longest:0} ms, p95 {P95:0} ms; page end {Scrollable:0}, at {Offset:0}, reached at {Reached} ms; {Layouts} layout passes; pictures {Images} alive, {Loaded} in the window; the collector paused {GcMs:0} ms ({Gen2} gen 2); working set {Mb} MB",
			s.Frames, s.Ms, s.Slow, s.SlowMs, s.LongestMs, s.P95Ms, s.Scrollable, s.EndOffset, s.ReachedEndMs is { } at ? at.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "never",
			s.Layouts, s.Images, s.ImagesLoaded, s.GcPauseMs, s.Gen2, s.WorkingSetMb);
	}
}

/// <summary>What the frames of a timed stretch were (<see cref="HarnessFrames"/>).</summary>
/// <param name="Frames">The frames drawn.</param>
/// <param name="Ms">The stretch, from the start to the stop.</param>
/// <param name="Slow">The frames that took over <see cref="HarnessFrames.SlowFrameMs"/> (counting the start to the first frame and the last frame to the stop).</param>
/// <param name="SlowMs">How long those took together.</param>
/// <param name="LongestMs">The longest frame.</param>
/// <param name="P95Ms">The 95th percentile of the frames' times.</param>
/// <param name="Scrollable">The page's scrollable height at the stop (NaN: no page scroller).</param>
/// <param name="ReachedEndMs">When the page first reached its end (within a pixel), from the start; null: never.</param>
/// <param name="EndOffset">The page's offset at the stop.</param>
/// <param name="Layouts">The layout passes in the stretch.</param>
/// <param name="Images">The pictures (LoadingImage) alive at the stop.</param>
/// <param name="ImagesLoaded">Those of them in the window.</param>
/// <param name="GcPauseMs">How long the collector paused the app in the stretch.</param>
/// <param name="Gen2">Its gen-2 collections in the stretch.</param>
/// <param name="WorkingSetMb">The app's working set at the stop.</param>
public readonly record struct FrameStats(int Frames, double Ms, int Slow, double SlowMs, double LongestMs, double P95Ms, double Scrollable, double? ReachedEndMs, double EndOffset,
	int Layouts = 0, int Images = 0, int ImagesLoaded = 0, double GcPauseMs = 0, int Gen2 = 0, long WorkingSetMb = 0)
{
	/// <summary>The stats of frames drawn at <paramref name="frames"/> (timestamps) between <paramref name="start"/> and <paramref name="end"/>, the page's offset at each.</summary>
	public static FrameStats Of(IReadOnlyList<long> frames, long start, long end, long frequency, IReadOnlyList<double> offsets, double scrollable)
	{
		var times = new List<double>(frames.Count + 1);
		var previous = start;
		foreach (var at in frames)
		{
			times.Add((at - previous) * 1000.0 / frequency);
			previous = at;
		}

		times.Add((end - previous) * 1000.0 / frequency);
		var slow = times.Where(x => x > HarnessFrames.SlowFrameMs).ToList();
		var sorted = times.OrderBy(x => x).ToList();
		double? reached = null;
		for (var i = 0; i < frames.Count && i < offsets.Count; i++)
		{
			if (scrollable > 0 && offsets[i] >= scrollable - 1)
			{
				reached = (frames[i] - start) * 1000.0 / frequency;
				break;
			}
		}

		return new FrameStats(frames.Count, (end - start) * 1000.0 / frequency, slow.Count, slow.Sum(), sorted.Count > 0 ? sorted[^1] : 0,
			sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)] : 0, scrollable, reached, double.NaN);
	}
}
