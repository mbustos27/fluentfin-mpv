using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using Microsoft.Kiota.Abstractions.Serialization;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (the interaction audit, item 9): the seek bar's thumbnails: the item's trickplay tiles read from its <c>Trickplay</c>
/// field, the tile for a time, and what the controls hand the view (<see cref="MpvControlsViewModel.PreviewAt"/>).
/// </summary>
public sealed class TrickplayTilesTests
{
	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";

	// Jellyfin's layout as the fake server serves it: 10 x 10 tiles of 320 x 180, one every 10 s
	private const string Json = """
		{"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1": {
			"320": {"Width": 320, "Height": 180, "TileWidth": 10, "TileHeight": 10, "ThumbnailCount": 60, "Interval": 10000, "Bandwidth": 2000000},
			"640": {"Width": 640, "Height": 360, "TileWidth": 10, "TileHeight": 10, "ThumbnailCount": 60, "Interval": 10000, "Bandwidth": 4000000},
			"160": {"Width": 160, "Height": 90, "TileWidth": 10, "TileHeight": 10, "ThumbnailCount": 60, "Interval": 10000, "Bandwidth": 1000000}}}
		""";

	private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

	[Fact]
	public void Parse_picks_the_smallest_width_from_240_up()
	{
		Assert.Equal(new TrickplayTiles(320, 180, 10, 10, 60, 10000), TrickplayTiles.Parse(Json, Item));
		Assert.Equal(160, TrickplayTiles.Parse("""{"x": {"160": {"Width": 160, "Height": 90, "TileWidth": 10, "TileHeight": 10, "ThumbnailCount": 5, "Interval": 1000}}}""")!.Width);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("[]")]
	[InlineData("{}")]
	[InlineData("""{"x": {"320": {"Width": 320}}}""")]
	public void Parse_of_nothing_usable_is_null(string? json) => Assert.Null(TrickplayTiles.Parse(json, Item));

	[Theory]
	[InlineData(0, 0, 0, 0)]
	[InlineData(9.9, 0, 0, 0)]
	[InlineData(10, 0, 320, 0)]
	[InlineData(95, 0, 2880, 0)]
	[InlineData(100, 0, 0, 180)]
	[InlineData(999, 0, 2880, 1620)]
	[InlineData(1000, 1, 0, 0)]
	[InlineData(-5, 0, 0, 0)]
	public void Locate_finds_the_sheet_and_the_corner(double seconds, int sheet, int x, int y)
	{
		var tiles = new TrickplayTiles(320, 180, 10, 10, 200, 10000);
		Assert.Equal((sheet, x, y), tiles.Locate(Ticks(seconds)));
	}

	[Fact]
	public void Locate_past_the_end_is_the_last_thumbnail()
	{
		var tiles = new TrickplayTiles(320, 180, 10, 10, 60, 10000);
		Assert.Equal((0, 2880, 900), tiles.Locate(Ticks(5000)));
		Assert.Null(new TrickplayTiles(320, 180, 10, 10, 0, 10000).Locate(0));
	}

	[Fact]
	public void From_the_SDKs_untyped_additional_data()
	{
		static UntypedObject Info(int width, int height) => new(new Dictionary<string, UntypedNode>
		{
			["Width"] = new UntypedInteger(width),
			["Height"] = new UntypedInteger(height),
			["TileWidth"] = new UntypedInteger(10),
			["TileHeight"] = new UntypedInteger(10),
			["ThumbnailCount"] = new UntypedInteger(60),
			["Interval"] = new UntypedInteger(10000),
			["Bandwidth"] = new UntypedLong(2_000_000),
		});

		var data = new Dictionary<string, object>
		{
			["ffffffffffffffffffffffffffffffff"] = new UntypedObject(new Dictionary<string, UntypedNode> { ["640"] = Info(640, 360) }),
			[Item] = new UntypedObject(new Dictionary<string, UntypedNode> { ["320"] = Info(320, 180) }),
		};

		Assert.Equal(new TrickplayTiles(320, 180, 10, 10, 60, 10000), TrickplayTiles.FromAdditionalData(data, Item));
		Assert.Null(TrickplayTiles.FromAdditionalData(null, Item));
		Assert.Null(TrickplayTiles.FromAdditionalData(new Dictionary<string, object>(), Item));
	}

	[Fact]
	public async Task The_controls_load_the_items_tiles_and_hand_out_the_preview()
	{
		var asked = new List<string>();
		var vm = new MpvControlsViewModel((_, _) => Task.CompletedTask)
		{
			TrickplayOf = id =>
			{
				asked.Add(id);
				return id == Item ? Task.FromResult(TrickplayTiles.Parse(Json, id)) : new TaskCompletionSource<TrickplayTiles?>().Task;
			},
			TrickplaySheetAt = (id, width, sheet) => new Uri($"https://jf.example/Videos/{id}/Trickplay/{width}/{sheet}.jpg"),
		};
		Assert.Null(vm.PreviewAt(0));

		vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""{"itemId": "{{Item}}", "name": "Pilot", "type": "Episode"}""")));
		await Task.Yield();

		Assert.Equal([Item], asked);
		var preview = vm.PreviewAt(Ticks(25));
		Assert.NotNull(preview);
		Assert.Equal(new Uri($"https://jf.example/Videos/{Item}/Trickplay/320/0.jpg"), preview.Value.Sheet);
		Assert.Equal((640, 0), (preview.Value.X, preview.Value.Y));

		// another item: none until its own are known
		vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse("""{"itemId": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2", "name": "Two", "type": "Episode"}""")));
		Assert.Equal(2, asked.Count);
		Assert.Null(vm.PreviewAt(Ticks(25)));
	}
}
