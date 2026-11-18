using FluentFin.Core.ViewModels;
using FluentFin.Dialogs.ViewModels;

namespace FluentFin.Core.Admin;

/// <summary>
/// Nixie 1.2.0 (admin): the server administration's sections, as tabs along the top of the page (no side pane: the user
/// hates side bars). A section is one page, or (Libraries, Playback) a few pages shown as a second row of tabs. Keys are the
/// view models' full names, which the Settings frame navigates to. A page opened from a section (a user's settings, from
/// Users) keeps its section's tab selected and offers a way back to it.
/// </summary>
public static class AdminSections
{
	public static IReadOnlyList<AdminSection> All { get; } =
	[
		new("Dashboard", [new("Dashboard", Key<DashboardViewModel>())]),
		new("General", [new("General", Key<GeneralSettingsViewModel>())]),
		new("Users", [new("Users", Key<UsersViewModel>())], subPages: [Key<UserEditorViewModel>()]),
		new("Libraries",
		[
			new("Library folders", Key<LibrariesSettingsViewModel>()),
			new("Display", Key<LibrariesDisplayViewModel>()),
			new("Metadata", Key<LibrariesMetadataViewModel>()),
			new("NFO settings", Key<LibrariesNfoSettingsViewModel>()),
		]),
		new("Playback",
		[
			new("Transcoding", Key<PlaybackTranscodingViewModel>()),
			new("Resume and streaming", Key<PlaybackResumeViewModel>()),
			new("Trickplay", Key<PlaybackTrickplayViewModel>()),
		]),
		new("Activity", [new("Activity", Key<ActivitiesViewModel>())]),
		new("Scheduled tasks", [new("Scheduled tasks", Key<ScheduledTasksViewModel>())]),
	];

	/// <summary>Where a page belongs: its section, the page tab (null for a sub-page) and whether it is a sub-page; null for a page of no section.</summary>
	public static AdminPlace? Find(string? viewModelKey)
	{
		if (string.IsNullOrEmpty(viewModelKey))
		{
			return null;
		}

		foreach (var section in All)
		{
			if (section.Pages.FirstOrDefault(x => x.Key == viewModelKey) is { } page)
			{
				return new AdminPlace(section, page, IsSubPage: false);
			}

			if (section.SubPages.Contains(viewModelKey))
			{
				return new AdminPlace(section, null, IsSubPage: true);
			}
		}

		return null;
	}

	private static string Key<T>() => typeof(T).FullName!;
}

/// <summary>A section: one page (the section is that page), or more (a second row of tabs; the first opens with the section).</summary>
public sealed class AdminSection(string name, IReadOnlyList<AdminPage> pages, IReadOnlyList<string>? subPages = null)
{
	public string Name { get; } = name;

	public IReadOnlyList<AdminPage> Pages { get; } = pages;

	/// <summary>Pages opened from the section's page (they show "Back to {Name}").</summary>
	public IReadOnlyList<string> SubPages { get; } = subPages ?? [];

	public bool HasPageTabs => Pages.Count > 1;

	/// <summary>Where the section's tab goes.</summary>
	public string Key => Pages[0].Key;
}

public sealed record AdminPage(string Name, string Key);

public sealed record AdminPlace(AdminSection Section, AdminPage? Page, bool IsSubPage);
