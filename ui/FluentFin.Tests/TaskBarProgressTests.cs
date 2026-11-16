using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>fluentfin-mpv: no taskbar (a private desktop, Explorer not running) means no progress, not a player page that cannot open.</summary>
public sealed class TaskBarProgressTests
{
	private sealed class NoTaskbar(Exception failure) : ITaskbarList3
	{
		public int Calls { get; private set; }
		public void HrInit() => throw failure;
		public void AddTab(IntPtr hwnd) => Calls++;
		public void DeleteTab(IntPtr hwnd) => Calls++;
		public void ActivateTab(IntPtr hwnd) => Calls++;
		public void SetActiveAlt(IntPtr hwnd) => Calls++;
		public void MarkFullscreenWindow(IntPtr hwnd, bool fullscreen) => Calls++;
		public void SetProgressValue(IntPtr hwnd, ulong completed, ulong total) => Calls++;
		public void SetProgressState(IntPtr hwnd, TaskbarProgressBarStatus status) => Calls++;
	}

	public static TheoryData<Exception> Failures => new()
	{
		new NotImplementedException(),                                           // E_NOTIMPL: a desktop with no taskbar
		new System.Runtime.InteropServices.COMException("no taskbar", unchecked((int)0x80004005)),
	};

	[Theory]
	[MemberData(nameof(Failures))]
	public void HrInitFailing_LeavesAProgressThatDoesNothing(Exception failure)
	{
		var taskbar = new NoTaskbar(failure);

		var progress = new TaskBarProgress(() => taskbar);
		progress.SetProgressPercent(50);
		progress.Clear();

		Assert.Equal(0, taskbar.Calls);
	}

	// review: the failure was swallowed without a word
	[Fact]
	public void NoTaskbar_IsSaidOnceInTheLog()
	{
		var log = new Mpv.ListLogger<TaskBarProgress>();

		_ = new TaskBarProgress(() => new NoTaskbar(new System.Runtime.InteropServices.COMException("no taskbar", unchecked((int)0x80004001))), log);

		var entry = Assert.Single(log.Entries);
		Assert.Contains("taskbar", entry.Message);
		Assert.Contains("80004001", entry.Message);
	}

	[Fact]
	public void NoTaskbarClass_LeavesAProgressThatDoesNothing()
	{
		var progress = new TaskBarProgress(() => throw new System.Runtime.InteropServices.COMException("class not registered", unchecked((int)0x80040154)));

		progress.SetProgressPercent(50);
		progress.Clear();
	}
}
