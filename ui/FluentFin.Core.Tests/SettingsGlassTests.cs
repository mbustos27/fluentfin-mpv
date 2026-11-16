using System.Reactive.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass: Settings > Appearance > Glass is kept on this PC, shown when the page opens, and every mode is offered.</summary>
[Collection(SessionInfoCollection.Name)]
public sealed class SettingsGlassTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));
	private readonly LocalSettingsService _service;

	static SettingsGlassTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public SettingsGlassTests() => _service = new LocalSettingsService(new KnownFolders(_root));

	private async Task<SettingsViewModel> OpenSettings()
	{
		var backend = new Mock<IMpvBackend>();
		backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		var vm = new SettingsViewModel(new Settings.Settings(_service), backend.Object, Mock.Of<IJellyfinClient>(), null!);
		try
		{
			await vm.OnNavigatedTo(null!);
		}
		catch (Exception)
		{
			// no server here; the Glass choice is read before the server's preferences
		}

		return vm;
	}

	[Fact]
	public void ByDefaultItFollowsWindows() => Assert.Equal(GlassChoice.Automatic, new Settings.Settings(_service).Glass);

	[Theory]
	[InlineData(GlassChoice.Full)]
	[InlineData(GlassChoice.Reduced)]
	[InlineData(GlassChoice.Solid)]
	[InlineData(GlassChoice.Automatic)]
	public async Task APick_IsSaved_AndComesBackWhenSettingsOpens(GlassChoice choice)
	{
		var vm = await OpenSettings();
		vm.SelectedGlass = SettingsViewModel.GlassChoices.Single(x => x.Choice == choice);

		Assert.Equal(choice, _service.ReadSetting(SettingKeys.Glass));
		Assert.Equal(choice, new Settings.Settings(_service).Glass);

		var reopened = await OpenSettings();
		Assert.Equal(choice, reopened.SelectedGlass.Choice);
	}

	[Fact]
	public async Task APick_RaisesGlassChanged_SoTheAppCanApplyIt()
	{
		var vm = await OpenSettings();
		var changed = new List<string?>();
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		vm.SelectedGlass = SettingsViewModel.GlassChoices.Single(x => x.Choice == GlassChoice.Solid);

		Assert.Contains(nameof(SettingsViewModel.Glass), changed);
	}

	[Fact]
	public void EveryChoiceIsOffered_WithADescription()
	{
		Assert.Equal(Enum.GetValues<GlassChoice>().Order(), SettingsViewModel.GlassChoices.Select(x => x.Choice).Order());
		Assert.All(SettingsViewModel.GlassChoices, x => Assert.False(string.IsNullOrWhiteSpace(x.Description)));
		Assert.Equal(["Automatic", "Full", "Reduced", "Solid"], SettingsViewModel.GlassChoices.Select(x => x.Name));
	}

	[Fact]
	public void AStoredValueThatIsNotAChoice_ReadsAsAutomatic()
	{
		_service.SaveSetting(SettingKeys.Glass, (GlassChoice)42);

		Assert.Equal(GlassChoice.Automatic, new Settings.Settings(_service).Glass);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, true);
		}
	}
}
