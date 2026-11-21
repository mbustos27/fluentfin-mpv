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
		// happens (GettingFocus), whatever the timing; the focus is drawn
		// (turned: a move no key or pointer made, and, as the dialog opens, its own first move whatever it says it came from;
		// Tab and clicks after that go where the user sends them)
		var shown = System.Diagnostics.Stopwatch.StartNew();
		dialog.GettingFocus += (_, args) =>
		{
			var opening = shown.ElapsedMilliseconds < 800 && args.InputDevice is not (FocusInputDeviceKind.Mouse or FocusInputDeviceKind.Touch or FocusInputDeviceKind.Pen);
			if ((args.InputDevice == FocusInputDeviceKind.None || opening) && FindNamed<Button>(dialog, "CloseButton") is { } close
				&& !ReferenceEquals(args.NewFocusedElement, close))
			{
				args.TrySetNewFocusedElement(close);
			}
		};
		dialog.Opened += (_, _) => dialog.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (FindNamed<Button>(dialog, "CloseButton") is { } close && close.FocusState == FocusState.Unfocused)
			{
				close.Focus(FocusState.Keyboard);
			}
		});
		try
		{
			shown.Restart();
			await dialog.ShowAsync();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
		{
			// another dialog is open: the notes wait for the next click
		}
	}

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
