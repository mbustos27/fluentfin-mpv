using FluentFin.Mpv;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv player controls: mpv's swap chain crosses from the core's event thread to the UI thread
/// (<see cref="MpvSwapChainSlot"/>). mpv may release it at any time after it said so; the slot's own references keep it
/// alive until the panel shows it, and the one shown before is let go only after the panel shows the next. Reference
/// counts are played by <see cref="FakeCom"/>, which notes any use of a swap chain that is already gone.
/// </summary>
public sealed class SwapChainSlotTests
{
	/// <summary>COM objects by number: AddRef and Release, and a fault for any touch of one whose count reached 0.</summary>
	private sealed class FakeCom(int objects)
	{
		private readonly int[] _counts = new int[objects + 1];
		private readonly Lock _faultsLock = new();
		private int _next;

		public List<string> Faults { get; } = [];

		/// <summary>A new object, one reference held by its maker (mpv).</summary>
		public nint New()
		{
			var p = Interlocked.Increment(ref _next);
			_counts[p] = 1;
			return p;
		}

		public int Count(nint p) => Volatile.Read(ref _counts[p]);

		public int AddRef(nint p)
		{
			var count = Interlocked.Increment(ref _counts[p]);
			if (count <= 1)
			{
				lock (_faultsLock)
				{
					Faults.Add($"AddRef on {p} after it was freed");
				}
			}

			return count;
		}

		public int Release(nint p)
		{
			var count = Interlocked.Decrement(ref _counts[p]);
			if (count < 0)
			{
				lock (_faultsLock)
				{
					Faults.Add($"Release of {p} after it was freed");
				}
			}

			return count;
		}
	}

	/// <summary>A SwapChainPanel: SetSwapChain takes a reference of its own and lets go of the one it showed.</summary>
	private sealed class FakePanel(FakeCom com)
	{
		public nint Showing { get; private set; }

		public void Bind(nint swapChain)
		{
			if (swapChain != 0 && com.Count(swapChain) <= 0)
			{
				com.Faults.Add($"the panel was given {swapChain} after it was freed");
			}

			if (swapChain != 0)
			{
				com.AddRef(swapChain);
			}

			if (Showing != 0)
			{
				com.Release(Showing);
			}

			Showing = swapChain;
		}
	}

	/// <summary>What MpvCompositionVideo.BindCurrent does on the UI thread.</summary>
	private static void BindOnTheUiThread(MpvSwapChainSlot slot, FakePanel panel)
	{
		var next = slot.TakeLatest();
		if (next == slot.Shown)
		{
			slot.Drop(next);
			return;
		}

		panel.Bind(next);
		slot.SetShown(next);
	}

	[Fact]
	public void MpvReleasingItsSwapChainBeforeTheUiBindsIt_LeavesItAlive()
	{
		var com = new FakeCom(4);
		var slot = new MpvSwapChainSlot(com.AddRef, com.Release);
		var panel = new FakePanel(com);

		var a = com.New();
		slot.Publish(a);            // the event thread hears of it
		com.Release(a);             // mpv lets go of it (a reconfigure) before the UI thread gets to it
		BindOnTheUiThread(slot, panel);

		Assert.Empty(com.Faults);
		Assert.Equal(a, panel.Showing);
		Assert.True(com.Count(a) > 0);
	}

	[Fact]
	public void TheSwapChainShownBefore_IsLetGoOnlyAfterThePanelShowsTheNext_AndNothingLeaks()
	{
		var com = new FakeCom(4);
		var slot = new MpvSwapChainSlot(com.AddRef, com.Release);
		var panel = new FakePanel(com);
		var a = com.New();
		slot.Publish(a);
		BindOnTheUiThread(slot, panel);

		var b = com.New();
		slot.Publish(b);
		com.Release(a);             // mpv is done with the old one
		Assert.True(com.Count(a) > 0, "still shown: the panel and the slot hold it");
		BindOnTheUiThread(slot, panel);

		Assert.Empty(com.Faults);
		Assert.Equal(b, panel.Showing);
		Assert.Equal(0, com.Count(a));

		// the end: nothing published, the panel unbound; only mpv's own reference to b is left
		slot.Publish(0);
		BindOnTheUiThread(slot, panel);
		Assert.Equal(0, panel.Showing);
		Assert.Equal(1, com.Count(b));
		Assert.Empty(com.Faults);
	}

	[Fact]
	public void TheSameSwapChainAgain_IsNotBoundTwice_AndNotLeaked()
	{
		var com = new FakeCom(2);
		var slot = new MpvSwapChainSlot(com.AddRef, com.Release);
		var panel = new FakePanel(com);
		var a = com.New();
		slot.Publish(a);
		BindOnTheUiThread(slot, panel);
		var held = com.Count(a);

		BindOnTheUiThread(slot, panel);
		BindOnTheUiThread(slot, panel);

		Assert.Equal(held, com.Count(a));
		Assert.Empty(com.Faults);
	}

	[Fact]
	public async Task PublishedFromOneThread_BoundOnAnother_MpvReleasingEachAtOnce_NeverTouchesAFreedOne()
	{
		const int rounds = 20_000;
		var com = new FakeCom(rounds + 1);
		var slot = new MpvSwapChainSlot(com.AddRef, com.Release);
		var panel = new FakePanel(com);
		var done = false;

		var events = Task.Run(() =>
		{
			for (var i = 0; i < rounds; i++)
			{
				var swapChain = com.New();
				slot.Publish(swapChain);
				com.Release(swapChain);   // mpv's own reference goes right after
			}

			Volatile.Write(ref done, true);
		});

		while (!Volatile.Read(ref done))
		{
			BindOnTheUiThread(slot, panel);
		}

		await events;
		BindOnTheUiThread(slot, panel);
		slot.Publish(0);
		BindOnTheUiThread(slot, panel);

		Assert.Empty(com.Faults);
		for (nint p = 1; p <= rounds; p++)
		{
			Assert.Equal(0, com.Count(p));
		}
	}
}
