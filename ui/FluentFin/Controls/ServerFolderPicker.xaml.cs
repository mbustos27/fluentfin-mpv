using System.Collections.ObjectModel;
using CommunityToolkit.WinUI;
using DevWinUI;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>1.1.4 (states): where the folder picker is with the folder it shows.</summary>
public enum ServerFolderStatus
{
	Loading,
	Loaded,
	/// <summary>No folders came back: an empty folder, or one the server could not read (the client answers both with none).</summary>
	Empty,
	/// <summary>Asking the server failed.</summary>
	Failed,
}

/// <summary>1.1.4 (states): the folder picker's paths and words, without WinUI (tested in FluentFin.Tests).</summary>
public static class ServerFolders
{
	/// <summary>The folder above <paramref name="path"/> ("/media/movies" -> "/media", "/media" -> "/"), or null at the top.</summary>
	public static string? ParentOf(string? path)
	{
		var parts = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
		return parts.Length == 0 ? null : "/" + string.Join('/', parts.Take(parts.Length - 1));
	}

	public static string Title(ServerFolderStatus status) => status switch
	{
		ServerFolderStatus.Empty => "No folders here",
		ServerFolderStatus.Failed => "Couldn't read this folder",
		_ => "",
	};

	public static string Message(ServerFolderStatus status) => status switch
	{
		ServerFolderStatus.Empty => "This folder has no folders in it, or the server can't read it. Select this folder, go up, or refresh.",
		ServerFolderStatus.Failed => "The server didn't list this folder. Check that it is running, then try again.",
		_ => "",
	};

	/// <summary>1.5 (the button review's S19): "Refresh" for a folder that came back empty (nothing failed), else "Try again".</summary>
	public static string RetryLabel(ServerFolderStatus status) => status == ServerFolderStatus.Empty ? "Refresh" : "Try again";

	/// <summary>
	/// 1.5 (the button review's S20): Select this folder works only on a folder the server listed: not while it is being
	/// listed, nor when listing it failed (an empty folder can be chosen: its message says so).
	/// </summary>
	public static bool CanSelect(string? path, ServerFolderStatus status) =>
		!string.IsNullOrEmpty(path) && status is not (ServerFolderStatus.Loading or ServerFolderStatus.Failed);

	/// <summary>What Narrator hears for the path line ("Current folder: /media/movies").</summary>
	public static string PathLabel(string? path) => string.IsNullOrEmpty(path) ? "No folder" : $"Current folder: {path}";
}

public sealed partial class ServerFolderPicker : UserControl
{
	private readonly IJellyfinClient _jellyfinClient = App.GetService<IJellyfinClient>();
	private int _run;

	[GeneratedDependencyProperty(DefaultValue = "/")]
	public partial string CurrentFolder { get; set; }

	[GeneratedDependencyProperty(DefaultValue = ServerFolderStatus.Loading)]
	public partial ServerFolderStatus Status { get; set; }

	/// <summary>The ring's bool (a ring is never turned on through an x:Bind function: BindingXamlTests).</summary>
	[GeneratedDependencyProperty(DefaultValue = true)]
	public partial bool IsLoadingFolders { get; set; }

	partial void OnStatusChanged(ServerFolderStatus newValue) => IsLoadingFolders = newValue == ServerFolderStatus.Loading;

	/// <summary>The folder chosen with Select this folder; null when the picker was cancelled or closed.</summary>
	public string? SelectedFolder { get; private set; }

	public Action? CloseWindow { get; set; }

	public ServerFolderPicker()
	{
		InitializeComponent();

		Loaded += ServerFolderPicker_Loaded;
	}

	public ObservableCollection<FileSystemEntryInfo> Folders { get; } = [];

	private async void ServerFolderPicker_Loaded(object sender, RoutedEventArgs e)
	{
		await Update(CurrentFolder);
		FocusFirst();
	}

	private async void ItemsView_ItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
	{
		if (args.InvokedItem is not FileSystemEntryInfo { Path: not null } info)
		{
			return;
		}

		await Update(info.Path);
		FocusFirst();
	}

	/// <summary>Lists <paramref name="path"/>'s folders; never throws (an async void handler that throws ends the app).</summary>
	private async Task Update(string path)
	{
		var run = ++_run;
		Status = ServerFolderStatus.Loading;
		List<FileSystemEntryInfo> folders;
		try
		{
			folders = await _jellyfinClient.GetDirectoryContents(path);
		}
		catch (Exception)
		{
			if (run == _run)
			{
				CurrentFolder = path;
				Folders.Clear();
				Show(ServerFolderStatus.Failed);
			}

			return;
		}

		if (run != _run)
		{
			return;
		}

		CurrentFolder = path;
		Folders.Clear();
		Folders.AddRange(folders);
		Show(folders.Count == 0 ? ServerFolderStatus.Empty : ServerFolderStatus.Loaded);
	}

	/// <summary>Sets the status; a message is announced (a polite live region) when it appears.</summary>
	private void Show(ServerFolderStatus status)
	{
		Status = status;
		if (status is ServerFolderStatus.Empty or ServerFolderStatus.Failed
			&& (FrameworkElementAutomationPeer.FromElement(MessageTitle) ?? FrameworkElementAutomationPeer.CreatePeerForElement(MessageTitle)) is { } peer)
		{
			peer.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent,
				$"{ServerFolders.Title(status)}. {ServerFolders.Message(status)}", "ServerFolderPicker");
		}
	}

	/// <summary>The keyboard on the first folder, else on Try again or Select this folder.</summary>
	private void FocusFirst()
	{
		DispatcherQueue.TryEnqueue(() =>
		{
			if (Folders.Count > 0)
			{
				FolderList.StartBringItemIntoView(0, new BringIntoViewOptions());
				FolderList.Focus(FocusState.Programmatic);
			}
			else if (Status == ServerFolderStatus.Failed)
			{
				RetryButton.Focus(FocusState.Programmatic);
			}
			else
			{
				SelectButton.Focus(FocusState.Programmatic);
			}
		});
	}

	private async void Button_Click(object sender, RoutedEventArgs e)
	{
		if (ServerFolders.ParentOf(CurrentFolder) is not { } parent)
		{
			return;
		}

		await Update(parent);
		FocusFirst();
	}

	private async void Retry_Click(object sender, RoutedEventArgs e)
	{
		await Update(CurrentFolder);
		FocusFirst();
	}

	private void OnCloseWindow(object sender, RoutedEventArgs e)
	{
		SelectedFolder = CurrentFolder;
		CloseWindow?.Invoke();
	}

	// 1.1.4 (states): Cancel closes the window and picks nothing (it emptied the path and left the window open)
	private void OnCancel(object sender, RoutedEventArgs e)
	{
		SelectedFolder = null;
		CloseWindow?.Invoke();
	}

	public bool CanGoUp(string path) => ServerFolders.ParentOf(path) is not null;

	public bool CanSelect(string path, ServerFolderStatus status) => ServerFolders.CanSelect(path, status);

	public string RetryLabel(ServerFolderStatus status) => ServerFolders.RetryLabel(status);

	public string PathLabel(string path) => ServerFolders.PathLabel(path);

	public Visibility MessageVisibility(ServerFolderStatus status) => status is ServerFolderStatus.Empty or ServerFolderStatus.Failed ? Visibility.Visible : Visibility.Collapsed;

	public Visibility RetryVisibility(ServerFolderStatus status) => MessageVisibility(status);

	public string MessageTitleOf(ServerFolderStatus status) => ServerFolders.Title(status);

	public string MessageOf(ServerFolderStatus status) => ServerFolders.Message(status);

}
