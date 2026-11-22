using FluentFin.Core.Mpv;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace FluentFin.Controls;

/// <summary>
/// 1.6 player: the Playback stats panel over the video (PlaybackStatsPanel.xaml). Shown while
/// <see cref="MpvControlsViewModel.IsStatsOpen"/>; its rows are <see cref="MpvControlsViewModel.StatsRows"/>. Copy puts
/// <see cref="MpvControlsViewModel.StatsCopyText"/> on the clipboard (scrubbed of the token and server credentials); Close,
/// I and Esc close it. A close with the keyboard inside the panel hands the keyboard back to the player, so its keys go on
/// working (otherwise WinUI would hand it to the next control outside the player, as with the Skip button).
/// </summary>
public sealed partial class PlaybackStatsPanel : UserControl
{
	public static readonly DependencyProperty ControlsProperty = DependencyProperty.Register(
		nameof(Controls), typeof(MpvControlsViewModel), typeof(PlaybackStatsPanel), new PropertyMetadata(null, OnControlsChanged));

	/// <summary>How long Copy says "Copied".</summary>
	public static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(2);

	private DispatcherQueueTimer? _copied;

	public PlaybackStatsPanel()
	{
		InitializeComponent();
		Visibility = Visibility.Collapsed;
		Unloaded += (_, _) => _copied?.Stop();
	}

	public MpvControlsViewModel? Controls
	{
		get => (MpvControlsViewModel?)GetValue(ControlsProperty);
		set => SetValue(ControlsProperty, value);
	}

	/// <summary>The rows as shown (the harness and the tests read them).</summary>
	internal ItemsControl RowsView => RowsList;

	private static void OnControlsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var self = (PlaybackStatsPanel)d;
		if (e.OldValue is MpvControlsViewModel old)
		{
			old.PropertyChanging -= self.OnControlsPropertyChanging;
			old.PropertyChanged -= self.OnControlsPropertyChanged;
		}

		if (e.NewValue is MpvControlsViewModel controls)
		{
			controls.PropertyChanging += self.OnControlsPropertyChanging;
			controls.PropertyChanged += self.OnControlsPropertyChanged;
		}

		self.Show(self.Controls?.IsStatsOpen == true);
	}

	private void OnControlsPropertyChanging(object? sender, System.ComponentModel.PropertyChangingEventArgs e)
	{
		if (e.PropertyName == nameof(MpvControlsViewModel.IsStatsOpen) && Controls is { IsStatsOpen: true } && KeyboardInside())
		{
			// about to close with the keyboard in it (Tab put it on Copy or Close; I or Esc pressed there): the player takes it now
			GiveKeyboardToPlayer();
		}
	}

	private void OnControlsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(MpvControlsViewModel.IsStatsOpen) && Controls is { } controls)
		{
			Show(controls.IsStatsOpen);
			Announce(controls.IsStatsOpen ? "Playback stats shown. I or Esc hides them." : "Playback stats hidden.");
		}
	}

	private void Show(bool open)
	{
		Visibility = open ? Visibility.Visible : Visibility.Collapsed;
		if (!open)
		{
			ResetCopyLabel();
		}
	}

	private void OnCloseClick(object sender, RoutedEventArgs e) => Controls?.SetStatsOpen(false);

	private void OnCopyClick(object sender, RoutedEventArgs e)
	{
		if (Controls is not { } controls)
		{
			return;
		}

		controls.NotifyActivity();
		try
		{
			var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
			package.SetText(controls.StatsCopyText());
			Clipboard.SetContent(package);
		}
		catch (Exception)
		{
			// the clipboard is busy (another app holds it): said, nothing else happens
			CopyLabel.Text = "Try again";
			Announce("The stats could not be copied. Try again.");
			return;
		}

		CopyLabel.Text = "Copied";
		Announce("Playback stats copied");
		_copied ??= DispatcherQueue.CreateTimer();
		_copied.Interval = CopiedFor;
		_copied.IsRepeating = false;
		_copied.Tick -= OnCopiedTick;
		_copied.Tick += OnCopiedTick;
		_copied.Start();
	}

	private void OnCopiedTick(DispatcherQueueTimer sender, object args) => ResetCopyLabel();

	private void ResetCopyLabel()
	{
		_copied?.Stop();
		CopyLabel.Text = "Copy";
	}

	private bool KeyboardInside()
	{
		if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
		{
			return false;
		}

		for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, this))
			{
				return true;
			}
		}

		return false;
	}

	private void GiveKeyboardToPlayer()
	{
		for (var node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is MpvPlayerControls player)
			{
				player.Focus(FocusState.Programmatic);
				return;
			}
		}
	}

	/// <summary>Narrator meets the panel as one named group ("Playback stats"), its rows and buttons inside it.</summary>
	protected override AutomationPeer OnCreateAutomationPeer() => new PanelPeer(this);

	private sealed partial class PanelPeer(PlaybackStatsPanel owner) : FrameworkElementAutomationPeer(owner)
	{
		protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

		protected override string GetNameCore() => "Playback stats";

		protected override string GetClassNameCore() => nameof(PlaybackStatsPanel);
	}

	/// <summary>Narrator says <paramref name="text"/> without moving the focus.</summary>
	private void Announce(string text)
	{
		if ((FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this)) is { } peer)
		{
			peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "PlaybackStats");
		}
	}
}
