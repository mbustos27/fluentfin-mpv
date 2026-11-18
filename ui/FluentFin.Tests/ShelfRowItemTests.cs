using FluentFin.Controls;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.0: a shared row's card is its item's DataContext, so a layout theme's {Binding} card template (PosterTemplate,
/// StillTemplate) is never empty in a ShelfRow (Drive-in found its cards empty and used x:Bind copies).
/// </summary>
public sealed class ShelfRowItemTests
{
	[Fact]
	public void A_prepared_card_is_given_its_item()
	{
		var source = File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Controls", "ShelfRow.xaml.cs")));
		var prepared = source[source.IndexOf("Repeater.ElementPrepared +=", StringComparison.Ordinal)..];
		Assert.Contains("GiveItem(e.Element, e.Index)", prepared[..prepared.IndexOf("};", StringComparison.Ordinal)], StringComparison.Ordinal);
	}

	[Fact]
	public void A_card_takes_its_item_once()
	{
		var item = new object();
		Assert.Same(item, ShelfRow.NeedsItem(null, item));
		Assert.Same(item, ShelfRow.NeedsItem(new object(), item));
		Assert.Null(ShelfRow.NeedsItem(item, item));
		Assert.Null(ShelfRow.NeedsItem(null, null));
	}
}
