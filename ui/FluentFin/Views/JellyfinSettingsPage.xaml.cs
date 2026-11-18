using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.Services;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core;
using FluentFin.Core.Admin;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using FluentFin.UI.Core.Contracts.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Views;

/// <summary>
/// Nixie 1.2.0 (admin): the server administration, with its sections as text tabs along the top (Core.Admin.AdminSections)
/// instead of a left navigation pane. Picking a tab opens its page in the Settings frame; opening a page any other way
/// (a user's settings from Users) selects its section's tab, and a page opened from a section links back to it.
/// </summary>
public sealed partial class JellyfinSettingsPage : Page
{
	private readonly INavigationService _navigationService = App.GetKeyedService<INavigationService>(NavigationRegions.Settings);
	private readonly IPageService _pageService = App.GetService<IPageService>();
	private bool _syncing;

	public JellyfinSettingsViewModel ViewModel { get; } = App.GetService<JellyfinSettingsViewModel>();

	public JellyfinSettingsPage()
	{
		InitializeComponent();

		foreach (var section in AdminSections.All)
		{
			SectionBar.Items.Add(new SelectorBarItem { Text = section.Name, Tag = section });
		}

		_navigationService.Frame = NavFrame;
		Follow(true);
		// a page in the back stack is unloaded and loaded again: the tabs follow the frame only while the page is shown
		Loaded += (_, _) =>
		{
			Follow(true);
			ShowPlace(NavFrame.Content?.GetType());
		};
		Unloaded += (_, _) => Follow(false);
	}

	private bool _following;

	private void Follow(bool on)
	{
		if (on != _following)
		{
			if (on)
			{
				_navigationService.Navigated += OnNavigated;
			}
			else
			{
				_navigationService.Navigated -= OnNavigated;
			}

			_following = on;
		}
	}

	private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
	{
		if (_syncing || sender.SelectedItem?.Tag is not AdminSection section)
		{
			return;
		}

		// a section with pages opens its first one; its own tab row shows once the page is there (OnNavigated)
		Open(section.Key);
	}

	private void PageBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
	{
		if (_syncing || sender.SelectedItem?.Tag is not AdminPage page)
		{
			return;
		}

		Open(page.Key);
	}

	private void Open(string key)
	{
		if (_pageService.GetPageType(key) != NavFrame.Content?.GetType())
		{
			_navigationService.NavigateTo(key);
		}
	}

	private void BackLink_Click(object sender, RoutedEventArgs e)
	{
		if (NavFrame.CanGoBack)
		{
			NavFrame.GoBack();
		}
		else if (SectionBar.SelectedItem?.Tag is AdminSection section)
		{
			_navigationService.NavigateTo(section.Key);
		}
	}

	/// <summary>The tabs follow the page, however it was opened.</summary>
	private void OnNavigated(object sender, NavigationEventArgs e) => ShowPlace(e.SourcePageType);

	private void ShowPlace(Type? pageType)
	{
		AdminPlace? place;
		try
		{
			place = pageType is null ? null : AdminSections.Find(_pageService.GetViewModelType(pageType)?.FullName);
		}
		catch (ArgumentException)
		{
			place = null; // a page of no view model
		}

		_syncing = true;
		try
		{
			foreach (var item in SectionBar.Items)
			{
				item.IsSelected = place is not null && item.Tag == place.Section;
			}

			PageBar.Items.Clear();
			if (place is { Section.HasPageTabs: true })
			{
				AutomationPropertiesName(PageBar, $"{place.Section.Name} pages");
				foreach (var page in place.Section.Pages)
				{
					var item = new SelectorBarItem { Text = page.Name, Tag = page };
					PageBar.Items.Add(item);
					item.IsSelected = page == place.Page;
				}
			}

			PageBar.Visibility = PageBar.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			BackLink.Visibility = place is { IsSubPage: true } ? Visibility.Visible : Visibility.Collapsed;
			BackLinkText.Text = place is { IsSubPage: true } ? $"Back to {place.Section.Name}" : "";
		}
		finally
		{
			_syncing = false;
		}
	}

	private static void AutomationPropertiesName(DependencyObject element, string name) =>
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}

public partial class JellyfinSettingsViewModel : ObservableObject, INavigationAware
{
	private readonly INavigationService _navigationService;

	public JellyfinSettingsViewModel([FromKeyedServices(NavigationRegions.Settings)] INavigationService navigationService)
	{
		_navigationService = navigationService;
	}

	public async Task OnNavigatedFrom()
	{
		// leaving settings doesn't navigate the inner frame, so the dashboard won't stop its sessions feed on its own
		if (_navigationService.Frame?.GetPageViewModel() is DashboardViewModel dashboard)
		{
			await dashboard.OnNavigatedFrom();
		}
	}

	public Task OnNavigatedTo(object parameter)
	{
		_navigationService.NavigateTo<DashboardViewModel>(new());
		return Task.CompletedTask;
	}
}
