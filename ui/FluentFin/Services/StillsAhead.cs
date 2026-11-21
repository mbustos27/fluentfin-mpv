using System.Runtime.InteropServices;
using FluentFin.Core;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.5 (season speed): the stills of the seasons either side of the one shown, asked ahead into WinINet's cache, where
/// WinUI's picture loader finds them. WinUI starts a picture's download only once its row is laid out and drawn, so a
/// season's stills always came after its rows (0.2 to 0.3 s with 6 at a time, 0.5 to 1.1 s with 2); a season asked ahead
/// (<see cref="SeriesEpisodes.NextToShownKept"/>) now has them in before its click. Only the page shown says which picture
/// its rows ask for (<see cref="Follow"/>: the theme's width and kind; Editorial's depend on its layout, so it asks none);
/// a second after the season's list is in (the shown season's own stills first), at most <see cref="AtOnce"/> at a time,
/// each picture once a session; nothing more once the page is left, and a failure stops the rest of that season.
/// </summary>
internal static class StillsAhead
{
	/// <summary>Downloads at once: WinINet's 6 connections to the server stay mostly the shown pictures'.</summary>
	public const int AtOnce = 2;

	/// <summary>The most stills asked of one season (a long season's further rows wait for their turn on screen).</summary>
	public const int MostPerSeason = 24;

	/// <summary>The pictures remembered as asked (a session of browsing; the oldest are forgotten past it).</summary>
	public const int MostRemembered = 4000;

	/// <summary>How long after a season's list is in its stills are asked: the shown season's own pictures go first.</summary>
	public static readonly TimeSpan After = TimeSpan.FromSeconds(1);

	private static readonly Lock s_lock = new();
	private static readonly HashSet<string> s_asked = [];
	private static readonly Queue<string> s_order = new();
	private static readonly SemaphoreSlim s_gate = new(AtOnce);
	private static (Page Page, Func<BaseItemDto, string?> Still)? s_shown;
	private static bool s_listening;

	/// <summary>
	/// While <paramref name="page"/> is shown, the seasons asked ahead get their stills asked too, as its rows ask them:
	/// <paramref name="still"/> is a row's picture path on the server for an episode (null: none).
	/// </summary>
	public static void Follow(Page page, Func<BaseItemDto, string?> still)
	{
		page.Loaded += (_, _) =>
		{
			lock (s_lock)
			{
				s_shown = (page, still);
				if (!s_listening)
				{
					s_listening = true;
					App.GetService<SeriesEpisodes>().NextToShownKept += OnNextToShownKept;
				}
			}
		};
		page.Unloaded += (_, _) =>
		{
			lock (s_lock)
			{
				if (s_shown?.Page == page)
				{
					s_shown = null;
				}
			}
		};
	}

	private static void OnNextToShownKept(object? sender, IReadOnlyList<BaseItemDto> episodes)
	{
		List<string> urls;
		Page page;
		lock (s_lock)
		{
			if (s_shown is not { } shown || string.IsNullOrEmpty(SessionInfo.BaseUrl))
			{
				return;
			}

			page = shown.Page;
			urls = Plan(episodes, shown.Still, SessionInfo.BaseUrl, s_asked, s_order, MostPerSeason, MostRemembered);
		}

		if (urls.Count > 0)
		{
			_ = Ask(urls, page);
		}
	}

	// whether the page that asked is still the one shown (review m4: a page left asks nothing more)
	private static bool Shown(Page page)
	{
		lock (s_lock)
		{
			return s_shown?.Page == page;
		}
	}

	/// <summary>
	/// The pictures to ask for <paramref name="episodes"/>: the first <paramref name="most"/> that have one and were not asked
	/// before, remembered as asked (<paramref name="asked"/>, the oldest forgotten past <paramref name="remembered"/>).
	/// </summary>
	internal static List<string> Plan(IEnumerable<BaseItemDto> episodes, Func<BaseItemDto, string?> still, string baseUrl, HashSet<string> asked, Queue<string> order, int most, int remembered)
	{
		var urls = new List<string>();
		foreach (var episode in episodes.Take(most))
		{
			if (still(episode) is not { Length: > 0 } path)
			{
				continue;
			}

			var url = baseUrl.TrimEnd('/') + path;
			if (!asked.Add(url))
			{
				continue;
			}

			order.Enqueue(url);
			urls.Add(url);
			while (order.Count > remembered && order.TryDequeue(out var oldest))
			{
				asked.Remove(oldest);
			}
		}

		return urls;
	}

	private static Task Ask(List<string> urls, Page page) =>
		Ask(urls, () => Shown(page), Download, After, Forget);

	/// <summary>
	/// Asks <paramref name="urls"/> into the cache, <see cref="AtOnce"/> at a time, <paramref name="after"/> from now: stopped by
	/// the page being left (<paramref name="shown"/> false; review m4) or by a failure (a server that refuses or does not answer:
	/// each would wait as long). <paramref name="forget"/> hears the pictures not cached, to be asked the next time their season
	/// is asked ahead: one that failed, and those not asked (review m4: not the ones already in the cache).
	/// </summary>
	internal static async Task Ask(IReadOnlyList<string> urls, Func<bool> shown, Func<string, bool> download, TimeSpan after, Action<IEnumerable<string>> forget)
	{
		await Task.Delay(after).ConfigureAwait(false);
		var failed = 0;
		var running = new List<Task>(urls.Count);
		for (var i = 0; i < urls.Count; i++)
		{
			await s_gate.WaitAsync().ConfigureAwait(false);
			if (Volatile.Read(ref failed) > 0 || !shown())
			{
				s_gate.Release();
				forget(urls.Skip(i).ToList());
				break;
			}

			var url = urls[i];
			running.Add(Task.Run(() =>
			{
				try
				{
					if (!download(url))
					{
						Interlocked.Increment(ref failed);
						forget([url]);
					}
				}
				catch (Exception)
				{
					Interlocked.Increment(ref failed);
					forget([url]);
				}
				finally
				{
					s_gate.Release();
				}
			}));
		}

		await Task.WhenAll(running).ConfigureAwait(false);
	}

	// into WinINet's cache (as WinUI's own loader would, no token: a picture needs none); false when it failed (not cached: the
	// row asks for it when it is drawn)
	private static bool Download(string url)
	{
		var file = new System.Text.StringBuilder(1024);
		return URLDownloadToCacheFile(IntPtr.Zero, url, file, file.Capacity, 0, IntPtr.Zero) == 0;
	}

	// pictures not asked after all, or that failed (review m4: only those, not the ones in the cache already): asked again
	// the next time their season is asked ahead
	private static void Forget(IEnumerable<string> urls)
	{
		lock (s_lock)
		{
			foreach (var url in urls)
			{
				s_asked.Remove(url);
			}
		}
	}

	[DllImport("urlmon.dll", EntryPoint = "URLDownloadToCacheFileW", CharSet = CharSet.Unicode)]
	private static extern int URLDownloadToCacheFile(IntPtr caller, string url, System.Text.StringBuilder fileName, int length, int reserved, IntPtr callback);
}
