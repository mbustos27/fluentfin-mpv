using System.Globalization;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7 (the owner, 2026-11-22): what Nixie keeps of each source, in the words Settings' "What is sent" cards and PRIVACY.md
/// both use (PrivacyPolicyTests pins them to each other): Nixie's own copy of public lists, not the user's data, with how
/// often it is refreshed and how long the last copy is used when the source can't be reached. Made from the sources' own
/// rules (<see cref="OutsideSources"/>), so the words can't drift from what the code does.
/// </summary>
public static class OutsideKeep
{
	/// <summary>"Nixie keeps its own copy of these public lists. Refreshed weekly; the last copy is used for up to 6 months if TMDb can't be reached."</summary>
	public static string Describe(OutsideSourceId id)
	{
		var info = OutsideSources.Get(id);
		var refreshed = Every(info.RefreshAfter) + (id == OutsideSourceId.Trakt ? " (the box office daily, and after its Monday update)" : "");
		return $"Nixie keeps its own copy of these public lists. Refreshed {refreshed}; the last copy is used for up to {Span(info.MaxAge)} if {Service(id)} can't be reached.";
	}

	/// <summary>"every 6 hours", "daily", "weekly", "every 3 days".</summary>
	internal static string Every(TimeSpan refresh) => refresh switch
	{
		{ TotalDays: 1 } => "daily",
		{ TotalDays: 7 } => "weekly",
		{ TotalDays: < 1 } => $"every {refresh.TotalHours.ToString("0", CultureInfo.InvariantCulture)} hours",
		_ => $"every {refresh.TotalDays.ToString("0", CultureInfo.InvariantCulture)} days",
	};

	/// <summary>"24 hours", "7 days", "60 days", "6 months" (182 days).</summary>
	internal static string Span(TimeSpan age) => age.TotalDays switch
	{
		<= 1 => $"{age.TotalHours.ToString("0", CultureInfo.InvariantCulture)} hours",
		>= 180 => $"{Math.Round(age.TotalDays / 30.4).ToString("0", CultureInfo.InvariantCulture)} months",
		_ => $"{age.TotalDays.ToString("0", CultureInfo.InvariantCulture)} days",
	};

	/// <summary>The service as the user knows it: both TMDb sources are "TMDb".</summary>
	private static string Service(OutsideSourceId id) => id switch
	{
		OutsideSourceId.Trakt => "Trakt",
		OutsideSourceId.Wikidata => "Wikidata",
		_ => "TMDb",
	};
}
