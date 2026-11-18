using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.ViewModels;
using FluentFin.Dialogs.ViewModels;
using FluentFin.Plugins.Playback_Reporting.ViewModels;
using FluentFin.Plugins.Playback_Reporting.Views;
using FluentFin.UI.Core;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.ViewModels;
using FluentFin.Views;
using FluentFin.Views.JellyfinSettings;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Services;

public class PageService : IPageService, IPageRegistration
{
	private readonly Dictionary<string, Type> _pages = [];
	private readonly Dictionary<Type, Type> _viewModels = [];
	private readonly Dictionary<Type, Type> _parents = [];
	private readonly Lock _lock = new();

	// Nixie 1.3.0: the layout themes' own pages for the browsing view models (ThemeService), each mapped back to its view model
	private readonly ThemeService? _themes;
	private readonly Dictionary<string, Type> _keyViewModels = [];
	private readonly Dictionary<Type, Type> _themedViews = [];

	public PageService(IPluginManager pluginManager, ThemeService? themes = null)
	{
		_themes = themes;
		if (themes is not null)
		{
			foreach (var theme in themes.Themes)
			{
				foreach (var vm in Themes.ThemeContract.ThemableViewModels)
				{
					if (ThemeService.ViewFor(theme, vm) is { } view)
					{
						_themedViews.TryAdd(view, vm);
					}
				}
			}
		}

		// Setup Section
		Configure<ShellViewModel, ShellPage>();
		Configure<SelectServerViewModel, SelectServerPage>();
		Configure<LoginViewModel, LoginPage>();

		// Main NavView Pages
		Configure<HomeViewModel, HomePage>();
		Configure<LibraryViewModel, LibraryPage>();
		Configure<ItemGridViewModel, ItemGridPage>();
		Configure<VideoPlayerViewModel, VideoPlayerPage>();
		Configure<MovieViewModel, MoviePage>();
		Configure<SeriesViewModel, SeriesPage>();
		Configure<SeasonViewModel, SeasonPage>();
		Configure<JellyfinSettingsViewModel, JellyfinSettingsPage>();
		Configure<SettingsViewModel, SettingsPage>();
		Configure<MediaSegmentsEditorViewModel, MediaSegmentsEditorPage>();
		Configure<LibrariesLandingPageViewModel, LibrariesLandingPage>();
		Configure<EpisodeViewModel, EpisodePage>();
		// Seaglass 1.1.4: Enter in the title bar's search
		Configure<SearchResultsViewModel, SearchResultsPage>();

		// Dashboard Pages
		Configure<DashboardViewModel, DashboardPage>();
		Configure<GeneralSettingsViewModel, GeneralSettingsPage>();
		Configure<UsersViewModel, UsersPage>();
		Configure<UserEditorViewModel, UserEditorPage>();
		Configure<UserProfileEditorViewModel, UserProfileEditorPage>();
		Configure<UserAccessEditorViewModel, UserAccessEditorPage>();
		Configure<UserParentalControlEditorViewModel, UserParentalControlEditorPage>();
		Configure<UserPasswordEditorViewModel, UserPasswordEditorPage>();
		Configure<LibrariesSettingsViewModel, LibrariesSettingsPage>();
		Configure<LibrariesMetadataViewModel, LibrariesMetadataPage>();
		Configure<LibrariesNfoSettingsViewModel, LibrariesNfoSettingsPage>();
		Configure<LibrariesDisplayViewModel, LibrariesDisplayPage>();
		Configure<PlaybackResumeViewModel, PlaybackResumePage>();
		Configure<PlaybackTrickplayViewModel, PlaybackTrickplayPage>();
		Configure<PlaybackTranscodingViewModel, PlaybackTranscodingPage>();
		Configure<ActivitiesViewModel, ActivitiesPage>();
		Configure<ScheduledTasksViewModel, ScheduledTasksPage>();

		// Setup Parent/Child Relationships
		ConfigureParent<LibraryViewModel, LibrariesLandingPageViewModel>();

		pluginManager.ConfigurePages(this);
	}

	/// <summary>
	/// The page for a view model's key: the active layout theme's own page for it when it has one (Nixie 1.3.0), else the
	/// shared page.
	/// </summary>
	public Type GetPageType(string key)
	{
		Type? pageType;
		Type? vmType;
		lock (_pages)
		{
			if (!_pages.TryGetValue(key, out pageType))
			{
				throw new ArgumentException($"Page not found: {key}. Did you forget to call PageService.Configure?");
			}

			_keyViewModels.TryGetValue(key, out vmType);
		}

		if (vmType is not null && _themes?.ViewFor(vmType) is { } themed)
		{
			lock (_pages)
			{
				_themedViews.TryAdd(themed, vmType);
			}

			return themed;
		}

		return pageType;
	}

	/// <summary>Nixie 1.3.0: the shared page a page stands for (a theme's page: the shared page of its view model; any other: itself).</summary>
	public Type SharedPageType(Type pageType)
	{
		lock (_pages)
		{
			return _themedViews.TryGetValue(pageType, out var vm) && _pages.TryGetValue(vm.FullName!, out var shared) ? shared : pageType;
		}
	}

	public Type GetViewModelType(Type type)
	{
		Type? vmType;
		lock (_pages)
		{
			if (!_viewModels.TryGetValue(type, out vmType) && !_themedViews.TryGetValue(type, out vmType))
			{
				throw new ArgumentException($"VM not found: {type.FullName}. Did you forget to call PageService.Configure?");
			}
		}

		return vmType;
	}

	public Type? GetParent(Type typeKey)
	{
		if (_parents.TryGetValue(typeKey, out var parent))
		{
			return parent;
		}

		return null;
	}

	public void Configure<VM, V>()
		where VM : ObservableObject
		where V : Page
	{
		lock (_pages)
		{
			var key = typeof(VM).FullName!;
			if (_pages.ContainsKey(key))
			{
				throw new ArgumentException($"The key {key} is already configured in PageService");
			}

			var type = typeof(V);
			if (_pages.ContainsValue(type))
			{
				throw new ArgumentException($"This type is already configured with key {_pages.First(p => p.Value == type).Key}");
			}

			_pages.Add(key, type);
			_viewModels.Add(type, typeof(VM));
			_keyViewModels.Add(key, typeof(VM));
		}
	}

	private void ConfigureParent<TChild, TParent>()
	where TChild : INotifyPropertyChanged
	where TParent : INotifyPropertyChanged
	{
		lock (_lock)
		{
			var key = typeof(TChild);
			if (_parents.ContainsKey(key))
			{
				throw new ArgumentException($"The key {key} is already configured in PageService");
			}

			_parents.Add(key, typeof(TParent));
		}
	}
}
