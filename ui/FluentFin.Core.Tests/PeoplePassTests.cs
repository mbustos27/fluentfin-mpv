using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Anchors;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-2): the people pass, top-3 billing only: once every library's people for an index that has none, then only
/// the new titles' people by id, the rest lent from the index before (1.7.1 read every library's people at every read).
/// </summary>
public sealed class PeoplePassTests
{
	private static readonly DateTimeOffset Now = new(2026, 11, 24, 20, 0, 0, TimeSpan.Zero);

	private static IndexEntry Title(Guid id, IndexPerson[]? people = null) => new() { Id = id, Name = $"Title {id:N}"[..14], Kind = IndexKind.Movie, People = people ?? [] };

	private static IndexSnapshot Index(IEnumerable<IndexEntry> entries, bool hasPeople = true) => new([], [.. entries], new Dictionary<Guid, SeriesPlays>(), hasPeople, Now);

	private static BaseItemDto WithPeople(Guid id, params (string Name, BaseItemPerson_Type Type)[] people) =>
		new() { Id = id, People = [.. people.Select(p => new BaseItemPerson { Name = p.Name, Type = p.Type })] };

	[Fact]
	public void ATitlesPeople_AreItsDirectorsAndItsFirstThreeActors()
	{
		var dto = WithPeople(Guid.NewGuid(),
			("Writer W", BaseItemPerson_Type.Writer), ("Lead", BaseItemPerson_Type.Actor), ("Director D", BaseItemPerson_Type.Director), ("  ", BaseItemPerson_Type.Actor),
			("Second", BaseItemPerson_Type.Actor), ("Guest", BaseItemPerson_Type.GuestStar), ("Third", BaseItemPerson_Type.Actor), ("Fourth", BaseItemPerson_Type.Actor));

		Assert.Equal(["Director D/Director", "Lead/Actor", "Second/Actor", "Third/Actor"], PeoplePass.Billed(dto).Select(p => $"{p.Name}/{p.Kind}"));
		Assert.Empty(PeoplePass.Billed(new BaseItemDto { Id = Guid.NewGuid() }));
	}

	[Fact]
	public void AnIndexWithNoPeople_ReadsEveryLibrary_ThenOnlyTheNewTitlesById()
	{
		var known = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
		var before = Index(known.Select(id => Title(id, [new IndexPerson("Someone", null, "Actor")])));
		var fresh = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToList();
		var now = Index([.. known.Concat(fresh).Select(id => Title(id))], hasPeople: false);

		Assert.True(PeoplePass.PlanFor(now, null).Whole); // a first read
		Assert.True(PeoplePass.PlanFor(now, before with { HasPeople = false }).Whole); // the index before had none (1.7.3's)
		Assert.True(PeoplePass.PlanFor(now, before with { PeopleDue = true }).Whole); // its whole pass never finished

		var plan = PeoplePass.PlanFor(now, before);
		Assert.False(plan.Whole);
		Assert.Equal(fresh.Order(), plan.Ask.Order());

		// those it still had to ask are asked too; nothing new and nothing left: nothing at all
		Assert.Equal(fresh.Append(known[3]).Order(), PeoplePass.PlanFor(now, before, [known[3]]).Ask.Order());
		Assert.True(PeoplePass.PlanFor(before, before).Nothing);

		// review (Major): however many titles are new (a library added), by id: never every library's people again
		var many = Index([.. known.Select(id => Title(id)), .. Enumerable.Range(0, 5_000).Select(_ => Title(Guid.NewGuid()))]);
		var manyPlan = PeoplePass.PlanFor(many, before);
		Assert.False(manyPlan.Whole);
		Assert.Equal(5_000, manyPlan.Ask.Count);
	}

	[Fact]
	public async Task ASecondRead_LendsThePeopleItHad_AndAsksOnlyTheNewTitles_AHundredARequest()
	{
		var known = Enumerable.Range(0, 300).Select(_ => Guid.NewGuid()).ToList();
		var before = Index(known.Select(id => Title(id, [new IndexPerson($"Kept {id:N}"[..10], null, "Director")])));
		var fresh = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList();
		var now = Index([.. known.Concat(fresh).Select(id => Title(id))], hasPeople: false) with { PeopleDue = true };
		var asked = new List<int>();
		var wholePages = 0;

		var run = await new PeoplePass(null).RunAsync(now, before, null, [new BaseItemDto { Id = Guid.NewGuid() }],
			(_, _, _, _, _) => { wholePages++; return Task.FromResult<BaseItemDtoQueryResult?>(null); },
			(ids, _) => { asked.Add(ids.Count); return Task.FromResult<IReadOnlyList<BaseItemDto>?>([.. ids.Select(id => WithPeople(id, ("New Lead", BaseItemPerson_Type.Actor)))]); },
			IndexPages.PageSize, CancellationToken.None);
		var made = run.Index;

		Assert.True(run.Complete);
		Assert.Empty(made.PeopleToAsk);
		Assert.Equal(0, wholePages);
		Assert.Equal([100, 50], asked);
		Assert.True(made.HasPeople);
		Assert.False(made.PeopleDue);
		Assert.All(made.Entries.Where(x => known.Contains(x.Id)), x => Assert.StartsWith("Kept", Assert.Single(x.People).Name));
		Assert.All(made.Entries.Where(x => fresh.Contains(x.Id)), x => Assert.Equal("New Lead", Assert.Single(x.People).Name));
	}

	[Fact]
	public async Task AFirstRead_ReadsEveryLibrarysPeople_EachTitleOnce()
	{
		var libraries = new[] { new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies" }, new BaseItemDto { Id = Guid.NewGuid(), Name = "4K Movies" } };
		var inFirst = Enumerable.Range(0, 1_200).Select(_ => Guid.NewGuid()).ToList();
		var inSecond = inFirst.Take(300).Concat(Enumerable.Range(0, 100).Select(_ => Guid.NewGuid())).ToList(); // 300 in both
		var byLibrary = new Dictionary<Guid, List<Guid>> { [libraries[0].Id!.Value] = inFirst, [libraries[1].Id!.Value] = inSecond };
		var now = Index([.. inFirst.Concat(inSecond).Distinct().Select(id => Title(id))], hasPeople: false);
		var byIds = 0;

		var run = await new PeoplePass(null).RunAsync(now, null, null, libraries,
			(library, start, limit, withTotal, _) =>
			{
				var all = byLibrary[library.Id!.Value];
				return Task.FromResult<BaseItemDtoQueryResult?>(new BaseItemDtoQueryResult
				{
					Items = [.. all.Skip(start).Take(limit).Select(id => WithPeople(id, ("Dee Rector", BaseItemPerson_Type.Director), ("Lead", BaseItemPerson_Type.Actor)))],
					TotalRecordCount = withTotal ? all.Count : null,
				});
			},
			(_, _) => { byIds++; return Task.FromResult<IReadOnlyList<BaseItemDto>?>([]); },
			IndexPages.PageSize, CancellationToken.None);
		var made = run.Index;

		Assert.True(run.Complete);
		Assert.Empty(made.PeopleToAsk);
		Assert.Equal(0, byIds);
		Assert.Equal(1_300, made.Entries.Count);
		Assert.All(made.Entries, x => Assert.Equal(["Dee Rector", "Lead"], x.People.Select(p => p.Name)));
	}

	[Fact]
	public async Task NewTitlesWhosePeopleDidntCome_AreLeftToAsk_TheLentOnesKept()
	{
		var known = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
		var before = Index(known.Select(id => Title(id, [new IndexPerson("Kept", null, "Actor")])));
		var fresh = Guid.NewGuid();
		var now = Index([.. known.Append(fresh).Select(id => Title(id))], hasPeople: false);

		var run = await new PeoplePass(null).RunAsync(now, before, null, [], (_, _, _, _, _) => Task.FromResult<BaseItemDtoQueryResult?>(null),
			(_, _) => Task.FromResult<IReadOnlyList<BaseItemDto>?>(null), IndexPages.PageSize, CancellationToken.None);

		Assert.False(run.Complete);
		Assert.True(run.Index.HasPeople);
		Assert.Equal(known.Count, run.Index.Entries.Count(x => x.People.Length == 1)); // the lent ones kept
		Assert.Equal([fresh], run.Index.PeopleToAsk); // the new one asked again
		Assert.Equal([fresh], PeoplePass.PlanFor(run.Index, run.Index, run.Index.PeopleToAsk).Ask);
	}

	// review (Major): a pass cut short keeps what came, and the next one asks only the rest
	[Fact]
	public async Task AWholePassCutShort_KeepsWhatCame_AndTheNextAsksOnlyTheRestById()
	{
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies" };
		var all = Enumerable.Range(0, 1_200).Select(_ => Guid.NewGuid()).ToList();
		var now = Index([.. all.Select(id => Title(id))], hasPeople: false);
		using var stop = new CancellationTokenSource();
		var kept = new List<IndexSnapshot>();
		var twoIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		// the first page (200) and the next one come (and are handed over); the pass is cut off while the third waits
		var run = await new PeoplePass(null) { KeepEvery = TimeSpan.Zero }.RunAsync(now, null, null, [library],
			async (_, start, limit, withTotal, token) =>
			{
				if (start >= 450)
				{
					await twoIn.Task.WaitAsync(token);
					await stop.CancelAsync();
					await Task.Delay(Timeout.Infinite, token);
				}

				return new BaseItemDtoQueryResult { Items = [.. all.Skip(start).Take(limit).Select(id => WithPeople(id, ("Dee Rector", BaseItemPerson_Type.Director)))], TotalRecordCount = withTotal ? all.Count : null };
			},
			(_, _) => Task.FromResult<IReadOnlyList<BaseItemDto>?>([]), 250, stop.Token, soFar =>
			{
				lock (kept)
				{
					kept.Add(soFar);
				}

				if (soFar.Entries.Count(x => x.People.Length == 1) >= 450)
				{
					twoIn.TrySetResult();
				}

				return Task.CompletedTask;
			});

		Assert.False(run.Complete);
		Assert.Equal(450, run.Came);
		Assert.Equal(450, run.Index.Entries.Count(x => x.People.Length == 1));
		Assert.Equal(all.Skip(450).Order(), run.Index.PeopleToAsk.Order());
		Assert.False(run.Index.PeopleDue);
		Assert.NotEmpty(kept); // what came so far was handed over as it came
		Assert.Equal(450, kept[^1].Entries.Count(x => x.People.Length == 1));

		// the next pass: by id, only the 750 left; none of the 450 asked again
		var asked = new List<Guid>();
		var next = await new PeoplePass(null).RunAsync(run.Index, run.Index, run.Index.PeopleToAsk, [library], (_, _, _, _, _) => throw new InvalidOperationException("no page"),
			(ids, _) => { asked.AddRange(ids); return Task.FromResult<IReadOnlyList<BaseItemDto>?>([.. ids.Select(id => WithPeople(id, ("Dee Rector", BaseItemPerson_Type.Director)))]); },
			250, CancellationToken.None);
		Assert.Equal(all.Skip(450).Order(), asked.Order());
		Assert.True(next.Complete);
		Assert.All(next.Index.Entries, x => Assert.Single(x.People));
	}

	// re-review (Minor 3): the server refusing a page or a request (401, 403) is a cut: what came is kept
	[Fact]
	public async Task ARefusal_KeepsWhatCame_AsACut()
	{
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies" };
		var all = Enumerable.Range(0, 1_200).Select(_ => Guid.NewGuid()).ToList();
		var now = Index([.. all.Select(id => Title(id))], hasPeople: false);
		var twoIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var whole = await new PeoplePass(null) { KeepEvery = TimeSpan.Zero }.RunAsync(now, null, null, [library],
			async (_, start, limit, withTotal, token) =>
			{
				if (start >= 450)
				{
					await twoIn.Task.WaitAsync(token);
					throw new FluentFin.Core.Services.IndexRefusedException(401);
				}

				return new BaseItemDtoQueryResult { Items = [.. all.Skip(start).Take(limit).Select(id => WithPeople(id, ("Dee Rector", BaseItemPerson_Type.Director)))], TotalRecordCount = withTotal ? all.Count : null };
			},
			(_, _) => Task.FromResult<IReadOnlyList<BaseItemDto>?>([]), 250, CancellationToken.None,
			soFar =>
			{
				if (soFar.Entries.Count(x => x.People.Length == 1) >= 450)
				{
					twoIn.TrySetResult();
				}

				return Task.CompletedTask;
			});
		Assert.False(whole.Complete);
		Assert.Equal(450, whole.Came);
		Assert.Equal(750, whole.Index.PeopleToAsk.Count);

		var calls = 0;
		var byId = await new PeoplePass(null).RunAsync(whole.Index, whole.Index, whole.Index.PeopleToAsk, [library], (_, _, _, _, _) => throw new InvalidOperationException("no page"),
			(ids, _) => ++calls == 1
				? Task.FromResult<IReadOnlyList<BaseItemDto>?>([.. ids.Select(id => WithPeople(id, ("Dee Rector", BaseItemPerson_Type.Director)))])
				: throw new FluentFin.Core.Services.IndexRefusedException(403),
			250, CancellationToken.None);
		Assert.False(byId.Complete);
		Assert.Equal(100, byId.Came);
		Assert.Equal(650, byId.Index.PeopleToAsk.Count);
		Assert.Equal(550, byId.Index.Entries.Count(x => x.People.Length == 1));
	}

	[Fact]
	public async Task AWholePassOfWhichNothingCame_IsStillDue()
	{
		var now = Index([.. Enumerable.Range(0, 300).Select(_ => Title(Guid.NewGuid()))], hasPeople: false);
		var run = await new PeoplePass(null).RunAsync(now, null, null, [new BaseItemDto { Id = Guid.NewGuid() }],
			(_, _, _, _, _) => throw new OperationCanceledException(), (_, _) => Task.FromResult<IReadOnlyList<BaseItemDto>?>([]), 250, new CancellationToken(canceled: true));

		Assert.False(run.Complete);
		Assert.True(run.Index.PeopleDue); // not 300 titles to ask by id
		Assert.Empty(run.Index.PeopleToAsk);
	}

	[Fact]
	public async Task ATitleTheServerNoLongerHas_HasNoPeople_AndIsntAskedForever()
	{
		var known = Guid.NewGuid();
		var gone = Guid.NewGuid();
		var before = Index([Title(known, [new IndexPerson("Kept", null, "Actor")])]);
		var now = Index([Title(known), Title(gone)], hasPeople: false);

		var run = await new PeoplePass(null).RunAsync(now, before, null, [], (_, _, _, _, _) => Task.FromResult<BaseItemDtoQueryResult?>(null),
			(_, _) => Task.FromResult<IReadOnlyList<BaseItemDto>?>([]), IndexPages.PageSize, CancellationToken.None);
		var made = run.Index;

		Assert.True(run.Complete);
		Assert.Empty(made.Entries.Single(x => x.Id == gone).People);
		Assert.Empty(made.PeopleToAsk); // asked, answered without it: not asked again
		Assert.True(PeoplePass.PlanFor(made, made, made.PeopleToAsk).Nothing);
	}
}
