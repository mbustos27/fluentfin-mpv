using System.Text.Json;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;

namespace FluentFin.Core.Admin;

/// <summary>
/// Nixie 1.2.0 (admin): a deep copy of an SDK model (written and read back as the JSON it is sent as), so a form can edit a
/// copy and replace the original only once the server took it.
/// </summary>
public static class KiotaCopy
{
	public static T Clone<T>(T value, ParsableFactory<T> factory) where T : IParsable
	{
		using var writer = new JsonSerializationWriter();
		writer.WriteObjectValue(null, value);
		using var content = writer.GetSerializedContent();
		using var document = JsonDocument.Parse(content);
		return new JsonParseNode(document.RootElement).GetObjectValue(factory)!;
	}
}
