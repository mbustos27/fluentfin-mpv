using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Outside;
using FluentFin.Core.Settings;
using static FluentFin.Core.Tests.Outside.OutsideKit;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// BH-170-M1 (the 1.7.0 bug hunt): Wikidata or TMDb's export files turned off mid-round. The round read each switch once,
/// so 11 of Wikidata's 12 queries and TMDb's second file still went out after the switch was off, and the round's lists
/// were shown until the next one; PRIVACY.md says "Nixie stops asking it at once". A copy of cb27430's test for Trakt and
/// TMDb trending: the switch goes off during the first request; nothing more is sent, the answer that comes back is
/// neither kept on disk nor shown.
/// </summary>
public sealed class OutsideSwitchOffTests
{
	/// <summary>Counts the requests and calls <paramref name="onRequest"/> as each arrives, before the fake answers it.</summary>
	private sealed class Tap(HttpMessageHandler inner, Action<HttpRequestMessage> onRequest) : DelegatingHandler(inner)
	{
		private int _calls;

		public int Calls => Volatile.Read(ref _calls);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _calls);
			onRequest(request);
			return base.SendAsync(request, cancellationToken);
		}
	}

	[Theory]
	[InlineData(OutsideSourceId.Wikidata)]
	[InlineData(OutsideSourceId.TmdbExports)]
	public async Task AKeylessSourceTurnedOffMidRound_SendsNothingMore_KeepsNothing_AndShowsNothing(OutsideSourceId id)
	{
		var clock = new TestClock(Noon); // after 08:00 UTC: today's export files are up
		HttpMessageHandler fake = id == OutsideSourceId.Wikidata ? new StrictWikidata(clock.Func) : new StrictTmdbExports(clock.Func);
		var store = new MemoryStore();
		OutsideHub? hub = null;
		var turnedOff = 0;
		var tap = new Tap(fake, _ =>
		{
			if (Interlocked.Exchange(ref turnedOff, 1) == 0)
			{
				hub!.SetOn(id, false); // what the Settings switch does
			}
		});
		hub = new OutsideHub(new HttpClient(tap), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), store, clock.Func);
		var lists = new OutsideLists(hub, store, clock.Func);
		var changed = new List<OutsideListsSnapshot>();
		lists.Changed += changed.Add;
		store.SaveSetting(id == OutsideSourceId.Wikidata ? SettingKeys.LookUpWikidata : SettingKeys.LookUpTmdbExports, true);

		var round = await lists.RefreshAsync();
		await lists.Background; // the refresh the switch queued

		Assert.Equal(1, tap.Calls);
		Assert.True(round.IsEmpty);
		Assert.True(lists.Current.IsEmpty);
		Assert.DoesNotContain(changed, x => !x.IsEmpty);
		// the answer that arrived after the switch went off wasn't kept
		var cached = id == OutsideSourceId.Wikidata
			? hub.Cache.Read(OutsideSources.Wikidata, WikidataLists.All[0].Id)
			: hub.Cache.Read(OutsideSources.TmdbExports, TmdbExports.CacheName(TmdbExports.Collections));
		Assert.Null(cached);
	}

	[Theory]
	[InlineData(OutsideSourceId.Wikidata)]
	[InlineData(OutsideSourceId.TmdbExports)]
	public async Task ASourceLeftOn_StillAsksForEverything(OutsideSourceId id)
	{
		// the contrast: the same round with the switch left on asks for every list and keeps them
		var clock = new TestClock(Noon);
		HttpMessageHandler fake = id == OutsideSourceId.Wikidata ? new StrictWikidata(clock.Func) : new StrictTmdbExports(clock.Func);
		var store = new MemoryStore();
		var tap = new Tap(fake, _ => { });
		var hub = new OutsideHub(new HttpClient(tap), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), store, clock.Func);
		var lists = new OutsideLists(hub, store, clock.Func);
		store.SaveSetting(id == OutsideSourceId.Wikidata ? SettingKeys.LookUpWikidata : SettingKeys.LookUpTmdbExports, true);

		var round = await lists.RefreshAsync();

		Assert.Equal(id == OutsideSourceId.Wikidata ? WikidataLists.All.Count : 2, tap.Calls);
		Assert.False(round.IsEmpty);
	}
}
