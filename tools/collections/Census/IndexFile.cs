using System.Text.Json;
using FluentFin.Core.SmartCollections;

namespace Nixie.Collections;

/// <summary>
/// A saved library index (LibraryIndex's file, format 1 or 2), read with the app's own IndexEntry: the titles, copies collapsed
/// as every builder counts them (CanonicalTitles), and the played-episodes pass. Read only; the census never writes it.
/// </summary>
public sealed class IndexFile
{
	private sealed record Saved(int Version, List<IndexEntry>? Entries, Dictionary<Guid, SeriesPlays>? SeriesPlays, int Revision = 0);

	private IndexFile(CanonicalTitles titles, IReadOnlyDictionary<Guid, SeriesPlays> plays, int revision)
	{
		Titles = titles;
		Plays = plays;
		Revision = revision;
	}

	public CanonicalTitles Titles { get; }

	public IReadOnlyDictionary<Guid, SeriesPlays> Plays { get; }

	/// <summary>The saved index's revision (0 when it says none): 4 and later keep every matchable keyword of a title's tags.</summary>
	public int Revision { get; }

	/// <summary>
	/// What this index kept of a title's tags, so a rule on anything else is "unknown": revision 4 (1.9) keeps the allowlist,
	/// the seasonal ones and Data\keywords-matchable.json (<see cref="Keywords.Matchable"/>); before it, the allowlist and the
	/// six seasonal ones (<see cref="Keywords.KeptByRevision3"/>).
	/// </summary>
	public Func<string, bool> Kept => Revision >= 4
		? keyword => Keywords.Matchable.Contains(keyword.Trim().ToLowerInvariant())
		: Keywords.KeptByRevision3;

	/// <summary>Reads a saved index, sharing the file with whoever has it open (FileShare.ReadWrite).</summary>
	public static IndexFile Read(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		var saved = JsonSerializer.Deserialize<Saved>(stream) ?? throw new JsonException("empty index");
		return Of(saved.Entries ?? [], saved.SeriesPlays, saved.Revision);
	}

	public static IndexFile Of(IReadOnlyList<IndexEntry> entries, IReadOnlyDictionary<Guid, SeriesPlays>? plays = null, int revision = 0) =>
		new(CanonicalTitles.Of(entries), plays ?? new Dictionary<Guid, SeriesPlays>(), revision);
}
