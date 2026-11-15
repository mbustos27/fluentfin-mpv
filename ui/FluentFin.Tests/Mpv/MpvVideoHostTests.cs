using FluentFin.Mpv;

namespace FluentFin.Tests.Mpv;

public sealed class MpvVideoHostTests
{
	[Fact]
	public void Unresponsive_AHandlerThatThrows_DoesNotReachTheWatchdogsTimerThread()
	{
		// the watchdog raises Unresponsive on a System.Threading.Timer thread: an exception there ends FluentFin
		var log = new ListLogger<MpvVideoHost>();
		var host = new MpvVideoHost(log);
		var later = new List<string>();
		host.Unresponsive += _ => throw new AggregateException("Not all processes in process tree could be terminated.", new System.ComponentModel.Win32Exception(6));
		host.Unresponsive += later.Add;

		var error = Record.Exception(() => host.NotifyUnresponsive("mpv stopped responding"));

		Assert.Null(error);
		Assert.Equal(["mpv stopped responding"], later);
		Assert.Contains(log.Messages, x => x.Contains("unresponsive mpv window"));
	}
}
