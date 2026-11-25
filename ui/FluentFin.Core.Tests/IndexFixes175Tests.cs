using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.7.5 (stream fx1): the library index's Minors from 1.7.3's reviews (stream I's) and the bug hunt's BH-171-m5.</summary>
public sealed class IndexFixes175Tests
{
	[Fact]
	public void TheReadingLine_SaysTitles_WhileItsTotalIsOnlyAbout()
	{
		// stream I's review: the check's count is every film and show once, in libraries the index skips too: "of about
		// 34,006 films" counted shows, then read "of 37,173 films" once each library's count was in
		Assert.Equal("200 of about 34,006 titles", new IndexProgress(200, 34_006, IndexKind.Movie) { About = true }.Text);
		Assert.Equal("200 of about 900 titles", new IndexProgress(200, 900, IndexKind.Series) { About = true }.Text);
		Assert.Equal("200 of about 900 titles", new IndexProgress(200, 900, null) { About = true }.Text);
		// every library's own count in: their kind again
		Assert.Equal("12,000 of 37,173 films", new IndexProgress(12_000, 37_173, IndexKind.Movie).Text);
		Assert.Equal("300 of 1,200 shows", new IndexProgress(300, 1_200, IndexKind.Series).Text);
		Assert.Equal("300 of 1,200 titles", new IndexProgress(300, 1_200, null).Text);
		Assert.Equal("12,000 films", new IndexProgress(12_000, null, IndexKind.Movie).Text);
	}
}
