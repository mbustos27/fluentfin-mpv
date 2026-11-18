using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views.JellyfinSettings;

public sealed partial class ScheduledTasksPage : Page
{
	public ScheduledTasksViewModel ViewModel { get; } = App.GetService<ScheduledTasksViewModel>();

	public ScheduledTasksPage()
	{
		InitializeComponent();
	}

	/// <summary>The card runs its task: Narrator says so ("Run Scan Media Library now").</summary>
	public static string RunName(string? task) => $"Run {task} now";

	private void NoticeBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.Notice = null;
}
