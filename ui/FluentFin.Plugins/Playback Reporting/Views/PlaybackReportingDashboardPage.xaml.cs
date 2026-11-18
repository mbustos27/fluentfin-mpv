using FluentFin.Core;
using FluentFin.Core.Services;
using FluentFin.Plugins.Playback_Reporting.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;


namespace FluentFin.Plugins.Playback_Reporting.Views;

/// <summary>Nixie 1.2.0 (admin): the reports as text tabs along the top (it was a left navigation pane).</summary>
public sealed partial class PlaybackReportingDashboardPage : Page
{
	private bool _syncing;

	public PlaybackReportingDashboardViewModel ViewModel { get; } = Locator.GetService<PlaybackReportingDashboardViewModel>();

	public PlaybackReportingDashboardPage()
	{
		InitializeComponent();

		foreach (var report in PlaybackReportingDashboardViewModel.Reports)
		{
			ReportBar.Items.Add(new SelectorBarItem { Text = report.Name, Tag = report });
		}

		ViewModel.NavigationService.Frame = NavFrame;
		ViewModel.NavigationService.Navigated += OnFrameNavigated;
		Unloaded += (_, _) => ViewModel.NavigationService.Navigated -= OnFrameNavigated;
	}

	private void ReportBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
	{
		if (!_syncing && sender.SelectedItem?.Tag is PlaybackReport report && NavFrame.Content?.GetType() != report.PageType)
		{
			ViewModel.NavigationService.NavigateTo(report.Key);
		}
	}

	/// <summary>The tab follows the report shown.</summary>
	private void OnFrameNavigated(object sender, NavigationEventArgs e)
	{
		_syncing = true;
		try
		{
			foreach (var item in ReportBar.Items)
			{
				item.IsSelected = item.Tag is PlaybackReport report && report.PageType == e.SourcePageType;
			}
		}
		finally
		{
			_syncing = false;
		}
	}

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		ViewModel.NavigationService.NavigateTo<UsersReportViewModel>();
	}
}
