using FluentFin.Core.Settings;
using FluentFin.Core.Updates;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.5: Settings > Updates (UpdateSettingsPanel.xaml). It shows <see cref="UpdateView.ForSettings"/> of the updater's one
/// state, the same one the shell's notice shows, so a download started in either shows its progress in both.
/// </summary>
public sealed partial class UpdateSettingsPanel : UserControl
{
	private readonly ISettings _settings = App.GetService<ISettings>();
	private UpdateView? _view;
	private string? _announced;

	public UpdateSettingsPanel()
	{
		InitializeComponent();
		Loaded += (_, _) =>
		{
			Updates.Center.Changed += OnChanged;
			_announced = null;
			Show(announce: false);
		};
		Unloaded += (_, _) => Updates.Center.Changed -= OnChanged;
	}

	public UpdateNoticeService Updates { get; } = App.GetService<UpdateNoticeService>();

	/// <summary>Settings > Check for updates (on by default).</summary>
	public bool CheckForUpdates
	{
		get => _settings.CheckForUpdates;
		set => _settings.CheckForUpdates = value;
	}

	private void OnChanged(UpdateStatus status) => DispatcherQueue.TryEnqueue(() => Show(announce: true));

	private void Show(bool announce)
	{
		var status = Updates.Center.Status;
		var view = UpdateView.ForSettings(status, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
		_view = view;
		UpdateStatusTitle.Text = view.Title;
		UpdateStatusDetail.Text = view.Detail ?? "";
		UpdateStatusDetail.Visibility = view.Detail is null ? Visibility.Collapsed : Visibility.Visible;
		var busyRing = view.Busy && view.Progress is null && status.Phase != UpdatePhase.Downloading;
		UpdateStatusRing.IsActive = busyRing;
		UpdateStatusRing.Visibility = busyRing ? Visibility.Visible : Visibility.Collapsed;
		UpdateStatusIcon.Visibility = busyRing ? Visibility.Collapsed : Visibility.Visible;
		UpdateStatusIcon.Glyph = view.IsError ? "" : status.Phase is UpdatePhase.UpToDate ? "" : "";
		UpdateStatusProgress.Visibility = status.Phase == UpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
		UpdateStatusProgress.IsIndeterminate = view.Progress is null && status.Phase == UpdatePhase.Downloading;
		UpdateStatusProgress.Value = (view.Progress ?? 0) * 100;
		UpdatePrimaryButton.Visibility = view.Primary == UpdateAction.None ? Visibility.Collapsed : Visibility.Visible;
		UpdatePrimaryButton.Content = UpdateView.Label(view.Primary);
		UpdateCancelButton.Visibility = view.Others.Contains(UpdateAction.Cancel) ? Visibility.Visible : Visibility.Collapsed;
		var link = view.Others.FirstOrDefault(a => a is UpdateAction.ReleaseNotes or UpdateAction.OpenReleasePage);

		UpdateLinkButton.Visibility = link == UpdateAction.None ? Visibility.Collapsed : Visibility.Visible;
		UpdateLinkButton.Content = UpdateView.Label(link);
		UpdateLinkButton.Tag = link;
		UpdateStatusButtons.Visibility = UpdatePrimaryButton.Visibility == Visibility.Visible || UpdateCancelButton.Visibility == Visibility.Visible
			|| UpdateLinkButton.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
		CheckNowButton.IsEnabled = status.Phase != UpdatePhase.Checking;
		if (announce && _announced != view.Title)
		{
			_announced = view.Title;
			var peer = FrameworkElementAutomationPeer.FromElement(UpdateStatusTitle) ?? FrameworkElementAutomationPeer.CreatePeerForElement(UpdateStatusTitle);
			peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, view.Title, "NixieUpdateCheck");
		}
	}

	// Nixie 1.3.1: Check now asks at once (the setting on or off), says it's checking, then what it found, Narrator too
	private async void OnCheckNowClick(object sender, RoutedEventArgs e)
	{
		CheckNowButton.IsEnabled = false;
		try
		{
			await Updates.Center.CheckNowAsync();
		}
		finally
		{
			Show(announce: true);
		}
	}

	private async void OnPrimaryClick(object sender, RoutedEventArgs e)
	{
		if (_view is { Primary: var action and not UpdateAction.None })
		{
			UpdatePrimaryButton.IsEnabled = false;
			try
			{
				await UpdateActions.RunAsync(action, XamlRoot);
			}
			finally
			{
				UpdatePrimaryButton.IsEnabled = true;
			}
		}
	}

	private async void OnCancelClick(object sender, RoutedEventArgs e) => await UpdateActions.RunAsync(UpdateAction.Cancel, XamlRoot);

	private async void OnLinkClick(object sender, RoutedEventArgs e)
	{
		if (UpdateLinkButton.Tag is UpdateAction action and not UpdateAction.None)
		{
			await UpdateActions.RunAsync(action, XamlRoot);
		}
	}
}
