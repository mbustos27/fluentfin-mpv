using System.Diagnostics;
using FluentFin.Core;
using FluentFin.Core.Settings;
using FluentFin.Core.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: the update notice (Settings > Check for updates; Nixie 1.3.1: on by default). A timer ticks every minute, the first
/// tick about 20 s after the start; the work is in <see cref="UpdateCoordinator"/> (nothing goes out while the setting is off;
/// with it on, one request when the app opens and once a day after). It only tells: nothing is downloaded or installed before
/// the user's click.
/// The notice is an InfoBar in the shell (ShellPage). This replaces upstream FluentFin's WindowsUpdateService, which fetched
/// upstream's installer; nothing here can name any release but this repository's (<see cref="UpdateSource"/>).
/// </summary>
public sealed class UpdateNoticeService : IHostedService, IDisposable
{
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
	private readonly ILogger<UpdateNoticeService> _logger;
	private readonly string _downloadFolder = Path.Combine(Path.GetTempPath(), $"{AppIdentity.Name}-update");
	private CancellationTokenSource? _stop;
	private Task? _loop;

	public UpdateNoticeService(ISettings settings, ILogger<UpdateNoticeService> logger)
	{
		_logger = logger;
		// Nixie 1.3.1: the harness's dev runs ask its fake server (NIXIE_UPDATE_API), never GitHub; a release always asks GitHub
		var api = UpdateSource.ApiFor(AppIdentity.ReadEnvironment(UpdateSource.ApiVariable),
			harnessOn: Environment.GetEnvironmentVariable(GlassActivity.AlwaysActiveVariable) == "1", releasePackage: AppVersion.IsReleasePackage);
		Coordinator = new UpdateCoordinator(settings, new UpdateChecker(Http, AppVersion.Current, api), () => DateTimeOffset.UtcNow, AppVersion.Current);
		Coordinator.Checked += result => _logger.LogInformation("[update] checked {Host}: {Status}{Version}{Reason}", api.Host, result.Status,
			result.Release is { } r ? $" {r.Version}" : "", result.Reason is { } why ? $" ({why})" : "");
	}

	public UpdateCoordinator Coordinator { get; }

	/// <summary>Whether this copy was installed by the MSI (it can update itself) rather than unzipped (portable).</summary>
	public static bool IsMsiInstall()
	{
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(UpdateSource.RegistryKey);
			return InstallDetector.IsMsiInstall(AppContext.BaseDirectory, key?.GetValue("InstallFolder") as string);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>"Download and install": an installed copy downloads the installer, checks it and runs it; a portable one opens the release page.</summary>
	public async Task InstallAsync(ReleaseInfo release, Action? beforeClose = null)
	{
		try
		{
			var downloader = new UpdateDownloader(Http, _downloadFolder);
			await UpdateFlow.InstallAsync(release, IsMsiInstall(), downloader.DownloadAsync,
				info => Process.Start(info), OpenPage, () => CloseForUpdate(beforeClose)).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			// a failed or mismatched download is not an update: the release page is the way out
			_logger.LogWarning("Update download failed: {Message}", ex.Message);
			OpenPage(release.PageUrl);
		}
	}

	/// <summary>
	/// Once the installer is running: tell the user (the bar), then close the window, which runs the app's own clean close (the
	/// stop report, the backend and its mpv window, the settings flushed) so the installer finds no file locked.
	/// </summary>
	private static void CloseForUpdate(Action? beforeClose)
	{
		var window = App.MainWindow;
		window.DispatcherQueue.TryEnqueue(() =>
		{
			beforeClose?.Invoke();
			window.Close();
		});
	}

	public static void OpenPage(string url)
	{
		if (UpdateSource.IsReleasePage(url))
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_stop = new CancellationTokenSource();
		_loop = Task.Run(() => RunAsync(_stop.Token));
		return Task.CompletedTask;
	}

	private async Task RunAsync(CancellationToken token)
	{
		try
		{
			// not while the app starts: the first tick comes after the window is up
			await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
			do
			{
				try
				{
					await Coordinator.TickAsync(token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					_logger.LogDebug("Update check: {Message}", ex.Message);   // silent for the user
				}
			}
			while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
		}
		catch (OperationCanceledException)
		{
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		_stop?.Cancel();
		if (_loop is not null)
		{
			await Task.WhenAny(_loop, Task.Delay(1000, cancellationToken)).ConfigureAwait(false);
		}
	}

	public void Dispose() => _stop?.Dispose();
}
