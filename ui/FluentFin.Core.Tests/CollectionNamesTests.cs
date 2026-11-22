using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>Tests that change <see cref="CollectionNames.Tidy"/> or count the cleaner's calls: run alone, never beside another class.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CollectionNamesSerial
{
	public const string Name = "CollectionNames (static switch)";
}

/// <summary>Nixie 1.6 (smart collections phase 0, plan section 5): tidy collection and library names, and nothing else.</summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class CollectionNamesTests
{
	private static BaseItemDto BoxSet(string name, string? tmdb = null)
	{
		var dto = new BaseItemDto { Name = name, Type = BaseItemDto_Type.BoxSet };
		if (tmdb is not null)
		{
			dto.ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = tmdb } };
		}

		return dto;
	}

	[Theory]
	// the owner's server and the plan's examples
	[InlineData("[HUB] Marvel", "Marvel")]
	[InlineData("[HUB] Trending Movies", "Trending Movies")]
	[InlineData("[hub] Kids Movies", "Kids Movies")]
	[InlineData("{HUB} Marvel", "Marvel")]
	[InlineData("[HUB][4K] Marvel", "Marvel")]
	[InlineData("[HUB] [4K] Marvel", "Marvel")]
	[InlineData("[HUB] - Marvel", "Marvel")]
	[InlineData("[HUB]: Marvel", "Marvel")]
	[InlineData("[BOX SET] Bond", "Bond")]
	[InlineData("[Kometa] Oscars", "Oscars")]
	[InlineData("Marvel (4K)", "Marvel")]
	[InlineData("Marvel (4K HDR DV)", "Marvel")]
	[InlineData("Marvel (1080p)", "Marvel")]
	[InlineData("Marvel (SD DVD)", "Marvel")]
	[InlineData("Bond (Box Set)", "Bond")]
	[InlineData("Marvel [HUB]", "Marvel")]
	[InlineData("🎅 Christmas", "Christmas")]
	[InlineData("Christmas 🎄", "Christmas")]
	[InlineData("🎃👻 Halloween", "Halloween")]
	[InlineData("❤️ Romance", "Romance")]
	[InlineData("🇫🇷 French films", "French films")]
	[InlineData("👨‍👩‍👧 Family night", "Family night")]
	[InlineData("!010_Bond", "Bond")]
	[InlineData("001_Bond", "Bond")]
	[InlineData("01. Marvel", "Marvel")]
	[InlineData("01 - Marvel", "Marvel")]
	[InlineData("!!Marvel", "Marvel")]
	[InlineData("+++++++_X-Men", "X-Men")]
	[InlineData("~Misc", "Misc")]
	[InlineData("^actors", "actors")]
	[InlineData("_Kids", "Kids")]
	[InlineData("! Favorites", "Favorites")]
	[InlineData("!- Favorites", "Favorites")]
	[InlineData("zz Old stuff", "Old stuff")]
	[InlineData("zz_Old stuff", "Old stuff")]
	[InlineData("ZZ_Old stuff", "Old stuff")]
	[InlineData("Mar​vel", "Marvel")]
	[InlineData("﻿  Marvel   Heroes ", "Marvel Heroes")]
	[InlineData("[HUB] 🎅 Christmas (4K)", "Christmas")]
	public void Tidies(string server, string shown) => Assert.Equal(shown, CollectionNames.Clean(server, fromTmdb: false));

	[Theory]
	// titles that look like decorations, and names that are already clean
	[InlineData("[REC]")]
	[InlineData("[•REC]")]
	[InlineData("[REC]²")]
	[InlineData("#Alive")]
	[InlineData("!Women Art Revolution")]
	[InlineData("+1")]
	[InlineData("*batteries not included")]
	[InlineData("(500) Days of Summer")]
	[InlineData("M*A*S*H")]
	[InlineData("'71")]
	[InlineData("...And Justice for All")]
	[InlineData("$9.99")]
	[InlineData("9")]
	[InlineData("1917")]
	[InlineData("2001: A Space Odyssey")]
	[InlineData("1. Mai")]
	[InlineData("00 Schneider – Jagd auf Nihil Baxter")]
	[InlineData("Zzyzx")]
	[InlineData("ZZ Top: That Little Ol' Band from Texas")]
	[InlineData("!!!")]
	[InlineData("Hidamari Sketch x ☆☆☆")]
	[InlineData("★ Stars ★")]
	[InlineData("♪ Musicals")]
	[InlineData("The Collection")]
	[InlineData("The Criterion Collection")]
	[InlineData("Trilogy of Terror")]
	[InlineData("Star Wars: Skywalker Saga")]
	[InlineData("Godzilla (MonsterVerse)")]
	[InlineData("Alien / Predator")]
	[InlineData("Rocky / Creed")]
	[InlineData("Fast & Furious")]
	[InlineData("Mission: Impossible")]
	[InlineData("Oscars Best Picture Winners")]
	[InlineData("Best of 2022")]
	[InlineData("The Lion King Collection (Animated)")]
	[InlineData("Halloween (1978)")]
	[InlineData("[HUB]Marvel")]
	public void LeavesAlone(string name) => Assert.Equal(name, CollectionNames.Clean(name, fromTmdb: false));

	[Theory]
	[InlineData("The Mummy Collection", "The Mummy")]
	[InlineData("Spider-Man Collection", "Spider-Man")]
	[InlineData("[HUB] The Matrix Collection", "The Matrix")]
	[InlineData("The Collection", "The Collection")]
	[InlineData("Star Wars: Skywalker Saga", "Star Wars: Skywalker Saga")]
	[InlineData("The Lord of the Rings Trilogy", "The Lord of the Rings Trilogy")]
	[InlineData("The Lion King Collection (Animated)", "The Lion King Collection (Animated)")]
	public void TheCollectionSuffix_OnlyWhenTmdbNamedIt(string server, string shown)
	{
		Assert.Equal(shown, CollectionNames.Display(BoxSet(server, tmdb: "1234")));
		// no TMDb id: an admin's own name, kept
		Assert.Equal(CollectionNames.Clean(server, fromTmdb: false), CollectionNames.Display(BoxSet(server)));
	}

	[Theory]
	[InlineData("[HUB] Marvel")]
	[InlineData("🎅 Christmas")]
	[InlineData("!010_Bond")]
	[InlineData("[REC] Collection")]
	[InlineData("+++++++_X")]
	[InlineData("zz_!!Misc")]
	[InlineData("The Mummy Collection (4K)")]
	public void CleaningTwice_ChangesNothing(string server)
	{
		foreach (var tmdb in new[] { false, true })
		{
			var once = CollectionNames.Clean(server, tmdb);
			Assert.Equal(once, CollectionNames.Clean(once, tmdb));
		}
	}

	[Fact]
	public void TwoNamesThatWouldReadTheSame_KeepTheServersNames()
	{
		var sets = new[] { BoxSet("Spider-Man Collection (4K HDR)", "556"), BoxSet("Spider-Man Collection (SD)", "556"), BoxSet("[HUB] Marvel") };

		Assert.Equal(["Spider-Man Collection (4K HDR)", "Spider-Man Collection (SD)", "Marvel"], CollectionNames.DisplayAll(sets));
	}

	[Fact]
	public void ATidiedNameThatIsAnotherSetsName_KeepsTheServersName()
	{
		var sets = new[] { BoxSet("Marvel"), BoxSet("[HUB] Marvel") };

		Assert.Equal(["Marvel", "[HUB] Marvel"], CollectionNames.DisplayAll(sets));
	}

	[Fact]
	public void LibraryNames_StayUnique_WhenTwoWouldReadTheSame()
	{
		// review Major 5: "Movies" and "Movies (4K)" are two libraries (the reviewer's probe); "Movies" and "[HUB] Movies",
		// or "[HUB] Movies" and "[4K] Movies", would read the same tidied: each keeps its original
		try
		{
			CollectionNames.RememberLibraries([]);
			Assert.NotEqual(CollectionNames.DisplayLibrary("Movies"), CollectionNames.DisplayLibrary("Movies (4K)"));
			Assert.Equal("Movies (4K)", CollectionNames.DisplayLibrary("Movies (4K)"));

			CollectionNames.RememberLibraries(["Movies", "[HUB] Movies", "[HUB] Kids", "[HUB] Shows", "[4K] Shows"]);
			Assert.Equal("Movies", CollectionNames.DisplayLibrary("Movies"));
			Assert.Equal("[HUB] Movies", CollectionNames.DisplayLibrary("[HUB] Movies"));
			Assert.Equal("[HUB] Shows", CollectionNames.DisplayLibrary("[HUB] Shows"));
			Assert.Equal("[4K] Shows", CollectionNames.DisplayLibrary("[4K] Shows"));
			Assert.Equal("Kids", CollectionNames.DisplayLibrary("[HUB] Kids"));
			// where they show: Home's "New in …" and the library page's title
			Assert.Equal("New in [HUB] Movies", new NamedQueryResult(new BaseItemDto { Name = "[HUB] Movies" }).Title);
			Assert.Equal("New in Kids", new NamedQueryResult(new BaseItemDto { Name = "[HUB] Kids" }).Title);
			Assert.Equal("[HUB] Movies", CollectionNames.DisplayView(new BaseItemDto { Name = "[HUB] Movies", Type = BaseItemDto_Type.CollectionFolder }));
		}
		finally
		{
			CollectionNames.RememberLibraries([]);
		}
	}

	// 1.6 integration: the top bar's library tabs (its row and the Libraries menu) and the Libraries page's tiles (the name, what
	// Narrator reads, the star) say the tidy library names, unique as the libraries are; the setting off gives the server's back
	[Fact]
	public void TheTopBarsTabs_AndTheLibrariesPagesTiles_SayTheTidyLibraryNames()
	{
		static BaseItemDto Library(string name) =>
			new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var movies = Library("Movies");
		var hubMovies = Library("[HUB] Movies");
		var kids = Library("[HUB] Kids Movies");
		try
		{
			CollectionNames.RememberLibraries([movies.Name, hubMovies.Name, kids.Name]);
			var tabs = TopTabs.Build([movies, hubMovies, kids], [kids.Id!.Value, hubMovies.Id!.Value]);

			Assert.Equal(["Home", "Kids Movies", "[HUB] Movies", "Collections", "Libraries"], tabs.Select(t => t.Title));
			Assert.Equal([TopTabs.AllLibraries, "Movies", "[HUB] Movies", "Kids Movies"], tabs[^1].Menu.Select(t => t.Title));
			var tile = new LibraryTile(kids);
			Assert.Equal("Kids Movies", tile.Name);
			Assert.StartsWith("Kids Movies", tile.AutomationName);
			Assert.Equal("Add Kids Movies to the top bar", tile.StarName);
			Assert.Equal("[HUB] Movies", new LibraryTile(hubMovies).Name);

			CollectionNames.Tidy = false;
			Assert.Equal("[HUB] Kids Movies", TopTabs.Build([kids], [kids.Id!.Value])[1].Title);
			Assert.Equal("[HUB] Kids Movies", new LibraryTile(kids).Name);
		}
		finally
		{
			CollectionNames.Tidy = true;
			CollectionNames.RememberLibraries([]);
		}
	}

	[Fact]
	public void LibraryNames_AreTidied_AndCollisionsKeepTheServersNames()
	{
		CollectionNames.RememberLibraries([]);
		Assert.Equal("Trending Movies", CollectionNames.DisplayLibrary("[HUB] Trending Movies"));
		Assert.Equal("Movies", CollectionNames.DisplayLibrary("Movies"));
		Assert.Equal("", CollectionNames.DisplayLibrary(null));
		// a library is never TMDb-named: "Collection" stays
		Assert.Equal("Criterion Collection", CollectionNames.DisplayLibrary("[HUB] Criterion Collection"));
		Assert.Equal(["Movies", "[HUB] Movies", "Kids Movies"], CollectionNames.DisplayAllLibraries(["Movies", "[HUB] Movies", "[HUB] Kids Movies"]));
	}

	[Fact]
	public void NothingReadableLeft_ShowsTheServersName()
	{
		Assert.Equal("[HUB] The", CollectionNames.Clean("[HUB] The", fromTmdb: false));
		Assert.Equal("🎅 🎄", CollectionNames.Clean("🎅 🎄", fromTmdb: false));
		Assert.Equal("", CollectionNames.Clean("", fromTmdb: false));
	}

	[Fact]
	public void TheEmojiTable_MatchesUnicodesEmojiPresentation()
	{
		// Emoji_Presentation=Yes in emoji-data.txt (Unicode 16.0): samples from each block, and its neighbours that are not
		foreach (var yes in new[] { 0x231A, 0x23F3, 0x2614, 0x2648, 0x26A1, 0x26BD, 0x2705, 0x2728, 0x274C, 0x2B50, 0x1F004, 0x1F1E6, 0x1F1FF, 0x1F300, 0x1F385, 0x1F3C3, 0x1F600, 0x1F64F, 0x1F680, 0x1F7E0, 0x1F90C, 0x1F9FF, 0x1FA70, 0x1FAF8 })
		{
			Assert.True(Emoji.IsEmojiPresentation(yes), $"U+{yes:X4} is emoji by default");
		}

		// ☆ ★ ♪ ♡ ❤ ☺ ™ © and the digits are text by default
		foreach (var no in new[] { 0x2606, 0x2605, 0x266A, 0x2661, 0x2764, 0x263A, 0x2122, 0x00A9, 0x0031, 0x23F1, 0x2B51, 0x1F321, 0x1F336, 0x1F37D, 0x1F441, 0x1F6C6 })
		{
			Assert.False(Emoji.IsEmojiPresentation(no), $"U+{no:X4} is text by default");
		}
	}

	[Theory]
	[InlineData(BaseItemDto_Type.Movie)]
	[InlineData(BaseItemDto_Type.Series)]
	[InlineData(BaseItemDto_Type.Season)]
	[InlineData(BaseItemDto_Type.Episode)]
	[InlineData(BaseItemDto_Type.Playlist)]
	[InlineData(BaseItemDto_Type.MusicAlbum)]
	public void OtherItems_NeverReachTheCleaner(BaseItemDto_Type type)
	{
		var before = CollectionNames.CleanerCalls;
		foreach (var title in new[] { "[REC]", "#Alive", "+1", "!Women Art Revolution", "[HUB] Marvel", "🎅 Christmas", "!010_Bond", "zz_Old" })
		{
			Assert.Equal(title, CollectionNames.Display(new BaseItemDto { Name = title, Type = type }));
		}

		Assert.Equal(before, CollectionNames.CleanerCalls);
		CollectionNames.Display(BoxSet("[HUB] Marvel"));
		Assert.Equal(before + 1, CollectionNames.CleanerCalls);
	}

	[Fact]
	public void TheSettingOff_ShowsEveryNameAsTheServerHasIt()
	{
		try
		{
			CollectionNames.Tidy = false;
			Assert.Equal("[HUB] Marvel", CollectionNames.Display(BoxSet("[HUB] Marvel")));
			Assert.Equal("[HUB] Trending Movies", CollectionNames.DisplayLibrary("[HUB] Trending Movies"));
			Assert.Equal(["[HUB] Marvel"], CollectionNames.DisplayAll([BoxSet("[HUB] Marvel")]));
		}
		finally
		{
			CollectionNames.Tidy = true;
		}
	}
}
