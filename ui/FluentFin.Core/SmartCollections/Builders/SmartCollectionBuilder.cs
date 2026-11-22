using System.Globalization;

namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>What every builder reads: the index, the date (the seasonal row, "a month ago") and the franchises the admin boxed.</summary>
/// <param name="BoxedFranchises">TMDb collection ids an admin's box set already has (the franchise shows once, as the box set).</param>
public sealed record BuildContext(IndexSnapshot Index, DateTimeOffset Now, IReadOnlySet<string> BoxedFranchises)
{
	public static BuildContext For(IndexSnapshot index, DateTimeOffset now) => new(index, now, new HashSet<string>());
}

/// <summary>Nixie 1.6: one kind of Nixie collection (the plan's 1.3), built from the index alone. Thresholds are the plan's first guesses.</summary>
public interface ISmartCollectionBuilder
{
	SmartCollectionKind Kind { get; }

	IEnumerable<SmartCollection> Build(BuildContext context);
}

/// <summary>Helpers the builders share.</summary>
internal static class Make
{
	/// <summary>A collection of these titles, in this order, with its cover (the first four with a poster).</summary>
	public static SmartCollection Of(string id, SmartCollectionKind kind, string title, string why, bool hasOwnOrder, IReadOnlyList<IndexEntry> items) =>
		new(id, kind, title, why, hasOwnOrder, [.. items.Select(x => x.Id)])
		{
			Cover = [.. items.Where(x => x.PrimaryTag is not null).Concat(items.Where(x => x.PrimaryTag is null)).Take(4)],
			Only = items.Select(x => x.Kind).Distinct().Count() == 1 ? items[0].Kind : null,
		};

	/// <summary>By name, the way a library lists them.</summary>
	public static List<IndexEntry> ByName(IEnumerable<IndexEntry> items) =>
		[.. items.OrderBy(x => x.OrderName, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Year)];

	/// <summary>Oldest first: release order.</summary>
	public static List<IndexEntry> ByRelease(IEnumerable<IndexEntry> items) =>
		[.. items.OrderBy(x => x.PremiereDate ?? (x.Year is { } y ? $"{y:D4}" : "9999")).ThenBy(x => x.OrderName, StringComparer.CurrentCultureIgnoreCase)];

	/// <summary>A stable id part from a name: lower case, letters and digits only ("Walt Disney Pictures" → "waltdisneypictures").</summary>
	public static string Key(string name) => new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

	public static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

	public static string Noun(IndexKind kind, int count) => kind == IndexKind.Movie ? count == 1 ? "film" : "films" : count == 1 ? "show" : "shows";
}
