using System.Security.Cryptography;
using System.Text;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the review of wip/190-wire, Minor 6): what the library index keeps of a title's tags is
/// <see cref="Keywords.Matchable"/> (the allowlist, the seasonal ones and Data\keywords-matchable.json), and a saved index's
/// revision says which list it kept. Nothing tied the list to <see cref="LibraryIndex.FormatRevision"/>: a list changed without a
/// new revision would leave every saved index's tags of the old list, its categories matched on keywords it never kept.
/// </summary>
public class KeptKeywordsTests
{
	/// <summary>The first 16 hex digits of the SHA-256 of the keywords, lower case, sorted ordinally, one a line.</summary>
	internal static string HashOf(IEnumerable<string> keywords)
	{
		var text = string.Join("\n", keywords.Select(x => x.Trim().ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
	}

	[Fact]
	public void TheKeptKeywords_AreTheOnesFormatRevisionWasMadeFor()
	{
		var count = Keywords.Matchable.Count;
		var hash = HashOf(Keywords.Matchable);

		Assert.True(count == LibraryIndex.KeptKeywordsCount && hash == LibraryIndex.KeptKeywordsHash,
			$"The keywords the library index keeps changed ({count} keywords, hash {hash}; revision {LibraryIndex.FormatRevision} was made for "
			+ $"{LibraryIndex.KeptKeywordsCount}, {LibraryIndex.KeptKeywordsHash}). Raise LibraryIndex.FormatRevision (a saved index of the old "
			+ $"list is then kept until its day's read, its categories on the new keywords waiting), and set KeptKeywordsCount = {count} and "
			+ $"KeptKeywordsHash = \"{hash}\" beside it.");
	}

	[Fact]
	public void TheHash_ChangesWithTheList_NotWithItsCaseOrOrder()
	{
		Assert.Equal(HashOf(["heist", "office"]), HashOf(["Office ", "heist", "office"]));
		Assert.NotEqual(HashOf(["heist", "office"]), HashOf(["heist", "offices"]));
		Assert.NotEqual(HashOf(Keywords.Matchable), HashOf(Keywords.Matchable.Where(x => x != "thanksgiving")));
	}
}
