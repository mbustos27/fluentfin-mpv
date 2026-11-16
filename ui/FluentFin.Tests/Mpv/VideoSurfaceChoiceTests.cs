using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.Mpv;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv player controls: the video surface the backend is connected with (MpvVideoSurfaceChoice). Making
/// FluentFin's in-process mpv (mpv_create, mpv_initialize: its GPU device and swap chain) takes a while, and a connect can
/// come from the UI thread: it is made on the thread pool.
/// </summary>
[Collection(AloneCollection.Name)]
public sealed class VideoSurfaceChoiceTests
{
	private static MpvVideoSurfaceChoice Choice() => new(new MpvVideoHost(new ListLogger<MpvVideoHost>()),
		new MpvCompositionVideo(new ListLogger<MpvCompositionVideo>(), null), Mock.Of<ISettings>(), new ListLogger<MpvVideoSurfaceChoice>());

	[Fact]
	public async Task MakingTheInProcessMpv_DoesNotHoldTheCaller()
	{
		using var made = new ManualResetEventSlim();
		int? madeOn = null;
		var choice = Choice();
		choice.Choice = () => (true, null);
		choice.AttachedWindow = () =>
		{
			madeOn = Environment.CurrentManagedThreadId;
			made.Wait(TimeSpan.FromSeconds(10));
			return Task.FromResult(BridgeWindow.Attached("fluentfin-mpv-1-00"));
		};

		Task<BridgeWindow>? pending = null;
		int? callerId = null;
		var caller = new Thread(() =>
		{
			callerId = Environment.CurrentManagedThreadId;
			pending = choice.GetWindowAsync();
		});
		caller.Start();
		var returned = caller.Join(TimeSpan.FromSeconds(3));
		made.Set();
		caller.Join();

		Assert.True(returned, "the caller (the UI thread, in the app) was held while the core was made");
		Assert.NotEqual(callerId, madeOn);
		Assert.Equal(BridgeWindow.Attached("fluentfin-mpv-1-00"), await pending!);
	}
}
