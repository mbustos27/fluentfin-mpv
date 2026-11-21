using FluentFin.Core.Updates;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.5: the shell's update notice (UpdateNotice.xaml). It shows <see cref="UpdateView.ForNotice"/> of the updater's one
/// state (<see cref="UpdateCenter"/>), while the check setting is on and that version was not dismissed (or right after an
/// update: "Updated to"). <see cref="OpenChanged"/> tells the shell when it comes and goes (its room above the page).
/// </summary>
public sealed partial class UpdateNotice : UserControl
{
	private readonly UpdateNoticeService _updates = App.GetService<UpdateNoticeService>();
	private UpdateView? _view;
	private string? _announced;

	public UpdateNotice()
	{
		InitializeComponent();
		Loaded += (_, _) =>
		{
			_updates.Center.Changed += OnChanged;
			_updates.Coordinator.NoticeChanged += OnNoticeChanged;
			Show();
		};
		Unloaded += (_, _) =>
		{
			_updates.Center.Changed -= OnChanged;
			_updates.Coordinator.NoticeChanged -= OnNoticeChanged;
		};
	}

	/// <summary>The notice came or went.</summary>
	public event EventHandler? OpenChanged;

	public bool IsOpen => Visibility == Visibility.Visible;

	/// <summary>
	/// Nixie 1.5: the page shown is Settings, whose Updates group says the same with the same action: the notice steps aside
	/// there (one Primary per view), and comes back on the next page.
	/// </summary>
	public bool HiddenHere
	{
		get => _hiddenHere;
		set
		{
			if (_hiddenHere != value)
			{
				_hiddenHere = value;
				Show();
			}
		}
	}

	private bool _hiddenHere;

	private void OnChanged(UpdateStatus status) => DispatcherQueue.TryEnqueue(Show);

	private void OnNoticeChanged() => DispatcherQueue.TryEnqueue(Show);

	/// <summary>Whether the notice shows <paramref name="status"/>: "Updated to" always; a release while its notice stands (the setting on, not dismissed).</summary>
	internal static bool Shows(UpdateStatus status, ReleaseInfo? notice) =>
		UpdateView.ForNotice(status) is not null && (status.JustUpdated is not null || (notice is not null && notice.Version == status.Release?.Version));

	private void Show()
	{
		var status = _updates.Center.Status;
		var open = !_hiddenHere && Shows(status, _updates.Coordinator.Notice);
		_view = open ? UpdateView.ForNotice(status) : null;
		if (_view is { } view)
		{
			UpdateTitle.Text = view.Title;
			UpdateDetail.Text = view.Detail ?? "";
			UpdateDetail.Visibility = view.Detail is null ? Visibility.Collapsed : Visibility.Visible;
			UpdateProgress.Visibility = view.Progress is not null || view.Busy ? Visibility.Visible : Visibility.Collapsed;
			UpdateProgress.IsIndeterminate = view.Busy;
			UpdateProgress.Value = (view.Progress ?? 0) * 100;
			UpdatePrimaryButton.Visibility = view.Primary == UpdateAction.None ? Visibility.Collapsed : Visibility.Visible;
			UpdatePrimaryButton.Content = UpdateView.Label(view.Primary);
			var link = view.Others.FirstOrDefault(a => a is UpdateAction.ReleaseNotes or UpdateAction.OpenReleasePage);
			UpdateLinkButton.Visibility = link == UpdateAction.None ? Visibility.Collapsed : Visibility.Visible;
			UpdateLinkButton.Content = UpdateView.Label(link);
			UpdateLinkButton.Tag = link;
			UpdateCancelButton.Visibility = view.Others.Contains(UpdateAction.Cancel) ? Visibility.Visible : Visibility.Collapsed;
			UpdateDismissButton.Visibility = view.Others.Contains(UpdateAction.Dismiss) ? Visibility.Visible : Visibility.Collapsed;
			UpdateIcon.Glyph = view.IsError ? "" : "";   // a warning for a failure, else the info mark
			Announce(view.Title);
		}

		var visibility = open ? Visibility.Visible : Visibility.Collapsed;
		if (Visibility != visibility)
		{
			Visibility = visibility;
			OpenChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>Narrator hears each step once (the title), never the progress line's ticking numbers.</summary>
	private void Announce(string title)
	{
		if (_announced == title)
		{
			return;
		}

		_announced = title;
		var peer = FrameworkElementAutomationPeer.FromElement(UpdateTitle) ?? FrameworkElementAutomationPeer.CreatePeerForElement(UpdateTitle);
		peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, title, "NixieUpdateNotice");
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

	private async void OnLinkClick(object sender, RoutedEventArgs e)
	{
		if (UpdateLinkButton.Tag is UpdateAction action and not UpdateAction.None)
		{
			await UpdateActions.RunAsync(action, XamlRoot);
		}
	}

	private async void OnCancelClick(object sender, RoutedEventArgs e) => await UpdateActions.RunAsync(UpdateAction.Cancel, XamlRoot);

	private async void OnDismissClick(object sender, RoutedEventArgs e) => await UpdateActions.RunAsync(UpdateAction.Dismiss, XamlRoot);
}
