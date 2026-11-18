using System.Text;
using System.Text.Json;
using Microsoft.Kiota.Abstractions.Serialization;

namespace FluentFin.Core.Mpv;

/// <summary>
/// 1.2.0 (the interaction audit, item 9): an item's trickplay thumbnails, as the server lists them in the item's
/// <c>Trickplay</c> field (Jellyfin 10.9+): sheets of <see cref="TileWidth"/> x <see cref="TileHeight"/> thumbnails of
/// <see cref="Width"/> x <see cref="Height"/> pixels, one every <see cref="IntervalMs"/>, fetched as
/// <c>/Videos/{id}/Trickplay/{width}/{sheet}.jpg</c>. The seek bar shows the one under the pointer or the thumb, as
/// Netflix, Plex, jellyfin-web and mpv's own bar here do. docs/BRIDGE.md §6: with the UI's controls the UI asks the server for
/// the tiles itself.
/// </summary>
public sealed record TrickplayTiles(int Width, int Height, int TileWidth, int TileHeight, int ThumbnailCount, int IntervalMs)
{
	/// <summary>The thumbnail width the controls prefer: the smallest the server has at least this wide, else its widest.</summary>
	public const int PreferredWidth = 240;

	private int PerSheet => TileWidth * TileHeight;

	/// <summary>Usable: every number positive.</summary>
	public bool IsValid => Width > 0 && Height > 0 && TileWidth > 0 && TileHeight > 0 && ThumbnailCount > 0 && IntervalMs > 0;

	/// <summary>A sheet's width in pixels (every sheet is that wide; the last may have fewer rows).</summary>
	public int SheetWidth => TileWidth * Width;

	/// <summary>
	/// The thumbnail for <paramref name="ticks"/>: its sheet and its top-left corner in that sheet's pixels. The last one
	/// past the end, the first before the start; null when not usable.
	/// </summary>
	public (int Sheet, int X, int Y)? Locate(long ticks)
	{
		if (!IsValid)
		{
			return null;
		}

		var index = (int)Math.Clamp(Math.Max(0, ticks) / TimeSpan.TicksPerMillisecond / IntervalMs, 0, ThumbnailCount - 1);
		var within = index % PerSheet;
		return (index / PerSheet, within % TileWidth * Width, within / TileWidth * Height);
	}

	/// <summary>
	/// The tiles from the item's <c>Trickplay</c> field as JSON (<c>{"&lt;mediaSourceId&gt;": {"&lt;width&gt;": {"Width": ...,
	/// "Height", "TileWidth", "TileHeight", "ThumbnailCount", "Interval"}}}</c>): the media source <paramref name="itemId"/>
	/// names (an item's own source has its id) or else the first, at the width nearest <see cref="PreferredWidth"/> from
	/// above. Null when there is none or it is not readable. Never throws.
	/// </summary>
	public static TrickplayTiles? Parse(string? json, string? itemId = null)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			JsonElement? source = null;
			foreach (var entry in doc.RootElement.EnumerateObject())
			{
				if (entry.Value.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				if (source is null || (itemId is not null && SameId(entry.Name, itemId)))
				{
					source = entry.Value;
				}
			}

			if (source is not { } widths)
			{
				return null;
			}

			var candidates = new List<TrickplayTiles>();
			foreach (var entry in widths.EnumerateObject())
			{
				if (entry.Value.ValueKind == JsonValueKind.Object
					&& new TrickplayTiles(Int(entry.Value, "Width"), Int(entry.Value, "Height"), Int(entry.Value, "TileWidth"), Int(entry.Value, "TileHeight"),
						Int(entry.Value, "ThumbnailCount"), Int(entry.Value, "Interval")) is { IsValid: true } tiles)
				{
					candidates.Add(tiles);
				}
			}

			return candidates.Where(x => x.Width >= PreferredWidth).OrderBy(x => x.Width).FirstOrDefault()
				?? candidates.OrderByDescending(x => x.Width).FirstOrDefault();
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// <see cref="Parse"/> of the item DTO's <c>Trickplay</c> as the SDK hands it: Kiota's additional data (untyped nodes, or
	/// JSON elements from an older serializer). Never throws.
	/// </summary>
	public static TrickplayTiles? FromAdditionalData(IDictionary<string, object>? data, string? itemId = null)
	{
		if (data is not { Count: > 0 })
		{
			return null;
		}

		try
		{
			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				foreach (var (key, value) in data)
				{
					writer.WritePropertyName(key);
					Write(writer, value);
				}

				writer.WriteEndObject();
			}

			return Parse(Encoding.UTF8.GetString(stream.ToArray()), itemId);
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	private static void Write(Utf8JsonWriter writer, object? value)
	{
		switch (value)
		{
			case null or UntypedNull: writer.WriteNullValue(); break;
			case JsonElement element: element.WriteTo(writer); break;
			case UntypedObject obj:
				writer.WriteStartObject();
				foreach (var (key, node) in obj.GetValue())
				{
					writer.WritePropertyName(key);
					Write(writer, node);
				}

				writer.WriteEndObject();
				break;
			case UntypedArray array:
				writer.WriteStartArray();
				foreach (var node in array.GetValue())
				{
					Write(writer, node);
				}

				writer.WriteEndArray();
				break;
			case UntypedInteger n: writer.WriteNumberValue(n.GetValue()); break;
			case UntypedLong n: writer.WriteNumberValue(n.GetValue()); break;
			case UntypedDouble n: writer.WriteNumberValue(n.GetValue()); break;
			case UntypedFloat n: writer.WriteNumberValue(n.GetValue()); break;
			case UntypedDecimal n: writer.WriteNumberValue(n.GetValue()); break;
			case UntypedBoolean b: writer.WriteBooleanValue(b.GetValue()); break;
			case UntypedString text: writer.WriteStringValue(text.GetValue()); break;
			case int n: writer.WriteNumberValue(n); break;
			case long n: writer.WriteNumberValue(n); break;
			case double n: writer.WriteNumberValue(n); break;
			case bool b: writer.WriteBooleanValue(b); break;
			case string text: writer.WriteStringValue(text); break;
			case IDictionary<string, object> map:
				writer.WriteStartObject();
				foreach (var (key, node) in map)
				{
					writer.WritePropertyName(key);
					Write(writer, node);
				}

				writer.WriteEndObject();
				break;
			default: writer.WriteNullValue(); break;
		}
	}

	private static int Int(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

	private static bool SameId(string a, string b) =>
		string.Equals(a.Replace("-", "", StringComparison.Ordinal), b.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
}
