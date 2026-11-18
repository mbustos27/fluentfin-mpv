using FluentFin.Core.ViewModels;
using FluentFin.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views.JellyfinSettings;

public sealed partial class DashboardPage : Page
{
	public DashboardViewModel ViewModel { get; } = App.GetService<DashboardViewModel>();

	public DashboardPage()
	{
		InitializeComponent();
		// Nixie 1.2.0 (admin): Restart and Shut down ask here, Cancel the default
		ViewModel.Confirm = DestructiveConfirm.AskInMainWindow;
	}

	public static bool IsClosable(ServerNotice? notice) => notice is { Busy: false, State: not ServerPowerState.ShutDown };

	public static InfoBarSeverity Severity(ServerNotice? notice) => notice?.Kind switch
	{
		NoticeKind.Success => InfoBarSeverity.Success,
		NoticeKind.Warning => InfoBarSeverity.Warning,
		NoticeKind.Error => InfoBarSeverity.Error,
		_ => InfoBarSeverity.Informational,
	};

	private void ServerNoticeBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissNotice();

	public static string PlayPauseName(string? userName, string? deviceName) => SessionName("Play or pause", userName, deviceName);

	public static string InfoName(string? userName, string? deviceName) => SessionName("Playback information for", userName, deviceName);

	public static string MessageName(string? userName, string? deviceName) => SessionName("Send a message to", userName, deviceName);

	/// <summary>A session button's name says whose session it acts on: "Play or pause alice on Living room TV".</summary>
	public static string SessionName(string action, string? userName, string? deviceName)
	{
		var who = string.IsNullOrWhiteSpace(userName) ? " this session" : $" {userName}";
		var where = string.IsNullOrWhiteSpace(deviceName) ? "" : $" on {deviceName}";
		return $"{action}{who}{where}";
	}

	public static string StopName(string? userName) => string.IsNullOrWhiteSpace(userName) ? "Stop playback" : $"Stop {userName}'s playback";
}
