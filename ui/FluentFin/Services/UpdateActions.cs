using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Updates;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.5: what the updater's buttons do, the same in the shell's notice and in Settings (<see cref="UpdateAction"/>), the
/// page to come back to after the update, and the release notes ("What's new") in a dialog.
/// </summary>
public static class UpdateActions
{
	public static async Task RunAsync(UpdateAction action, XamlRoot? root)
	{
		var updates = App.GetService<UpdateNoticeService>();
		var status = updates.Center.Status;
		switch (action)
		{
			case UpdateAction.Download:
				_ = updates.Center.StartDownload();
				break;
			case UpdateAction.Retry when status.Phase == UpdatePhase.InstallFailed:
			case UpdateAction.RestartToUpdate:
				updates.RestartToUpdate(CurrentPlace());
				break;
			case UpdateAction.Retry:
				_ = updates.Center.StartDownload();
				break;
			case UpdateAction.RetryCheck:
				await updates.Center.CheckNowAsync();
				break;
			case UpdateAction.Cancel:
				updates.Center.Cancel();
				break;
			case UpdateAction.OpenReleasePage when status.Release is { } release:
				UpdateNoticeService.OpenPage(release.PageUrl);
				break;
			case UpdateAction.OpenReleasePage:
				UpdateNoticeService.OpenPage(UpdateSource.ReleasesPage + "/latest");
				break;
			case UpdateAction.ReleaseNotes when status.Release is { } release:
				await ShowNotesAsync(root, $"What's new in {AppIdentity.Name} {release.Version}", release.Notes, release.PageUrl);
				break;
			case UpdateAction.WhatsNew when status.JustUpdated is { } done:
				await ShowNotesAsync(root, $"What's new in {AppIdentity.Name} {done.Version}", done.Notes, done.PageUrl);
				updates.Center.ForgetJustUpdated();
				break;
			case UpdateAction.Dismiss when status.JustUpdated is not null:
				updates.Center.ForgetJustUpdated();
				break;
			case UpdateAction.Dismiss:
				updates.Coordinator.Dismiss();
				updates.Center.Dismissed();   // review m2: not downloaded or installed by itself any more
				break;
		}
	}

	// ---------- where you were

	/// <summary>The page shown now: its view model, and the item for an item's page (Series, Season, Movie, Episode).</summary>
	public static ResumePlace? CurrentPlace()
	{
		try
		{
			var vm = App.GetService<INavigationService>().Frame?.GetPageViewModel();
			if (vm?.GetType().FullName is not { } key)
			{
				return null;
			}

			var item = vm.GetType().GetProperty("Dto")?.GetValue(vm) as BaseItemDto;
			return new ResumePlace(key, item?.Id);
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Reflection.TargetException)
		{
			return null;
		}
	}

	/// <summary>Pages that open with no parameter, so they can be opened again by their key alone.</summary>
	private static readonly string[] s_plainPages = [typeof(SettingsViewModel).FullName!, typeof(HomeViewModel).FullName!];

	/// <summary>
	/// After an update: back to the page Nixie was on (an item's page by its item, Settings or Home by their key); anything else
	/// stays on the first page. Silent when the item is gone.
	/// </summary>
	public static async Task ResumeAsync(ResumePlace place)
	{
		try
		{
			if (place.ItemId is { } id && id != Guid.Empty)
			{
				if (await App.GetService<IJellyfinClient>().GetItem(id) is { } item)
				{
					App.Commands.DisplayDto(item);
				}
			}
			else if (s_plainPages.Contains(place.PageKey))
			{
				App.GetService<INavigationService>().NavigateTo(place.PageKey);
			}
		}
		catch (Exception)
		{
			// the place is a convenience: the first page is fine
		}
	}

	// ---------- What's new

	/// <summary>The release notes in a dialog: headings, bullets, paragraphs; links open in the browser only on a click.</summary>
	public static async Task ShowNotesAsync(XamlRoot? root, string title, string notes, string pageUrl)
	{
		if (root is null)
		{
			return;
		}

		var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 560 };
		var blocks = ReleaseNotes.Parse(notes);
		foreach (var block in blocks)
		{
			var top = text.Blocks.Count == 0 ? 0 : block.Kind == NotesBlockKind.Heading ? 12 : 4;
			var paragraph = new Paragraph { Margin = new Thickness(block.Kind == NotesBlockKind.Bullet ? 16 + (block.Level * 16) : 0, top, 0, 0) };
			if (block.Kind == NotesBlockKind.Bullet)
			{
				paragraph.Inlines.Add(new Run { Text = "•  " });
			}

			foreach (var inline in block.Inlines)
			{
				paragraph.Inlines.Add(InlineFor(inline, block.Kind == NotesBlockKind.Heading));
			}

			if (block.Kind == NotesBlockKind.Heading)
			{
				paragraph.FontSize = block.Level <= 2 ? 18 : 15;
				paragraph.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
			}

			text.Blocks.Add(paragraph);
		}

		if (blocks.Count == 0)
		{
			text.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "This release has no notes." } } });
		}

		var page = new HyperlinkButton { Content = "Open the release page", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0) };
		page.Click += (_, _) => UpdateNoticeService.OpenPage(pageUrl);
		var body = new StackPanel { Children = { text, page } };
		var dialog = new ContentDialog
		{
			Title = title,
			Content = new ScrollViewer { Content = body, MaxHeight = 480, Padding = new Thickness(0, 0, 12, 0) },
			CloseButtonText = "Close",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = root,
		};
		if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("NixieDialogStyle", out var style) && style is Style dialogStyle)
		{
			dialog.Style = dialogStyle;
		}

		if (root.Content is FrameworkElement content)
		{
			dialog.RequestedTheme = content.ActualTheme;
		}

		Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, title);
		// the keyboard starts on Close, not on the first link in the notes (Enter there would open the browser)
		// the dialog's own first focus goes to the first focusable element, a link of the notes: it is turned to Close as it
		// happens (GettingFocus); the focus is drawn. Nixie 1.6.1 (bug hunt BH-160-m3): by what the user did, not by the
		// time: every move no pointer made is turned to Close until the user presses a key in the dialog (none of those moves
		// can be theirs). The 800 ms window lost its race on a session's first dialog, whose first focus came late (Classic
		// and Drive-in started on the notes' first link). Tab and clicks after that go where the user sends them.
		// and the links are no tab stops until then, so the dialog's own first focus has nothing in the notes to land on: on a
		// second opening the turn to Close did not take (Classic and Drive-in), and the keyboard started on "details" again
		var links = NotesLinks(text).ToList();
		HoldLinks(links, page, held: true);
		var keyed = false;
		dialog.PreviewKeyDown += (_, args) =>
		{
			if (!keyed && !args.KeyStatus.WasKeyDown)
			{
				keyed = true;
				HoldLinks(links, page, held: false); // before the key's own move: Tab goes on to the first link
			}
		};
		dialog.GettingFocus += (_, args) =>
		{
			if (TurnsToClose(keyed, args.InputDevice) && FindNamed<Button>(dialog, "CloseButton") is { } close
				&& !ReferenceEquals(args.NewFocusedElement, close))
			{
				args.TrySetNewFocusedElement(close);
			}
		};
		dialog.Opened += (_, _) => dialog.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (!keyed && FindNamed<Button>(dialog, "CloseButton") is { } close && close.FocusState == FocusState.Unfocused)
			{
				close.Focus(FocusState.Keyboard);
			}
		});
		try
		{
			await dialog.ShowAsync();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
		{
			// another dialog is open: the notes wait for the next click
		}
	}

	/// <summary>Nixie 1.6.1 (BH-160-m3): the notes' links (each Hyperlink in their paragraphs, in a span too).</summary>
	internal static IEnumerable<Hyperlink> NotesLinks(RichTextBlock text)
	{
		static IEnumerable<Hyperlink> In(IEnumerable<Inline> inlines)
		{
			foreach (var inline in inlines)
			{
				if (inline is Hyperlink link)
				{
					yield return link;
				}

				if (inline is Span span)
				{
					foreach (var inner in In(span.Inlines))
					{
						yield return inner;
					}
				}
			}
		}

		return text.Blocks.OfType<Paragraph>().SelectMany(p => In(p.Inlines));
	}

	/// <summary>
	/// Nixie 1.6.1 (BH-160-m3): the notes' links and the release page's are tab stops only once the user pressed a key in the
	/// dialog. A click and Narrator reach them all the time.
	/// </summary>
	private static void HoldLinks(IEnumerable<Hyperlink> links, Control page, bool held)
	{
		foreach (var link in links)
		{
			link.IsTabStop = !held;
		}

		page.IsTabStop = !held;
	}

	/// <summary>
	/// Nixie 1.6.1 (BH-160-m3): a focus move in the notes that goes to Close instead: one no pointer made, before the user
	/// pressed any key in the dialog (so it is the dialog's own, however late it comes).
	/// </summary>
	internal static bool TurnsToClose(bool userPressedAKey, FocusInputDeviceKind device) =>
		!userPressedAKey && device is not (FocusInputDeviceKind.Mouse or FocusInputDeviceKind.Touch or FocusInputDeviceKind.Pen);

	/// <summary>A named element of <paramref name="root"/>'s visual tree (the dialog's own Close button).</summary>
	private static T? FindNamed<T>(DependencyObject root, string name)
		where T : FrameworkElement
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is T element && element.Name == name)
			{
				return element;
			}

			if (FindNamed<T>(child, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private static Inline InlineFor(NotesInline inline, bool heading)
	{
		if (inline.Link is { } url)
		{
			var link = new Hyperlink();
			link.Inlines.Add(new Run { Text = inline.Text });
			Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, inline.Text);
			link.Click += (_, _) => OpenLink(url);
			return link;
		}

		var run = new Run { Text = inline.Text };
		if (inline.Bold && !heading)
		{
			run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
		}

		if (inline.Code)
		{
			run.FontFamily = new FontFamily("Cascadia Mono, Consolas");
		}

		return run;
	}

	/// <summary>A link from the notes, only on a click, only http(s) (<see cref="ReleaseNotes.SafeLink"/>), in the browser.</summary>
	private static void OpenLink(string url)
	{
		if (ReleaseNotes.SafeLink(url) is { } safe)
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(safe) { UseShellExecute = true });
		}
	}
}
