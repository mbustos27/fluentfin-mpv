// A TCP relay that puts a network between the client and the local fake server: every byte is delivered half a round
// trip later in each direction, and a new connection first pays its handshake (TCP + TLS 1.3 = 2 round trips by default)
// before any request byte reaches the server. So a request on a warm connection costs 1 RTT, one on a new connection 3 RTT,
// as against a remote HTTPS server. `Http2Like` models HTTP/2: requests share one connection, so only the first connection
// pays the handshake and connections opened while it is still in its handshake wait for it (streams of the same connection).
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;

sealed class LatencyProxy : IAsyncDisposable
{
	private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
	private readonly int _targetPort;
	private readonly CancellationTokenSource _cts = new();
	private readonly object _linkLock = new();
	private Task? _firstHandshake;
	private long _linkFreeAtTicks;
	private int _connections;

	public double RttMs { get; }
	public int HandshakeRtts { get; }
	public bool Http2Like { get; }
	/// <summary>Downstream bandwidth shared by all connections, megabits per second; 0 = unlimited.</summary>
	public double DownMbps { get; }
	public int Connections => Volatile.Read(ref _connections);
	public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

	public LatencyProxy(int targetPort, double rttMs, int handshakeRtts = 2, bool http2Like = false, double downMbps = 0)
	{
		_targetPort = targetPort;
		RttMs = rttMs;
		HandshakeRtts = handshakeRtts;
		Http2Like = http2Like;
		DownMbps = downMbps;
		_listener.Start();
		_ = AcceptLoop();
	}

	public void ResetCounters() => Interlocked.Exchange(ref _connections, 0);

	/// <summary>
	/// A new client (sign-in makes a new HttpClient, so a new connection pool): with Http2Like its first connection pays
	/// the handshake again.
	/// </summary>
	public void NewClient()
	{
		lock (_linkLock)
		{
			_firstHandshake = null;
		}
	}

	private async Task AcceptLoop()
	{
		while (!_cts.IsCancellationRequested)
		{
			TcpClient client;
			try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
			catch { return; }
			Interlocked.Increment(ref _connections);
			_ = Relay(client);
		}
	}

	private async Task Relay(TcpClient client)
	{
		using var _ = client;
		using var server = new TcpClient();
		client.NoDelay = true;
		server.NoDelay = true;
		try
		{
			await server.ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token);
			await Handshake();
			var a = Pump(client.GetStream(), server.GetStream(), downstream: false);
			var b = Pump(server.GetStream(), client.GetStream(), downstream: true);
			await Task.WhenAny(a, b);
		}
		catch
		{
			// a side went away
		}
	}

	private Task Handshake()
	{
		var cost = TimeSpan.FromMilliseconds(RttMs * HandshakeRtts);
		if (!Http2Like)
		{
			return Task.Delay(cost, _cts.Token);
		}

		lock (_linkLock)
		{
			// HTTP/2: one connection; later streams wait for its handshake if it is not done, otherwise they are free
			_firstHandshake ??= Task.Delay(cost, _cts.Token);
			return _firstHandshake;
		}
	}

	private async Task Pump(NetworkStream from, NetworkStream to, bool downstream)
	{
		var channel = Channel.CreateUnbounded<(long DueTicks, byte[] Data)>();
		var oneWay = (long)(RttMs / 2 * Stopwatch.Frequency / 1000);
		var writer = Task.Run(async () =>
		{
			await foreach (var (due, data) in channel.Reader.ReadAllAsync(_cts.Token))
			{
				var at = due;
				if (downstream && DownMbps > 0)
				{
					var serialize = (long)(data.Length * 8 / (DownMbps * 1_000_000) * Stopwatch.Frequency);
					lock (_linkLock)
					{
						at = Math.Max(due, _linkFreeAtTicks) + serialize;
						_linkFreeAtTicks = at;
					}
				}
				var wait = (at - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
				if (wait > 0.5)
				{
					await Task.Delay(TimeSpan.FromMilliseconds(wait), _cts.Token);
				}
				await to.WriteAsync(data, _cts.Token);
			}
		});

		var buffer = new byte[64 * 1024];
		try
		{
			while (true)
			{
				var n = await from.ReadAsync(buffer, _cts.Token);
				if (n == 0)
				{
					break;
				}
				channel.Writer.TryWrite((Stopwatch.GetTimestamp() + oneWay, buffer[..n]));
			}
		}
		catch
		{
			// closed
		}
		channel.Writer.TryComplete();
		try { await writer; } catch { }
		try { to.Socket.Shutdown(SocketShutdown.Send); } catch { }
	}

	public async ValueTask DisposeAsync()
	{
		_cts.Cancel();
		_listener.Stop();
		await Task.CompletedTask;
	}

	/// <summary>Task.Delay has the system timer's resolution (15.6 ms by default on Windows); ask for 1 ms in this process.</summary>
	public static void UseFineTimer() => _ = timeBeginPeriod(1);

	[DllImport("winmm.dll")]
	private static extern uint timeBeginPeriod(uint period);
}
