using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Outside;

namespace FluentFin.Core.Tests.Outside;

/// <summary>1.7 keyless: a handler that answers with a function (each request counted), for the outside layer's tests.</summary>
internal sealed class FuncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
	private int _calls;

	public FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : this((r, _) => Task.FromResult(answer(r))) { }

	public int Calls => _calls;

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		Interlocked.Increment(ref _calls);
		return answer(request, cancellationToken);
	}
}

/// <summary>A settings store in memory (a new install's: nothing saved).</summary>
internal sealed class MemoryStore : ILocalSettingsService
{
	public readonly Dictionary<string, JsonNode?> Values = [];

	public JsonNode? ReadSettingRaw(string key) => Values.GetValueOrDefault(key);

	public T ReadSetting<T>(string key, T defaultValue) => Values.TryGetValue(key, out var v) && v is not null ? v.Deserialize<T>()! : defaultValue;

	public void SaveSetting<T>(string key, T value) => Values[key] = JsonSerializer.SerializeToNode(value);

	public void RemoveSetting(string key) => Values.Remove(key);

	public byte[] GetEntropyBytes() => [];
}

/// <summary>A clock the test moves.</summary>
internal sealed class TestClock(DateTimeOffset start)
{
	public DateTimeOffset Now { get; set; } = start;

	public Func<DateTimeOffset> Func => () => Now;
}

internal static class OutsideKit
{
	public static readonly DateTimeOffset Noon = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

	public static HttpResponseMessage Answer(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? tweak = null)
	{
		var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
		tweak?.Invoke(response);
		return response;
	}

	public static OutsideSource Source(HttpMessageHandler handler, TestClock clock, OutsideSourceInfo? info = null, OutsideRouting? routing = null) =>
		new(info ?? OutsideSources.Wikidata, new HttpClient(handler), routing ?? OutsideRouting.Real, "1.7.0", clock.Func);

	public static string TempFolder()
	{
		var folder = Path.Combine(Path.GetTempPath(), "nixie-outside-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		return folder;
	}
}
