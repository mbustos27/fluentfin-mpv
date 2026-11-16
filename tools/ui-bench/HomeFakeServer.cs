// A Jellyfin-shaped server with a realistic home page behind it: 8 user views (Movies 2,000, Shows 300 series, Anime,
// Kids, Documentaries, 4K Movies, Music, Collections), continue watching (12), next up (24) and a "Latest" list per view
// (16 items; empty for Collections). Fake token only, 127.0.0.1 only. It records every request (server-side start/end,
// connection, bytes) and can add a server processing time per endpoint (see ServerMs), on top of what LatencyProxy adds.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

sealed record ServedRequest(double StartMs, double EndMs, string Method, string Path, string? ParentId, string Connection, long Bytes, int InFlightAtStart);

sealed class HomeFakeServer : IAsyncDisposable
{
	private WebApplication? _app;
	private readonly ConcurrentQueue<ServedRequest> _log = new();
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private int _inFlight;
	private int _maxInFlight;

	public sealed record View(Guid Id, string Name, string CollectionType, int Size);

	public List<View> Views { get; } =
	[
		new(Guid.NewGuid(), "Movies", "movies", 2000),
		new(Guid.NewGuid(), "Shows", "tvshows", 300),
		new(Guid.NewGuid(), "Anime", "tvshows", 80),
		new(Guid.NewGuid(), "Kids", "movies", 200),
		new(Guid.NewGuid(), "Documentaries", "movies", 150),
		new(Guid.NewGuid(), "4K Movies", "movies", 300),
		new(Guid.NewGuid(), "Music", "music", 900),
		new(Guid.NewGuid(), "Collections", "boxsets", 40),
	];

	/// <summary>Multiplies the server processing times below (0 = the server answers at once).</summary>
	public double ServerScale { get; set; } = 1;

	/// <summary>
	/// Assumed processing time of a real server with this library (not measured on one): Latest grows with the view's size
	/// (a TV view counts ~20 episodes per series), NextUp walks every series in progress.
	/// </summary>
	public double ServerMs(string path, string? parentId)
	{
		double ms = path switch
		{
			"/Items/Latest" => 20 + (Views.FirstOrDefault(v => v.Id.ToString("N") == parentId?.Replace("-", "")) is { } v ? (v.CollectionType == "tvshows" ? v.Size * 20 : v.Size) / 100.0 : 0),
			"/Shows/NextUp" => 60,
			"/UserItems/Resume" => 30,
			_ => 10,
		};
		return ms * ServerScale;
	}

	public int MaxInFlight => Volatile.Read(ref _maxInFlight);
	public double Now => _clock.Elapsed.TotalMilliseconds;

	public void Reset()
	{
		while (_log.TryDequeue(out _)) { }
		Interlocked.Exchange(ref _maxInFlight, 0);
	}

	/// <summary>Every request so far except the session websocket.</summary>
	public List<ServedRequest> Requests => [.. _log.Where(x => x.Path != "/socket")];

	private static readonly string[] Genres = ["Action", "Drama", "Comedy", "Thriller", "Horror", "Romance", "Sci-Fi", "Fantasy", "Animation", "Documentary"];
	private const string Blur = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";

	private static Dictionary<string, object?> Item(string type, string name, int seed, Guid? series = null, int? season = null, int? episode = null, bool resume = false)
	{
		var r = new Random(seed);
		var id = new Guid(r.Next(), (short)r.Next(), (short)r.Next(), [.. Enumerable.Range(0, 8).Select(_ => (byte)r.Next(256))]);
		var tag = id.ToString("N")[..8];
		var d = new Dictionary<string, object?>
		{
			["Name"] = name, ["ServerId"] = "fake", ["Id"] = id.ToString("N"), ["Type"] = type, ["IsFolder"] = type is "Series" or "Season" or "BoxSet",
			["PremiereDate"] = new DateTime(1980 + r.Next(45), 1 + r.Next(12), 1 + r.Next(28)).ToString("O") + "Z",
			["ProductionYear"] = 1980 + r.Next(45), ["CommunityRating"] = Math.Round(r.NextDouble() * 10, 1), ["OfficialRating"] = new[] { "PG", "PG-13", "R", "TV-MA" }[r.Next(4)],
			["RunTimeTicks"] = (long)r.Next(20, 180) * 60 * 10_000_000, ["ChannelId"] = null, ["VideoType"] = "VideoFile",
			["PrimaryImageAspectRatio"] = type == "Episode" ? 1.7777 : 0.6667, ["LocationType"] = "FileSystem", ["MediaType"] = type is "Movie" or "Episode" ? "Video" : "Unknown",
			["ImageTags"] = new Dictionary<string, string> { ["Primary"] = tag, ["Thumb"] = tag + "t" },
			["BackdropImageTags"] = new[] { tag + "b" },
			["ImageBlurHashes"] = new Dictionary<string, object> { ["Primary"] = new Dictionary<string, string> { [tag] = Blur }, ["Backdrop"] = new Dictionary<string, string> { [tag + "b"] = Blur }, ["Thumb"] = new Dictionary<string, string> { [tag + "t"] = Blur } },
			["UserData"] = new Dictionary<string, object?>
			{
				["PlaybackPositionTicks"] = resume ? (long)r.Next(5, 60) * 60 * 10_000_000 : 0, ["PlayedPercentage"] = resume ? r.Next(5, 90) : null,
				["PlayCount"] = r.Next(3), ["IsFavorite"] = r.Next(10) == 0, ["Played"] = false, ["Key"] = tag, ["ItemId"] = id.ToString("N"),
				["UnplayedItemCount"] = type == "Series" ? r.Next(0, 30) : null,
			},
		};
		if (type == "Series")
		{
			d["Status"] = r.Next(2) == 0 ? "Continuing" : "Ended";
			d["Genres"] = Genres.OrderBy(_ => r.Next()).Take(3).ToArray();
		}
		if (series is { } s)
		{
			d["SeriesId"] = s.ToString("N"); d["SeriesName"] = $"Show {seed % 300}"; d["SeriesPrimaryImageTag"] = "sp" + tag;
			d["SeasonId"] = Guid.NewGuid().ToString("N"); d["ParentIndexNumber"] = season; d["IndexNumber"] = episode;
			d["ParentThumbItemId"] = s.ToString("N"); d["ParentThumbImageTag"] = "pt" + tag; d["ParentBackdropItemId"] = s.ToString("N"); d["ParentBackdropImageTags"] = new[] { "pb" + tag };
		}
		return d;
	}

	private static byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);
	private static byte[] Query(IEnumerable<object> items) { var list = items.ToList(); return Json(new Dictionary<string, object> { ["Items"] = list, ["TotalRecordCount"] = list.Count, ["StartIndex"] = 0 }); }

	public async Task<int> StartAsync()
	{
		var views = Query(Views.Select(v => (object)new Dictionary<string, object?>
		{
			["Name"] = v.Name, ["ServerId"] = "fake", ["Id"] = v.Id.ToString("N"), ["Type"] = "CollectionFolder", ["CollectionType"] = v.CollectionType, ["IsFolder"] = true,
			["ImageTags"] = new Dictionary<string, string> { ["Primary"] = v.Id.ToString("N")[..8] }, ["BackdropImageTags"] = Array.Empty<string>(), ["LocationType"] = "FileSystem",
		}));
		var latest = new Dictionary<string, byte[]>();
		foreach (var (v, vi) in Views.Select((v, i) => (v, i)))
		{
			var items = v.CollectionType switch
			{
				"boxsets" => [],
				"tvshows" => Enumerable.Range(0, 16).Select(i => Item("Series", $"{v.Name} series {i}", vi * 1000 + i)).ToList(),
				_ => Enumerable.Range(0, 16).Select(i => Item("Movie", $"{v.Name} movie {i}", vi * 1000 + i)).ToList(),
			};
			latest[v.Id.ToString("N")] = Json(items);
		}
		var resume = Query(Enumerable.Range(0, 12).Select(i => (object)(i % 2 == 0 ? Item("Movie", $"Movie {i}", 50_000 + i, resume: true) : Item("Episode", $"Episode {i}", 50_000 + i, Guid.NewGuid(), 1 + i % 4, 1 + i, resume: true))));
		var nextUp = Query(Enumerable.Range(0, 24).Select(i => (object)Item("Episode", $"Episode {i + 2}", 60_000 + i, Guid.NewGuid(), 1 + i % 5, 2 + i)));

		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		_app = builder.Build();
		_app.UseWebSockets();
		_app.Use(async (ctx, next) =>
		{
			var start = Now;
			var path = ctx.Request.Path.Value ?? "";
			var parent = ctx.Request.Query["parentId"].FirstOrDefault() ?? ctx.Request.Query["ParentId"].FirstOrDefault();
			var socket = path == "/socket";
			var inFlight = socket ? 0 : Interlocked.Increment(ref _inFlight);
			int seen;
			while (inFlight > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, inFlight, seen) != seen) { }
			try
			{
				var ms = path == "/socket" ? 0 : ServerMs(path, parent);
				if (ms > 0)
				{
					await Task.Delay(TimeSpan.FromMilliseconds(ms));
				}
				var counter = new CountingStream(ctx.Response.Body);
				ctx.Response.Body = counter;
				await next();
				_log.Enqueue(new(start, Now, ctx.Request.Method, path, parent, ctx.Connection.Id, counter.Bytes, inFlight));
			}
			finally
			{
				if (!socket)
				{
					Interlocked.Decrement(ref _inFlight);
				}
			}
		});
		async Task Send(HttpContext ctx, byte[] body) { ctx.Response.ContentType = "application/json"; await ctx.Response.Body.WriteAsync(body); }
		_app.MapPost("/Sessions/Capabilities/Full", () => Results.NoContent());
		_app.MapGet("/Plugins", (HttpContext c) => Send(c, "[]"u8.ToArray()));
		_app.MapGet("/UserViews", (HttpContext c) => Send(c, views));
		_app.MapGet("/Items/Latest", (HttpContext c) =>
		{
			var pid = (c.Request.Query["parentId"].FirstOrDefault() ?? "").Replace("-", "");
			return Send(c, latest.TryGetValue(pid, out var body) ? body : "[]"u8.ToArray());
		});
		_app.MapGet("/UserItems/Resume", (HttpContext c) => Send(c, resume));
		_app.MapGet("/Shows/NextUp", (HttpContext c) => Send(c, nextUp));
		_app.Map("/socket", async (HttpContext c) =>
		{
			if (!c.WebSockets.IsWebSocketRequest)
			{
				c.Response.StatusCode = 400;
				return;
			}
			using var ws = await c.WebSockets.AcceptWebSocketAsync();
			var buffer = new byte[4096];
			try
			{
				while (ws.State == System.Net.WebSockets.WebSocketState.Open)
				{
					await ws.ReceiveAsync(buffer, c.RequestAborted);
				}
			}
			catch
			{
				// closed
			}
		});
		await _app.StartAsync();
		return new Uri(_app.Urls.First()).Port;
	}

	public async ValueTask DisposeAsync()
	{
		if (_app is not null)
		{
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}

	private sealed class CountingStream(Stream inner) : Stream
	{
		private readonly Stream _inner = inner;
		private long _bytes;
		public long Bytes => Interlocked.Read(ref _bytes);
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => _inner.Flush();
		public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) { Interlocked.Add(ref _bytes, count); _inner.Write(buffer, offset, count); }
		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Interlocked.Add(ref _bytes, buffer.Length); return _inner.WriteAsync(buffer, ct); }
		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Interlocked.Add(ref _bytes, count); return _inner.WriteAsync(buffer, offset, count, ct); }
	}
}
