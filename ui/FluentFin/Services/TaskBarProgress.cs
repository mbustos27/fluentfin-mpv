using System.Runtime.InteropServices;

namespace FluentFin.Services;

internal class TaskBarProgress : ITaskBarProgress
{
	// fluentfin-mpv: null when there is no taskbar to show progress on
	private readonly ITaskbarList3? _taskbarInstance;
	public TaskBarProgress() : this(() => (ITaskbarList3)new TaskbarInstance())
	{
	}

	/// <summary>
	/// fluentfin-mpv: with no taskbar -- a desktop without Explorer's, such as the private desktop the dev harness runs on,
	/// or Explorer not running -- HrInit fails (E_NOTIMPL) and there is simply no progress. Throwing here made the player
	/// page, whose view model needs this service, impossible to open there.
	/// </summary>
	internal TaskBarProgress(Func<ITaskbarList3> create)
	{
		try
		{
			var taskbar = create();
			taskbar.HrInit();
			_taskbarInstance = taskbar;
		}
		catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException)
		{
			_taskbarInstance = null;
		}
	}

	public void SetProgressPercent(int percent)
	{
		_taskbarInstance?.SetProgressValue(App.MainWindow.GetWindowHandle(), (ulong)percent, 100);
	}

	public void Clear()
	{
		_taskbarInstance?.SetProgressState(App.MainWindow.GetWindowHandle(), TaskbarProgressBarStatus.NoProgress);
	}
}

// Define the CLSID and IID for TaskbarList
[ComImport]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ITaskbarList3
{
	void HrInit();
	void AddTab(IntPtr hwnd);
	void DeleteTab(IntPtr hwnd);
	void ActivateTab(IntPtr hwnd);
	void SetActiveAlt(IntPtr hwnd);
	void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.VariantBool)] bool fullscreen);
	void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
	void SetProgressState(IntPtr hwnd, TaskbarProgressBarStatus status);
}

[ComImport]
[Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
[ClassInterface(ClassInterfaceType.None)]
internal class TaskbarInstance
{
}

internal enum TaskbarProgressBarStatus
{
	NoProgress = 0,
	Indeterminate = 1,
	Normal = 2,
	Error = 4,
	Paused = 8,
}
