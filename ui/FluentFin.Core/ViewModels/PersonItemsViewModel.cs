using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.5 (search; the second review, 2026-11-20: people search for actors): what a person opens until the person
/// collections arrive: "With Ava Abara", the films and shows they are in, newest first, as a library-style poster grid
/// (<c>/Items?personIds=</c>). Loading, nothing found and a failure are the page's LoadState.
/// </summary>
public partial class PersonItemsViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title))]
	public partial string Name { get; private set; } = "";

	/// <summary>"With Ava Abara".</summary>
	public string Title => TitleFor(Name);

	[ObservableProperty]
	public partial IReadOnlyList<BaseItemViewModel> Items { get; private set; } = [];

	public LoadState Results { get; } = new("their films and shows");

	public static string TitleFor(string? name) => string.IsNullOrWhiteSpace(name) ? "With this person" : $"With {name.Trim()}";

	public async Task OnNavigatedTo(object parameter)
	{
		var person = parameter as BaseItemDto;
		Name = person?.Name?.Trim() ?? "";
		Items = [];
		await Results.RunAsync(async () =>
		{
			if (person?.Id is not { } id)
			{
				return LoadOutcome.Empty("Nothing to show", "Choose a person in the search results.");
			}

			if (await JellyfinClient.GetPersonItems(id) is not { } result)
			{
				return LoadOutcome.Failed($"Couldn't load the films and shows with {(Name.Length > 0 ? Name : "this person")}", LoadMessages.CheckServer());
			}

			Items = [.. (result.Items ?? []).Where(i => i is not null).Select(BaseItemViewModel.FromDto)];
			// 1.5 huntfix (BH-150-m3): the page asks for films and shows, and Jellyfin credits a guest star on the episodes only,
			// so "has no films or shows with them" was false for one; it says what Nixie looked at
			return Items.Count == 0
				? LoadOutcome.Empty($"Nothing with {(Name.Length > 0 ? Name : "this person")}", EmptyDetail(LoadMessages.ServerName()))
				: LoadOutcome.Loaded;
		});
	}

	/// <summary>1.5 huntfix (BH-150-m3): "No films or shows on Home Server list them. Guest roles on single episodes aren't shown here."</summary>
	public static string EmptyDetail(string server) =>
		$"No films or shows on {server} list them. Guest roles on single episodes aren't shown here.";

	public Task OnNavigatedFrom() => Task.CompletedTask;
}
