using System.Text.Json.Nodes;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, stream wire): the index's revision 4 keeps the categories' matchable keywords. The 1.7.5 promise
/// holds for the update: 1.8.1's saved index (revision 3) shows at once, its ratings' backing still known, and is read again
/// behind it, once; nothing a page shows waits for that read (design rules 1 and 3).
/// </summary>
public sealed partial class IndexOnABigServerTests
{
	private static readonly string[] s_matchableTags = ["office", "workplace"];

	// the saved index and its note as 1.8.1 left them: revision 3, each title's tags the allowlist's and the seasonal ones only
	private async Task<(string File, string Note)> AsSavedBy181()
	{
		var folder = Path.Combine(_folder, "LibraryIndex");
		var file = Directory.GetFiles(folder, "*.json").Single();
		var root = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
		Assert.Equal(LibraryIndex.FormatRevision, (int?)root["Revision"]);
		root["Revision"] = LibraryIndex.BackingRevision;
		foreach (var entry in root["Entries"]!.AsArray())
		{
			var tags = entry!["Tags"]!.AsArray().Select(x => (string)x!).Where(x => !s_matchableTags.Contains(x)).ToList();
			entry.AsObject()["Tags"] = new JsonArray([.. tags.Select(x => (JsonNode)x)]);
		}

		await File.WriteAllTextAsync(file, root.ToJsonString());
		var note = Directory.GetFiles(Path.Combine(folder, "Reads"), "*.json").Single();
		var reads = JsonNode.Parse(await File.ReadAllTextAsync(note))!.AsObject();
		Assert.Equal(LibraryIndex.FormatRevision, (int?)reads["Revision"]);
		reads["Revision"] = LibraryIndex.BackingRevision;
		await File.WriteAllTextAsync(note, reads.ToJsonString());
		return (file, note);
	}

	// the review of wip/190-wire (Major 2): 1.8.1's index was read again at once for its keywords, at every start (a read cut short
	// by a close began again from its first page at the next), a whole read of the server the user uses at each short session.
	// No read is made for the keywords alone now: the day's read brings them
	[Fact]
	public async Task TheUpdateFrom181_ShowsItsIndexAtOnce_ReadsNothingForItsKeywords_AndTheDaysReadBringsThem()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
		foreach (var film in _films.Where((_, i) => i % 5 == 1))
		{
			film.Tags = ["office", "Workplace ", "duringcreditsstinger"]; // two matchable keywords, and TMDb's junk
		}

		foreach (var film in _films.Where((_, i) => i % 5 == 4))
		{
			film.CriticRating = 81; // rated 9: backed by critics, Acclaimed's
		}

		await ReadAndSave(time);
		var (file, note) = await AsSavedBy181();
		var log = new CoreListLogger<LibraryIndex>();

		// the first start after the update, ten minutes after 1.8.1's last read, and two more short sessions that day: shown at
		// once, and not a page asked (the server's check unchanged)
		var read = _pages;
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Pages(_ => release.Task);
		using var index = Index(time, log);
		try
		{
			foreach (var later in new[] { TimeSpan.FromMinutes(10), TimeSpan.FromHours(3), TimeSpan.FromHours(20) })
			{
				time.Now = new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero) + later;
				using var start = Index(time, log);
				var first = await start.GetAsync().WaitAsync(TimeSpan.FromSeconds(10)); // at once, from this PC
				Assert.Equal(2500, first!.Entries.Count);
				Assert.True(first.BackingKnown); // 1.8.1 kept the critics' ratings and tag counts: Acclaimed keeps its backing
				Assert.False(first.KeywordsKnown);
				Assert.Equal(81f, first.Entries.Single(x => x.Name == "Film 00004").Critic);
				Assert.DoesNotContain(first.Entries, x => x.Tags.Contains("office"));
				await Task.Delay(100);
				await start.Checking.WaitAsync(TimeSpan.FromSeconds(10));
				Assert.True(start.Settled.IsCompleted, "no read began for the keywords"); // a held read would wait here for ever
				Assert.Equal(read, _pages);
			}

			// the day's read, a day after 1.8.1's: behind the saved copy, every page held; what a page shows waits for nothing of it
			time.Now = new DateTimeOffset(2026, 11, 26, 8, 5, 0, TimeSpan.Zero);
			var shown = await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(10));
			Assert.False(shown!.KeywordsKnown);
			var built = await SmartCollectionCatalog.BuildAllAsync(shown, time.Now).WaitAsync(TimeSpan.FromSeconds(20));
			Assert.Contains(built, x => x.Id == "keyword:heist");
			Assert.Contains(built, x => x.Kind == SmartCollectionKind.HighlyRated);
			for (var i = 0; i < 200 && _pages == read; i++)
			{
				await Task.Delay(10);
			}

			Assert.True(_pages > read, "the day's read, behind the saved copy");
			Assert.False(index.Current!.KeywordsKnown); // the saved copy still shows meanwhile
		}
		finally
		{
			release.TrySetResult();
		}

		await index.Settled;
		await WaitForSaved(x => x.Contains($"\"Revision\":{LibraryIndex.FormatRevision}", StringComparison.Ordinal) && x.Contains("\"office\"", StringComparison.Ordinal));
		Assert.True(index.Current!.KeywordsKnown);
		var office = index.Current.Entries.Single(x => x.Name == "Film 00001");
		Assert.Equal(["office", "workplace"], office.Tags); // matchable: kept, lower case; TMDb's junk dropped
		Assert.Equal(3, office.TagCount); // the count before the tiers, as before
		Assert.Equal(LibraryIndex.FormatRevision, (int?)JsonNode.Parse(await File.ReadAllTextAsync(note))!["Revision"]); // the note stamped: this read is in

		// once: the same run's next visit, and the next start past the hour, read nothing more for it
		var once = _pages;
		Assert.NotNull(await index.SavedAsync());
		await Task.Delay(50);
		await index.Settled;
		time.Now += TimeSpan.FromMinutes(90);
		using (var next = Index(time))
		{
			var again = await next.GetAsync();
			Assert.True(again!.KeywordsKnown);
			Assert.Contains(again.Entries, x => x.Tags.Contains("workplace"));
			await Task.Delay(100);
			await next.Settled;
			await next.Checking;
		}

		Assert.Equal(once, _pages);
		Assert.Contains($"\"Revision\":{LibraryIndex.FormatRevision}", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
	}

	// a revision 3 index saved again before its day's read: a people step asked on its own (Settings turned More from on). Saved
	// as revision 4, the next start would take its titles' tags as the matchable ones and its categories would match on what the
	// index never kept. It stays revision 3; and (the review's Major 2) the next start reads nothing for it: the day's read does
	[Fact]
	public async Task A181IndexSavedAgain_BeforeItsDaysRead_StaysRevision3_AndTheNextStartReadsNothingForIt()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
		foreach (var film in _films.Where((_, i) => i % 5 == 1))
		{
			film.Tags = ["office"];
		}

		await ReadAndSave(time);
		var (file, _) = await AsSavedBy181();
		var root = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
		root["HasPeople"] = false; // 1.8.1's people not in yet: a people step is due
		await File.WriteAllTextAsync(file, root.ToJsonString());

		// the first start after the update: no title read
		time.Now += TimeSpan.FromMinutes(10);
		var before = _pages;
		using var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), time) { PeopleWanted = () => true };
		Assert.False((await index.GetAsync())!.KeywordsKnown);
		await Task.Delay(100);
		await index.Settled;
		await index.Checking;
		Assert.Equal(before, _pages);

		// Settings turns a people kind on: the people step alone, on the index shown (revision 3), and it is saved
		index.PeopleSwitched();
		await index.Settled;
		await WaitForSaved(x => x.Contains("\"HasPeople\":true", StringComparison.Ordinal));
		Assert.Contains($"\"Revision\":{LibraryIndex.BackingRevision}", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
		Assert.DoesNotContain($"\"Revision\":{LibraryIndex.FormatRevision}", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
		Assert.Equal(before, _pages); // the people alone

		// the next start, five minutes later: still of revision 3, honestly, and no title read for it
		time.Now += TimeSpan.FromMinutes(5);
		using (var again = Index(time))
		{
			var shown = (await again.GetAsync())!;
			Assert.False(shown.KeywordsKnown);
			Assert.True(shown.HasPeople);
			await Task.Delay(100);
			await again.Settled;
			await again.Checking;
		}

		Assert.Equal(before, _pages);

		// the day's read brings revision 4
		time.Now = new DateTimeOffset(2026, 11, 26, 8, 5, 0, TimeSpan.Zero);
		using (var day = Index(time))
		{
			Assert.NotNull(await day.GetAsync());
			for (var i = 0; i < 200 && _pages == before; i++)
			{
				await Task.Delay(10);
			}

			await day.Settled;
			Assert.True(day.Current!.KeywordsKnown);
		}

		await WaitForSaved(x => x.Contains($"\"Revision\":{LibraryIndex.FormatRevision}", StringComparison.Ordinal) && x.Contains("\"office\"", StringComparison.Ordinal));
	}

	// the review of wip/190-wire (Minor 5): "an older format" was one flag of this PC's index, set by whichever user's index was
	// loaded: one user's 1.7.2 index, its upgrade read failed, made another user's current index read again at each visit
	[Fact]
	public async Task OneUsersOlderIndex_DoesntMakeAnotherUsersCurrentIndexReadAgain()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
		var folder = Path.Combine(_folder, "LibraryIndex");
		var other = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000190");
		await ReadAndSave(time); // this user's index, current
		var mine = Directory.GetFiles(folder, "*.json").Single();
		var myNote = Directory.GetFiles(Path.Combine(folder, "Reads"), "*.json").Single();
		_client.SetupGet(x => x.UserId).Returns(other);
		await ReadAndSave(time);
		var theirs = Directory.GetFiles(folder, "*.json").Except([mine]).Single();
		var theirNote = Directory.GetFiles(Path.Combine(folder, "Reads"), "*.json").Except([myNote]).Single();
		await File.WriteAllTextAsync(theirs, (await File.ReadAllTextAsync(theirs)).Replace($",\"Revision\":{LibraryIndex.FormatRevision}", "", StringComparison.Ordinal)); // 1.7.2's index
		await File.WriteAllTextAsync(theirNote, (await File.ReadAllTextAsync(theirNote)).Replace($",\"Revision\":{LibraryIndex.FormatRevision}", "", StringComparison.Ordinal));

		// two hours later, on one PC: the other user's 1.7.2 index, its upgrade read failing part way
		time.Now += TimeSpan.FromHours(2);
		Pages(start => start == 0 ? Task.CompletedTask : Task.FromException(new HttpRequestException("the server went away")));
		using var index = Index(time);
		var before = _pages;
		Assert.True(LibraryIndex.OlderFormat((await index.GetAsync())!));
		for (var i = 0; i < 200 && _pages == before; i++)
		{
			await Task.Delay(10);
		}

		await index.Settled;
		Assert.True(_pages > before, "their upgrade read began");

		// then this user signs in: their index is current, read two hours ago: shown, and nothing read
		_client.SetupGet(x => x.UserId).Returns(User);
		Pages(_ => Task.CompletedTask);
		var failed = _pages;
		var shown = await index.SavedAsync();
		Assert.False(LibraryIndex.OlderFormat(shown!));
		await Task.Delay(100);
		await index.Settled;
		await index.Checking;
		Assert.Equal(failed, _pages);
		Assert.Equal(2500, index.Current!.Entries.Count);
	}

	[Fact]
	public void TheRevisionSaved_IsTheDatas()
	{
		var index = new IndexSnapshot([], [], new Dictionary<Guid, SeriesPlays>(), false, new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));

		Assert.Equal(4, LibraryIndex.FormatRevision);
		Assert.Equal(4, LibraryIndex.RevisionOf(index)); // a read now
		Assert.Equal(3, LibraryIndex.RevisionOf(index with { KeywordsKnown = false })); // 1.8.1's, saved again
		Assert.Equal(0, LibraryIndex.RevisionOf(index with { BackingKnown = false, KeywordsKnown = false })); // 1.7.2's
		// 1.9 (the index's load): 1.8.1's index takes what changed within the day and stays revision 3 (its old titles keep the
		// narrower tags until the day's read): WhatChangedIsAskedFromTheLastRead_AndA181IndexTakesItAndStaysRevision3
	}

	[Fact]
	public void TheIndexKeeps_TheMatchableKeywords_NotTMDbsJunkNorTagsTypedByHand_AndOnlyTheAllowlistNames()
	{
		var dto = new BaseItemDto
		{
			Id = Guid.NewGuid(),
			Name = "Tagged",
			Type = BaseItemDto_Type.Movie,
			Tags = ["Heist", " office ", "duringcreditsstinger", "Witty", "my own tag", "christmas", "office", "", "thanksgiving"],
		};

		var entry = IndexEntry.From(dto, MoviesId)!;

		Assert.Equal(["heist", "office", "witty", "christmas", "thanksgiving"], entry.Tags);
		Assert.Equal(9, entry.TagCount);
		Assert.True(Keywords.IsNamed("Heist "));
		Assert.All(new[] { "office", "witty", "christmas", "thanksgiving", "duringcreditsstinger" }, x => Assert.False(Keywords.IsNamed(x)));
		Assert.All(Keywords.Allowed.Keys, x => Assert.Contains(x, Keywords.Matchable));
		Assert.All(Keywords.Seasonal, x => Assert.Contains(x, Keywords.Matchable));
		Assert.True(Keywords.Matchable.Count > Keywords.Allowed.Count + Keywords.Seasonal.Count + 200, $"{Keywords.Matchable.Count} matchable");
	}
}
