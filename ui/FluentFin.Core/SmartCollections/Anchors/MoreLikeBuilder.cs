using System.Diagnostics;
using System.Globalization;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>
/// Nixie 1.8 (P1-2): "More like &lt;title&gt;" (the report's "Because you watched", said of the title): the titles of its kind
/// not watched yet that share an anchor's rare features (<see cref="TitleSimilarity"/>), its own franchise left to "Next
/// in". The <see cref="Considered"/> most alike, ordered by similarity times quality (the report's order), the first
/// <see cref="Most"/>; made only with <see cref="Least"/> or more alike. One a recent anchor, in the slots' order (the
/// lineup takes up to two). "Shares space, anime and Megumi Hayashibara · 12 shows".
/// </summary>
public sealed class MoreLikeBuilder(Func<BuildContext, IAnchorSource>? source = null) : ISmartCollectionBuilder
{
	public const string IdPrefix = "like:";

	/// <summary>The gate table's size floor: unwatched titles alike.</summary>
	public const int Least = 8;

	/// <summary>What the collection shows.</summary>
	public const int Most = 20;

	/// <summary>The most alike that quality may reorder: past these, a better rating doesn't make a title alike.</summary>
	public const int Considered = 60;

	/// <summary>The shared features the reason line names, of the first <see cref="ReasonFrom"/> titles.</summary>
	public const int Named = 3;

	public const int ReasonFrom = 8;

	public SmartCollectionKind Kind => SmartCollectionKind.MoreLike;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		// a library's presets strip builds every published kind over one library's titles (Whole): a personal slot is no chip
		if (context.Whole)
		{
			return [];
		}

		var watch = Stopwatch.StartNew();
		var data = AnchoredSlots.DataOf(context, (source ?? IndexAnchorSource.For)(context));
		var anchors = data.Ordered.Take(AnchorFinder.Recent).ToList();
		var made = anchors.Select(anchor => For(context, data, anchor)).OfType<SmartCollection>().ToList();
		context.Gates.Count(Kind, anchors.Count, made.Count, made.Count);
		if (anchors.Count > 0)
		{
			// rule 10: its own time in the census line (the similarity is made here, once an index read)
			context.Gates.Note(Kind, string.Create(CultureInfo.InvariantCulture, $"{anchors.Count} anchors, {watch.ElapsedMilliseconds} ms"));
		}

		return made;
	}

	/// <summary>The collection of one anchor; null when fewer than <see cref="Least"/> titles are alike.</summary>
	internal static SmartCollection? For(BuildContext context, AnchorData data, Anchor anchor)
	{
		var title = anchor.Title;
		// review (Minor, stream a): not watched by the anchors' own rule (a show at 90 % of its episodes is watched)
		var like = data.LikeOf(title); // 1.8 (stream l): and not its own franchise; made once an index read
		if (like.Count < Least)
		{
			return null;
		}

		var shown = like.Take(Considered)
			.OrderByDescending(x => x.Similarity * data.ScoreOf(x.Title))
			.ThenBy(x => x.Title.OrderName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => x.Title.Id)
			.Take(Most)
			.Select(x => x.Title)
			.ToList();
		var shared = data.Similarity.Shared(title, shown.Take(ReasonFrom), Named).Select(x => x.Label).ToList();
		// the count alone: every title is unwatched, and the title says what they are like
		var count = ReasonCount.OfAll(shown);
		var reason = shared.Count > 0 ? ReasonLine.Of($"Shares {AnchoredSlots.Listed(shared)}", count) : new ReasonLine { Count = count };
		// its order is the collection's own on its page (the most alike and best first), not A to Z
		// 1.8.1 (the owner's library, fix 1): its anchor's date, which the lineup ranks the personal picks by
		return Make.Of(context, $"{IdPrefix}{title.Id:N}", SmartCollectionKind.MoreLike, NixieTitles.MoreLike(title.Name), reason, hasOwnOrder: false, shown)
			with { InListOrder = true, AnchoredAt = anchor.When };
	}
}
