using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

public sealed partial class UserParentalControlEditorPage : Page
{
	public UserParentalControlEditorViewModel ViewModel { get; } = App.GetService<UserParentalControlEditorViewModel>();

	public UserParentalControlEditorPage()
	{
		InitializeComponent();
	}

	/// <summary>Nixie 1.2.0 (admin, D42): the delete button says which schedule ("Delete Monday 09:00-17:00").</summary>
	public static string DeleteScheduleName(AccessSchedule schedule) =>
		schedule is null ? "Delete schedule" : $"Delete {schedule.DayOfWeek} {Hour(schedule.StartHour)}-{Hour(schedule.EndHour)}";

	private static string Hour(double? hour) => TimeSpan.FromHours(hour ?? 0).ToString(@"hh\:mm");
}
