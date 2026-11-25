using System.Diagnostics;
using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace FluentFin.Tests;

/// <summary>
/// 1.7.5 (g4: what the basics gate's classes 6 to 8 found): a film page that couldn't load its film shows only the message,
/// in every theme; and the window's close says each step and, while it lasts, the last one (one close waited 25 s silently).
/// The theme switch's crash is ThemeSwitchTests' and the gate's theme-switch check.
/// </summary>
public sealed class G4FixTests
{
	// ---- a film that couldn't be loaded ----

	[Theory]
	[InlineData(LoadStatus.Failed, false)]
	[InlineData(LoadStatus.Loading, true)]
	[InlineData(LoadStatus.Loaded, true)]
	[InlineData(LoadStatus.Idle, true)]
	public void AnItemPagesContent_IsHidden_OnlyWhileItsItemCouldNotLoad(LoadStatus status, bool shown) =>
		Assert.Equal(shown ? Visibility.Visible : Visibility.Collapsed, ItemRowConverters.ItemContent(status));

	[Theory]
	[InlineData("Views", "MoviePage.xaml")]
	[InlineData("Themes", "Cinematic", "Views", "CinematicMoviePage.xaml")]
	[InlineData("Themes", "DriveIn", "Views", "DriveInMoviePage.xaml")]
	[InlineData("Themes", "Editorial", "Views", "EditorialMoviePage.xaml")]
	public void EveryThemesFilmPage_HidesPlayAndItsOtherContent_BehindCouldntLoad(params string[] path)
	{
		// Editorial's showed Play, Watched, Favourite and an empty Audio picker behind "Couldn't load this movie" (the gate's
		// lifecycle check, offline)
		var page = File.ReadAllText(Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. path]));
		Assert.Contains("<ScrollViewer x:Name=\"PageContent\" Visibility=\"{x:Bind c:ItemRowConverters.ItemContent(ViewModel.State.Status), Mode=OneWay}\">", page);
		Assert.Contains("xmlns:c=\"using:FluentFin.Converters\"", page);
	}

	// ---- the window's close, watched ----

	[Fact]
	public void AClose_SaysEachStep_AndWhileItLasts_TheLastStepReached()
	{
		var log = new LinesLogger();
		var since = Stopwatch.StartNew();
		using (var watch = new CloseWatch(log, since, "closing the window on SettingsPage", TimeSpan.FromMilliseconds(40)))
		{
			watch.Step = "the window's Closed handlers: done";
			Assert.True(SpinWait.SpinUntil(() => log.Lines.Any(l => l.Contains("still closing", StringComparison.Ordinal)), 3000), "a close that lasts says so");
		}

		var lines = log.Lines.ToArray();
		Assert.StartsWith("[close] closing the window on SettingsPage after ", lines[0], StringComparison.Ordinal);
		Assert.Contains(lines, l => l.StartsWith("[close] the window's Closed handlers: done after ", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.StartsWith("[close] the window is still closing after ", StringComparison.Ordinal) && l.EndsWith("; the last step: the window's Closed handlers: done", StringComparison.Ordinal));

		// once the close is over, nothing more
		var count = log.Lines.Count;
		Thread.Sleep(200);
		Assert.Equal(count, log.Lines.Count);
	}

	[Fact]
	public void TheStillClosingLine_NamesTheTimeAndTheStep() =>
		Assert.Equal("[close] the window is still closing after 6000 ms; the last step: closing the window on HomePage", CloseWatch.StillClosingLine(6000, "closing the window on HomePage"));

	[Fact]
	public void TheAppsClose_WatchesTheWindowsClose()
	{
		var app = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "App.xaml.cs"));
		// 1.7.5 (the review's Minor): set up in a try of its own before the close, and disposed after it (CloseWatchSafetyTests)
		var watched = app.IndexOf("watch = WatchWindowClose(log);", StringComparison.Ordinal);
		var close = watched > 0 ? app.IndexOf("MainWindow.Close();", watched, StringComparison.Ordinal) : -1;
		Assert.True(watched > 0 && close > watched && app.IndexOf("watch?.Dispose();", close, StringComparison.Ordinal) > close);
	}

	private sealed class LinesLogger : ILogger
	{
		public System.Collections.Concurrent.ConcurrentQueue<string> Queue { get; } = new();

		public IReadOnlyCollection<string> Lines => Queue;

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
			Queue.Enqueue(formatter(state, exception));
	}
}
