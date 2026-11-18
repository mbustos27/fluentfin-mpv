using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Microsoft.UI.Xaml;
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

		// Seaglass: the global keys and Mouse 4 / 5, at the window (docs/SHORTCUTS.md)
		if (Content is UIElement root)
		{
			WindowShortcuts.Attach(root);
		}
	}

	/// <summary>
	/// 1.2.0 (the top bar): a page before sign-in (servers, sign-in) starts below the bar, which is drawn over the frame; the
	/// shell does that for its own pages (it lets a hero run under the bar).
	/// </summary>
	private void OnRootNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) =>
		RootFrame.Padding = new Thickness(0, e.Content is FluentFin.Views.ShellPage ? 0 : TopBar.Height, 0, 0);

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
