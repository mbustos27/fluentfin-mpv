using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels
{
	public abstract partial class ServerConfigurationViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
	{
		protected readonly IJellyfinClient _jellyfinClient = jellyfinClient;
		private ServerConfiguration? _configuration;

		[ObservableProperty]
		public partial List<JellyfinConfigItemViewModel> Items { get; set; } = [];

		// 1.5 fixes (the button review's S22): Save is on only when something differs from what the server has (or the last
		// Save failed), and says how it went: "Saved", or that the server did not take it
		private bool _saving;
		private bool _unsaved;

		/// <summary>What the last Save did: "Saved", a failure in words, or empty (nothing saved yet, or changed since).</summary>
		[ObservableProperty]
		public partial string SaveStatus { get; set; } = "";

		public const string SavedText = "Saved";

		public const string NotSavedText = "Couldn't save: the server didn't take the changes. Check that it is running, then save again.";

		/// <summary>Something to save: a value differs from the server's, or the last Save failed.</summary>
		public bool CanSave => !_saving && (_unsaved || Items.Any(x => x.IsChanged));

		partial void OnItemsChanged(List<JellyfinConfigItemViewModel> oldValue, List<JellyfinConfigItemViewModel> newValue)
		{
			foreach (var item in (oldValue ?? []).SelectMany(x => x.AndChildren()))
			{
				item.PropertyChanged -= OnItemChanged;
			}

			foreach (var item in (newValue ?? []).SelectMany(x => x.AndChildren()))
			{
				item.PropertyChanged += OnItemChanged;
			}

			_unsaved = false;
			SaveStatus = "";
			SaveCommand.NotifyCanExecuteChanged();
		}

		private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName != nameof(JellyfinConfigItemViewModel.IsChanged))
			{
				return;
			}

			if (!_saving)
			{
				SaveStatus = "";
			}

			OnPropertyChanged(nameof(CanSave));
			SaveCommand.NotifyCanExecuteChanged();
		}
		// END 1.5 fixes

		public Task OnNavigatedFrom() => Task.CompletedTask;

		public async Task OnNavigatedTo(object parameter)
		{
			_configuration = await _jellyfinClient.GetConfiguration();

			if (_configuration is null)
			{
				return;
			}

			Items = CreateItems(_configuration);
		}

		protected abstract List<JellyfinConfigItemViewModel> CreateItems(ServerConfiguration config);

		/// <summary>
		/// 1.5.1 fixes: what a page saves besides the server configuration, after it (Libraries > Display's date-added choice is
		/// the metadata configuration's, which Save never sent while the page said "Saved"). True when the server took it.
		/// </summary>
		protected virtual Task<bool> SaveMoreAsync() => Task.FromResult(true);

		[RelayCommand(CanExecute = nameof(CanSave))]
		private async Task Save()
		{
			if (_configuration is null)
			{
				return;
			}

			_saving = true;
			SaveCommand.NotifyCanExecuteChanged();
			try
			{
				foreach (var item in Items)
				{
					item.Save();
				}

				// 1.5 fixes: whether the server took it (SaveConfiguration logs a failure and says nothing)
				var took = await _jellyfinClient.TrySaveConfiguration(_configuration);
				took = await SaveMoreAsync() && took; // 1.5.1 fixes
				_unsaved = !took;
				SaveStatus = took ? SavedText : LoadMessages.ActionFailed("Couldn't save", NotSavedText); // 1.6.1 (BH-151-m2)
			}
			finally
			{
				_saving = false;
				OnPropertyChanged(nameof(CanSave));
				SaveCommand.NotifyCanExecuteChanged();
			}
		}

		[RelayCommand]
		private void Reset()
		{
			foreach (var item in Items)
			{
				item.Reset();
			}
		}
	}
}
