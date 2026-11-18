using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.Services;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core;
using FluentFin.Plugins.Playback_Reporting.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FluentFin.Plugins.Playback_Reporting.ViewModels;

public partial class PlaybackReportingDashboardViewModel([FromKeyedServices(NavigationRegions.PlaybackReporting)] INavigationService navigationService) : ObservableObject, INavigationAware
{
	/// <summary>Nixie 1.2.0 (admin): the reports, in tab order (they were a left navigation pane).</summary>
	public static IReadOnlyList<PlaybackReport> Reports { get; } =
	[
		new("Users", typeof(UsersReportViewModel).FullName!, typeof(UsersReportPage)),
		new("Playback", typeof(PlaybackReportViewModel).FullName!, typeof(PlaybackReportPage)),
		new("Breakdown", typeof(BreakdownReportViewModel).FullName!, typeof(BreakdownReportPage)),
		new("Usage", typeof(UsageReportViewModel).FullName!, typeof(UsageReportPage)),
		new("Duration", typeof(SessionDurationReportViewModel).FullName!, typeof(SessionDurationReportPage)),
	];

	public INavigationService NavigationService { get; } = navigationService;

	public Task OnNavigatedFrom() => Task.CompletedTask;

	public Task OnNavigatedTo(object parameter) => Task.CompletedTask;
}

/// <param name="Key">The report's view model, which the reports' frame navigates to.</param>
public sealed record PlaybackReport(string Name, string Key, Type PageType);
