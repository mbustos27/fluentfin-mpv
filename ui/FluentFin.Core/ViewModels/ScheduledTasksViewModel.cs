using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <remarks>Nixie 1.2.0 (admin, button review D43): a task's card runs it now and says it started.</remarks>
public partial class ScheduledTasksViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	[ObservableProperty]
	public partial List<CategoryTaskPair> Tasks { get; set; } = new();

	/// <summary>What the last Run now did.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNotice))]
	public partial ServerNotice? Notice { get; set; }

	/// <summary>The notice bar opens from this bool (x:Bind does not call a function whose argument is null, so a bar opened through one never closed).</summary>
	public bool HasNotice => Notice is not null;

	public Task OnNavigatedFrom() => Task.CompletedTask;

	public async Task OnNavigatedTo(object parameter)
	{
		var tasks = await jellyfinClient.GetScheduledTasks();

		if (tasks is null)
		{
			return;
		}

		Tasks = tasks
			.Where(x => x.Category != null)
			.GroupBy(x => x.Category!)
			.OrderBy(x => x.Key)
			.Select(x => new CategoryTaskPair(x.Key, [.. x]))
			.ToList();
	}

	[RelayCommand]
	private async Task Run(TaskInfo task)
	{
		if (string.IsNullOrEmpty(task.Id))
		{
			return;
		}

		Notice = await jellyfinClient.RunScheduledTask(task.Id)
			? new ServerNotice(ServerPowerState.None, $"Started: {task.Name}", "The server runs it now; this page shows the task's last result next time it is opened.", NoticeKind.Success)
			: new ServerNotice(ServerPowerState.Failed, $"{task.Name} didn't start", "The server didn't accept the request.", NoticeKind.Error);
	}
}

public record CategoryTaskPair(string Category, List<TaskInfo> Tasks);
