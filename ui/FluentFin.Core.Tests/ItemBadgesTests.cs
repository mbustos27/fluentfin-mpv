using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.0 (the library UX review, X10): a card's two marks. One pink capsule meant both "watched" and "N to watch"; now a
/// watched check shows only when nothing is left, a count chip only when something is, never both.
/// </summary>
public sealed class ItemBadgesTests
{
	private static BaseItemDto Item(bool? played, int? unplayed) => new() { UserData = new UserItemDataDto { Played = played, UnplayedItemCount = unplayed } };

	[Theory]
	[InlineData(true, null, true, "")]       // a film, watched: the check alone
	[InlineData(true, 0, true, "")]          // a series, all watched
	[InlineData(false, null, false, "")]     // a film not watched: nothing
	[InlineData(false, 5, false, "5")]       // a series with 5 left: the count alone
	[InlineData(true, 3, false, "3")]        // played but new episodes came: the count, not the check
	[InlineData(false, 0, false, "")]
	[InlineData(false, 120, false, "99+")]   // the chip stays a small square
	[InlineData(false, 99, false, "99")]
	[InlineData(null, -2, false, "")]
	public void The_check_and_the_count_never_show_together(bool? played, int? unplayed, bool check, string count)
	{
		var dto = Item(played, unplayed);
		Assert.Equal(check, ItemBadges.Watched(dto));
		Assert.Equal(count, ItemBadges.ToWatchText(dto));
		Assert.False(ItemBadges.Watched(dto) && ItemBadges.ToWatch(dto) > 0);
	}

	[Fact]
	public void No_item_and_no_user_data_show_nothing()
	{
		Assert.False(ItemBadges.Watched((BaseItemDto?)null));
		Assert.Equal("", ItemBadges.ToWatchText((BaseItemDto?)null));
		Assert.False(ItemBadges.Watched(new BaseItemDto()));
		Assert.Equal(0, ItemBadges.ToWatch(new BaseItemDto()));
	}
}
