using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin;

public sealed partial class MainWindow : WindowEx
{
	public IMainWindowViewModel ViewModel { get; } = App.GetService<IMainWindowViewModel>();

	public MainWindow()
	{
		InitializeComponent();
		ExtendsContentIntoTitleBar = true;
		AppWindow.SetIcon("Assets/Seaglass.ico");

		App.GetKeyedService<INavigationService>(NavigationRegions.InitialSetup).Frame = RootFrame;

		// Seaglass: desktop acrylic as the window's own backdrop where no artwork covers it, Mica otherwise
		AmbientBackdrop.Changed += (_, _) => RunOnUi(UpdateBackdrop);
		try { GlassMode.Changed += (_, _) => RunOnUi(UpdateBackdrop); }
		catch (Exception) { }
		UpdateBackdrop();
	}

	private bool _acrylic;

	private void RunOnUi(Action action)
	{
		if (DispatcherQueue.HasThreadAccess)
		{
			action();
		}
		else
		{
			DispatcherQueue.TryEnqueue(() => action());
		}
	}

	private void UpdateBackdrop()
	{
		GlassModeKind mode;
		try { mode = GlassMode.Current; }
		catch (Exception) { mode = GlassModeKind.Solid; }

		var acrylic = AmbientBackdropView.WantsDesktopAcrylic(AmbientBackdrop.Current, mode);
		if (acrylic == _acrylic && SystemBackdrop is not null)
		{
			return;
		}

		_acrylic = acrylic;
		SystemBackdrop = acrylic
			? new DesktopAcrylicBackdrop()
			: new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
	}
}
