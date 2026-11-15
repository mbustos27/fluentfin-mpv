using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: what sign-in and the home page ask a server (a fake one on localhost, a fake token): the sign-in requests
/// and every home row go out together, not one round trip after another; the user's views are read once and shared, and
/// read again when they may have changed.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class HomeRequestTests : IAsyncLifetime
{
	private const string FakeToken = "fake-token-home-0123456789";
	private static readonly Guid UserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
	private static readonly Guid Movies = Guid.Parse("10000000-0000-0000-0000-000000000001");
	private static readonly Guid Music = Guid.Parse("10000000-0000-0000-0000-000000000002");
	private static readonly Guid Shows = Guid.Parse("10000000-0000-0000-0000-000000000003");
	private static readonly Guid Kids = Guid.Parse("10000000-0000-0000-0000-000000000004");

	private readonly ConcurrentFakeJellyfin _server = new();
	private readonly ManualClock _clock = new();
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-home-test-" + Guid.NewGuid().ToString("N"));
	private JellyfinClient _client = null!;

	static HomeRequestTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("/UserViews", 200, $$"""
			{"Items":[
			  {"Id":"{{Movies:N}}","Name":"Movies","Type":"CollectionFolder","CollectionType":"movies"},
			  {"Id":"{{Music:N}}","Name":"Music","Type":"CollectionFolder","CollectionType":"music"},
			  {"Id":"{{Shows:N}}","Name":"Shows","Type":"CollectionFolder","CollectionType":"tvshows"},
			  {"Id":"{{Kids:N}}","Name":"Kids","Type":"CollectionFolder","CollectionType":"movies"}
			],"TotalRecordCount":4}
			""");
		_server.Respond("/UserItems/Resume", 200, Query("Half watched"));
		_server.Respond("/Shows/NextUp", 200, Query("Next episode"));
		_server.RespondLatest(Movies, Array("Movie 1", "Movie 2"));
		_server.RespondLatest(Shows, Array("Show 1"));
		_server.RespondLatest(Kids, "[]");
		_server.Respond("/Library/VirtualFolders", 204, "");
		_server.Respond("/Library/VirtualFolders/Name", 204, "");
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
		return Task.CompletedTask;
	}

	private HomeViewModel NewHome(HomeCache cache) =>
		new(_client, Mock.Of<IObservable<IOutboundWebSocketMessage>>(), new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>()), cache);

	private async Task SignIn()
	{
		_client ??= new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>()) { Time = _clock };
		await _client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = FakeToken,
			ServerId = "fake-server",
			User = new UserDto { Id = UserId, Name = "alice" },
		});
	}

	private int ViewReads => _server.ApiRequests.Count(x => x.Path.Equals("/UserViews", StringComparison.OrdinalIgnoreCase));

	[Fact]
	public async Task Home_AsksForEveryRowAtOnce_WithTheViewsSignInRead()
	{
		await SignIn();
		Assert.Equal(1, ViewReads);
		var gate = _server.Hold(5, x => x.Path is "/UserItems/Resume" or "/Shows/NextUp" or "/Items/Latest", TimeSpan.FromSeconds(2));

		using var ui = new UiThread();
		using var cache = new HomeCache(_folder, new System.Reactive.Subjects.Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), _clock);
		var home = await ui.Invoke(() => NewHome(cache));
		await ui.Run(() => home.OnNavigatedTo(null!));

		// Continue Watching, Next Up and a Latest per library but Music, all in flight together
		Assert.Equal(5, gate.MaxTogether);
		Assert.Equal([Movies, Shows, Kids], _server.ApiRequests.Where(x => x.Path == "/Items/Latest").Select(x => Guid.Parse(x.Query["parentId"]!)).Order());
		Assert.Equal(1, ViewReads);
		// in the server's order; Kids has nothing recent
		Assert.Equal(["Movies", "Shows"], await ui.Invoke(() => home.RecentItems.Select(x => x.Library.Name!).ToList()));
		Assert.Equal(["Movie 1", "Movie 2"], await ui.Invoke(() => home.RecentItems[0].Items.Select(x => x.Dto.Name!).ToList()));
		Assert.False(await ui.Invoke(() => home.IsLoading));

		// Back to Home a moment later: the same page, and the server is asked nothing
		var asked = _server.ApiRequests.Count;
		var home2 = await ui.Invoke(() => NewHome(cache));
		await ui.Run(() => home2.OnNavigatedTo(null!));
		Assert.Equal(["Movies", "Shows"], await ui.Invoke(() => home2.RecentItems.Select(x => x.Library.Name!).ToList()));
		Assert.Equal(asked, _server.ApiRequests.Count);
	}

	[Fact]
	public async Task Views_AreReadOnce_AndAgainOnlyAfterTheirLifetime()
	{
		await SignIn();

		Assert.Equal(["Movies", "Music", "Shows", "Kids"], (await _client.GetUserViews()).Select(x => x.Name));
		await _client.GetUserViews();
		Assert.Equal(["Movies", "Music", "Shows", "Kids"], await _client.GetUserLibraries().Select(x => x.Name!).ToListAsync());
		_clock.Advance(JellyfinClient.UserViewsLifetime - TimeSpan.FromSeconds(1));
		await _client.GetUserViews();
		Assert.Equal(1, ViewReads);

		_clock.Advance(TimeSpan.FromSeconds(2));
		await _client.GetUserViews();
		await _client.GetUserViews();
		Assert.Equal(2, ViewReads);
	}

	[Fact]
	public async Task Views_AreReadAgain_AfterANewSignIn()
	{
		await SignIn();
		await SignIn();
		await _client.GetUserViews();

		Assert.Equal(2, ViewReads);
	}

	[Theory]
	[InlineData("create")]
	[InlineData("delete")]
	[InlineData("rename")]
	public async Task Views_AreReadAgain_AfterALibraryChangeMadeHere(string change)
	{
		await SignIn();

		await (change switch
		{
			"create" => _client.CreateLibrary("New", Jellyfin.Sdk.Generated.Library.VirtualFolders.CollectionTypeOptions.Movies, new LibraryOptions()),
			"delete" => _client.DeleteLibrary("Kids"),
			_ => _client.RenameLibrary("Kids", "Children"),
		});
		await _client.GetUserViews();
		await _client.GetUserViews();

		Assert.Equal(2, ViewReads);
	}

	[Fact]
	public async Task Views_AreReadAgain_WhenTheServerSaysThisUserChanged_NotAnotherUser()
	{
		await SignIn();

		_client.OnUserUpdated(new UserUpdatedMessage { Data = new UserDto { Id = Guid.NewGuid() } });
		await _client.GetUserViews();
		Assert.Equal(1, ViewReads);

		_client.OnUserUpdated(new UserUpdatedMessage { Data = new UserDto { Id = UserId } });
		await _client.GetUserViews();
		Assert.Equal(2, ViewReads);
	}

	[Fact]
	public async Task AFailedViewsRead_IsNotKept()
	{
		_server.Respond("/UserViews", 500, "");
		await SignIn();
		Assert.Empty(await _client.GetUserViews());
		Assert.Equal(2, ViewReads);

		_server.Respond("/UserViews", 200, $$"""{"Items":[{"Id":"{{Movies:N}}","Name":"Movies","Type":"CollectionFolder","CollectionType":"movies"}],"TotalRecordCount":1}""");
		Assert.Equal(["Movies"], (await _client.GetUserViews()).Select(x => x.Name));
		await _client.GetUserViews();
		Assert.Equal(3, ViewReads);
	}

	[Fact]
	public async Task CallersAtTheSameTime_ShareOneViewsRead()
	{
		await SignIn();
		_clock.Advance(JellyfinClient.UserViewsLifetime + TimeSpan.FromSeconds(1));
		var gate = _server.Hold(2, x => x.Path == "/UserViews", TimeSpan.FromMilliseconds(500));

		var both = await Task.WhenAll(_client.GetUserViews(), _client.GetUserViews());

		Assert.Equal(1, gate.MaxTogether);
		Assert.Equal(2, ViewReads);
		Assert.Same(both[0], both[1]);
	}

	private static string Query(params string[] names) => $$"""{"Items":{{Array(names)}},"TotalRecordCount":{{names.Length}}}""";

	private static string Array(params string[] names) =>
		"[" + string.Join(",", names.Select(n => $$"""{"Id":"{{Guid.NewGuid():N}}","Name":"{{n}}","Type":"Movie"}""")) + "]";
}

/// <summary>A clock the test moves by hand (JellyfinClient.Time, HomeCache).</summary>
internal sealed class ManualClock : TimeProvider
{
	public static readonly DateTimeOffset Start = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
	private long _ticks;
	public override long TimestampFrequency => TimeSpan.TicksPerSecond;
	public override long GetTimestamp() => 1_000_000 + Interlocked.Read(ref _ticks);
	public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
	public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>
/// One thread that runs every continuation posted to it, one at a time, as the app's UI thread does: a view model that
/// starts several requests and updates its collections as they answer runs here as it does in the app.
/// </summary>
internal sealed class UiThread : SynchronizationContext, IDisposable
{
	private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

	public UiThread() => new Thread(() =>
	{
		SetSynchronizationContext(this);
		foreach (var (callback, state) in _queue.GetConsumingEnumerable())
		{
			callback(state);
		}
	}) { IsBackground = true, Name = "test-ui" }.Start();

	public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

	public override SynchronizationContext CreateCopy() => this;

	public Task Run(Func<Task> action)
	{
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(async _ =>
		{
			try { await action(); done.SetResult(); }
			catch (Exception ex) { done.SetException(ex); }
		}, null);
		return done.Task;
	}

	public Task<T> Invoke<T>(Func<T> func)
	{
		var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(_ =>
		{
			try { done.SetResult(func()); }
			catch (Exception ex) { done.SetException(ex); }
		}, null);
		return done.Task;
	}

	public void Dispose() => _queue.CompleteAdding();
}

/// <summary>
/// A Jellyfin server that answers requests at the same time (FakeJellyfin answers one after another), records them, and
/// can hold a kind of request until a number of them are in flight together (or a timeout), to tell requests sent
/// together from requests sent one after another. Everything not set up is 404.
/// </summary>
internal sealed class ConcurrentFakeJellyfin : IDisposable
{
	private readonly HttpListener _listener = new();
	private readonly ConcurrentDictionary<string, (int Status, string Body)> _responses = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<Guid, string> _latest = new();
	private readonly ConcurrentDictionary<string, Func<FakeRequest, (int Status, string Body)>> _computed = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentQueue<FakeRequest> _requests = new();
	private volatile HoldGate? _gate;
	private Task? _loop;

	public string BaseUrl { get; private set; } = "";

	/// <summary>Without the session websocket's connection attempts.</summary>
	public IReadOnlyList<FakeRequest> ApiRequests => [.. _requests.Where(x => !x.Path.Equals("/socket", StringComparison.OrdinalIgnoreCase))];

	public void Respond(string path, int status, string body) => _responses[path] = (status, body);

	public void RespondWith(string path, Func<FakeRequest, (int Status, string Body)> answer) => _computed[path] = answer;

	public void RespondLatest(Guid library, string body) => _latest[library] = body;

	public HoldGate Hold(int together, Func<FakeRequest, bool> which, TimeSpan timeout) => _gate = new HoldGate(together, which, timeout);

	public void Start()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		BaseUrl = $"http://localhost:{port}";
		_listener.Prefixes.Add(BaseUrl + "/");
		_listener.Start();
		Respond("/Sessions/Capabilities/Full", 204, "");
		Respond("/Plugins", 200, "[]");
		_loop = Task.Run(Accept);
	}

	private async Task Accept()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception)
			{
				return;
			}
			_ = Task.Run(() => Serve(context));
		}
	}

	private async Task Serve(HttpListenerContext context)
	{
		var url = context.Request.Url!;
		var request = new FakeRequest(context.Request.HttpMethod, url.AbsolutePath, url.ToString(), System.Web.HttpUtility.ParseQueryString(url.Query));
		_requests.Enqueue(request);
		if (_gate is { } gate && gate.Which(request))
		{
			await gate.Pass();
		}

		var (status, body) = request.Path.Equals("/Items/Latest", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(request.Query["parentId"], out var library)
			? (_latest.TryGetValue(library, out var latest) ? (200, latest) : (200, "[]"))
			: _computed.TryGetValue(request.Path, out var answer) ? answer(request)
			: _responses.TryGetValue(request.Path, out var response) ? response : (404, "");
		try
		{
			context.Response.StatusCode = status;
			if (body.Length > 0)
			{
				var bytes = Encoding.UTF8.GetBytes(body);
				context.Response.ContentType = "application/json";
				context.Response.ContentLength64 = bytes.Length;
				await context.Response.OutputStream.WriteAsync(bytes);
			}
			context.Response.Close();
		}
		catch (Exception)
		{
			// the client went away
		}
	}

	public void Dispose()
	{
		try
		{
			_listener.Stop();
			_listener.Close();
		}
		catch (ObjectDisposedException)
		{
		}

		_loop?.Wait(TimeSpan.FromSeconds(2));
	}

	internal sealed class HoldGate(int together, Func<FakeRequest, bool> which, TimeSpan timeout)
	{
		private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _waiting;
		private int _max;

		public Func<FakeRequest, bool> Which { get; } = which;

		/// <summary>The most requests of this kind the server had in flight at once.</summary>
		public int MaxTogether => Volatile.Read(ref _max);

		public async Task Pass()
		{
			var now = Interlocked.Increment(ref _waiting);
			int seen;
			while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
			{
			}
			if (now >= together)
			{
				_all.TrySetResult();
			}
			await Task.WhenAny(_all.Task, Task.Delay(timeout));
			Interlocked.Decrement(ref _waiting);
		}
	}
}
