using FluentFin.Controls;
using FluentFin.Core.Contracts.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Dialogs.UserInput;

public class ServerFolderInput : IServerFolderPicker
{
	public async Task<string> PickFolder()
	{
		var control = new ServerFolderPicker();

		// 1.1.4 (states): a title for Alt+Tab and Narrator; only Select this folder picks one (the window's own close
		// returned the folder shown, as if chosen)
		var window = new WindowEx
		{
			Title = "Choose a folder on the server",
			Content = control,
			ExtendsContentIntoTitleBar = true,
			SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
		};

		control.CloseWindow = () => window.Close();

		var semaphore = new SemaphoreSlim(0, 1);

		window.SetWindowSize(460, 520);
		window.CenterOnScreen();
		if (window.Presenter is OverlappedPresenter op)
		{
			op.SetBorderAndTitleBar(true, false);
		}
		window.Activate();
		window.Closed += (_, _) => semaphore.Release();

		await semaphore.WaitAsync();

		return control.SelectedFolder ?? "";
	}
}
