using System;
using System.IO;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Journal;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Plugins.Journal.Views;
using FluentFin.UI.Core.Contracts.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluentFin.Plugins.Journal;

/// <summary>
/// fluentfin-mpv: the TV journal (docs/JOURNAL.md) as a FluentFin plugin, so it needs no change to the app's registration,
/// page list or navigation pane: its services, the Diary page, and a "Diary" entry in the main pane. When the main pane is
/// built (after a sign-in) the auto-logger starts listening to the mpv backend and the journal syncs with Jellyfin.
/// </summary>
public sealed class JournalPlugin : IPlugin
{
	public Guid Id { get; } = Guid.Parse("0f6b6c1e-7a52-4c38-9f3e-2b8a4d1c9e57");

	public void ConfigureServices(IServiceCollection services)
	{
		// journal.db in the app's own data folder (KnownFolders: a dev data folder covers it too)
		services.AddSingleton(sp => new JournalStore(Path.Combine(sp.GetRequiredService<KnownFolders>().ApplicationData, JournalStore.FileName), sp.GetService<ILogger<JournalStore>>()));
		services.AddSingleton<IJournalSession>(sp => new SessionInfoJournalSession(sp.GetService<ISettings>()));
		// FluentFin's own client, signed in as the current user (JellyfinClient_Journal.cs)
		services.AddSingleton(sp => (IJournalServer)sp.GetRequiredService<IJellyfinClient>());
		services.AddSingleton(sp => new JournalService(sp.GetRequiredService<JournalStore>(), sp.GetRequiredService<IJournalServer>(), sp.GetRequiredService<IJournalSession>(), sp.GetService<ILogger<JournalService>>()));
		services.AddSingleton(sp => new JournalSync(sp.GetRequiredService<JournalService>(), sp.GetRequiredService<ILogger<JournalSync>>()));
		services.AddSingleton(sp => new JournalAutoLogger(sp.GetRequiredService<IMpvBackend>().Events, sp.GetRequiredService<JournalService>(), sp.GetRequiredService<ILogger<JournalAutoLogger>>()));
		services.AddTransient<DiaryViewModel>();
		services.AddTransient<JournalRatingViewModel>();
	}

	public void ConfigurePages(IPageRegistration pageRegistration)
	{
		pageRegistration.Configure<DiaryViewModel, DiaryPage>();
	}

	public void AddNavigationViewItem(INavigationViewServiceCore navigationViewService)
	{
		// the main pane only (the settings and dashboard panes have a key)
		if (navigationViewService.Key is not null)
		{
			return;
		}

		navigationViewService.AddNavigationItem(new CustomNavigationViewItem
		{
			Key = typeof(DiaryViewModel).FullName!,
			Name = "Diary",
			Glyph = "",
		});

		try
		{
			OnSignedIn(Locator.GetService<JournalAutoLogger>(), Locator.GetService<JournalSync>());
		}
		catch (Exception ex)
		{
			// the journal never stops the app from opening
			try
			{
				Locator.GetService<ILogger<JournalPlugin>>().LogWarning(ex, "Journal: could not start");
			}
			catch (ArgumentException)
			{
			}
		}
	}

	/// <summary>The main pane was built for a signed-in user: listen for finished episodes, and catch up with Jellyfin.</summary>
	public static void OnSignedIn(JournalAutoLogger autoLogger, JournalSync sync)
	{
		autoLogger.Start();
		sync.SyncInBackground("sign-in");
	}
}
