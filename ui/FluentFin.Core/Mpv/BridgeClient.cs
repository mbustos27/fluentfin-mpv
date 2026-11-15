using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Core.Mpv;

/// <summary>
/// The UI side of the bridge protocol (docs/BRIDGE.md §1–2): requests are JSON lines written to <paramref name="output"/>
/// (the backend's stdin), responses and events are JSON lines read from <paramref name="input"/> (the backend's stdout).
/// Responses are matched to requests by id and returned as tasks, events are published on <see cref="Events"/> (on the
/// reader thread). When the input ends every pending request fails with <see cref="BridgeClosedException"/>.
/// </summary>
public sealed class BridgeClient(TextReader input, TextWriter output, ILogger? logger = null) : IDisposable
{
	private const int MaxLoggedLineLength = 500;

	private readonly ILogger _logger = logger ?? NullLogger.Instance;
	private readonly ConcurrentDictionary<long, Pending> _pending = new();
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly Subject<BridgeEvent> _events = new();
	private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private long _lastId;
	private Task? _readLoop;
	private Exception? _closeReason;

	private sealed record Pending(string Command, TaskCompletionSource<JsonNode?> Response);

	public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>Events in the order they arrive; completes when the connection closes.</summary>
	public IObservable<BridgeEvent> Events => _events.AsObservable();

	/// <summary>Completes when the input has ended or failed.</summary>
	public Task Closed => _closed.Task;

	public bool IsClosed => Volatile.Read(ref _closeReason) is not null;

	/// <summary>
	/// Why the connection closed, null while open: the end of the backend's output (<see cref="EndOfStreamException"/>),
	/// a read error, <see cref="ObjectDisposedException"/> after Dispose, or a <see cref="TimeoutException"/> when the
	/// backend stopped reading its input (a request could not be written in time).
	/// </summary>
	public Exception? CloseReason => Volatile.Read(ref _closeReason);

	public void Start() => _readLoop ??= Task.Run(ReadLoop);

	/// <summary>
	/// Sends one request and waits for its response, all within the timeout (writing included: a backend that stopped
	/// reading its input fills the pipe, and that write would never finish). Returns the <c>result</c> of an <c>ok</c>
	/// response; an error response throws <see cref="BridgeCommandException"/>, no response in time
	/// <see cref="BridgeTimeoutException"/>, a closed connection <see cref="BridgeClosedException"/>. A write that does not
	/// finish in time closes the connection (<see cref="CloseReason"/> is a <see cref="TimeoutException"/>).
	/// </summary>
	public async Task<JsonNode?> SendAsync(string command, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(command);

		if (Volatile.Read(ref _closeReason) is { } reason)
		{
			throw new BridgeClosedException($"The backend connection is closed, '{command}' was not sent", reason);
		}

		var limit = timeout ?? DefaultTimeout;
		var started = Environment.TickCount64;
		TimeSpan Left() => limit == Timeout.InfiniteTimeSpan
			? Timeout.InfiniteTimeSpan
			: TimeSpan.FromMilliseconds(Math.Max(0, limit.TotalMilliseconds - (Environment.TickCount64 - started)));

		var id = Interlocked.Increment(ref _lastId);
		var pending = new Pending(command, new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously));
		_pending[id] = pending;

		var message = new JsonObject
		{
			["id"] = id,
			["cmd"] = command,
		};
		if (args is not null)
		{
			message["args"] = args.Parent is null ? args : args.DeepClone();
		}

		try
		{
			// one line at a time; the lock is held until the line is really written (see below), so waiting for it is bounded too.
			// Every await that holds (or takes) the lock goes on without the caller's SynchronizationContext: the caller may be
			// the UI thread, and a busy or frozen UI thread must not keep the lock taken (other requests would then time out
			// as if the backend had stopped reading its input, and the backend would be killed)
			if (!await _writeLock.WaitAsync(Left(), cancellationToken).ConfigureAwait(false))
			{
				throw new BridgeTimeoutException(command, limit);
			}

			if (Volatile.Read(ref _closeReason) is { } closedWhileWaiting)
			{
				_writeLock.Release();
				throw new BridgeClosedException($"The backend connection is closed, '{command}' was not sent", closedWhileWaiting);
			}

			Task write;
			try
			{
				write = WriteLineAsync(message.ToJsonString());
			}
			catch
			{
				_writeLock.Release();
				throw;
			}

			// released when the write finishes, not when this caller stops waiting: a half-written line followed by the
			// next request would break the framing
			_ = write.ContinueWith(_ => _writeLock.Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

			try
			{
				await write.WaitAsync(Left(), cancellationToken);
			}
			catch (TimeoutException)
			{
				var stalled = new TimeoutException($"The backend did not read its input for {limit.TotalSeconds:0.#} s");
				Close(stalled);
				throw new BridgeClosedException($"The backend stopped reading its input, '{command}' was not sent", stalled);
			}
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException || (IsClosed && ex is not BridgeException and not OperationCanceledException))
		{
			// a write torn down under it (its pipe, or the writer over it, closed by a release or by a close that timed out
			// in the background) can fail with anything; once the connection is closed, that is all it means
			_pending.TryRemove(id, out _);
			Close(ex);
			throw new BridgeClosedException($"Could not send '{command}' to the backend", ex);
		}
		catch
		{
			_pending.TryRemove(id, out _);
			throw;
		}

		// the read loop may have ended between the check above and the registration
		if (Volatile.Read(ref _closeReason) is { } closedWhileSending && _pending.TryRemove(id, out _))
		{
			throw new BridgeClosedException($"The backend connection closed before it answered '{command}'", closedWhileSending);
		}

		try
		{
			return await pending.Response.Task.WaitAsync(Left(), cancellationToken);
		}
		catch (TimeoutException)
		{
			_pending.TryRemove(id, out _);
			throw new BridgeTimeoutException(command, limit);
		}
		catch (OperationCanceledException)
		{
			_pending.TryRemove(id, out _);
			throw;
		}
	}

	/// <summary>
	/// Closes the backend's input (stdin EOF is a shutdown request) once no line is being written: closing a writer in the
	/// middle of a write leaves that write hanging for good. False when a write the backend never takes holds it past
	/// <paramref name="timeout"/>; the input then stays open (killing the backend ends that write). Never throws.
	/// </summary>
	public async Task<bool> CloseOutputAsync(TimeSpan timeout)
	{
		if (!await _writeLock.WaitAsync(timeout).ConfigureAwait(false))
		{
			return false;
		}

		try
		{
			// the buffer is empty after every request, so this does not write; off the caller's thread all the same
			await Task.Run(output.Close).WaitAsync(timeout).ConfigureAwait(false);
			return true;
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
		{
			return false;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>The write lock is held until this completes, so it never waits for the caller's SynchronizationContext.</summary>
	private async Task WriteLineAsync(string line)
	{
		await output.WriteAsync(line + "\n").ConfigureAwait(false);
		await output.FlushAsync().ConfigureAwait(false);
	}

	private async Task ReadLoop()
	{
		Exception reason;
		try
		{
			while (true)
			{
				var line = await input.ReadLineAsync();
				if (line is null)
				{
					reason = new EndOfStreamException("The backend closed its output");
					break;
				}

				HandleLine(line);
			}
		}
		catch (Exception ex)
		{
			reason = ex;
		}

		Close(reason);
	}

	internal void HandleLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}

		JsonObject? message;
		try
		{
			message = JsonNode.Parse(line) as JsonObject;
		}
		catch (JsonException)
		{
			message = null;
		}

		if (message is null)
		{
			_logger.LogWarning("Ignoring a line from the mpv backend that is not a JSON object: {Line}", ForLog(line));
			return;
		}

		if (message.TryGetPropertyValue("id", out var idNode) && idNode is not null)
		{
			HandleResponse(message, idNode, line);
			return;
		}

		if (message["event"] is JsonValue eventName && eventName.GetValueKind() == JsonValueKind.String)
		{
			Publish(new BridgeEvent(eventName.GetValue<string>(), message["data"]?.DeepClone()));
			return;
		}

		_logger.LogWarning("Ignoring a line from the mpv backend that is neither a response nor an event: {Line}", ForLog(line));
	}

	private void HandleResponse(JsonObject message, JsonNode idNode, string line)
	{
		if (idNode.GetValueKind() != JsonValueKind.Number || !idNode.AsValue().TryGetValue<long>(out var id))
		{
			_logger.LogWarning("Ignoring a response from the mpv backend with an invalid id: {Line}", ForLog(line));
			return;
		}

		if (!_pending.TryRemove(id, out var pending))
		{
			// timed out earlier, or not ours
			_logger.LogWarning("Ignoring a response from the mpv backend to unknown request {Id}", id);
			return;
		}

		if (message["ok"] is JsonValue ok && ok.GetValueKind() == JsonValueKind.True)
		{
			pending.Response.TrySetResult(message["result"]?.DeepClone());
			return;
		}

		var error = message["error"] as JsonObject;
		var code = error?["code"] is JsonValue c && c.GetValueKind() == JsonValueKind.String ? c.GetValue<string>() : "internal";
		var detail = error?["message"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : null;
		pending.Response.TrySetException(new BridgeCommandException(pending.Command, code, BridgeRedaction.Redact(detail)));
	}

	private void Publish(BridgeEvent bridgeEvent)
	{
		try
		{
			_events.OnNext(bridgeEvent);
		}
		catch (Exception ex)
		{
			// a failing subscriber must not stop the reader
			_logger.LogError(ex, "A handler of the mpv backend event {Event} failed", bridgeEvent.Name);
		}
	}

	private void Close(Exception reason)
	{
		if (Interlocked.CompareExchange(ref _closeReason, reason, null) is not null)
		{
			return;
		}

		foreach (var id in _pending.Keys)
		{
			if (_pending.TryRemove(id, out var pending))
			{
				pending.Response.TrySetException(new BridgeClosedException($"The backend connection closed before it answered '{pending.Command}'", reason));
			}
		}

		try
		{
			_events.OnCompleted();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "A handler of the end of the mpv backend events failed");
		}

		_closed.TrySetResult();
	}

	private static string ForLog(string line)
	{
		var redacted = BridgeRedaction.Redact(line);
		return redacted.Length <= MaxLoggedLineLength ? redacted : redacted[..MaxLoggedLineLength] + "…";
	}

	/// <summary>
	/// Closes the connection: pending requests fail, events complete. The subject and the write lock are not disposed:
	/// the reader thread may be completing them at the same moment, and neither holds anything that needs releasing.
	/// </summary>
	public void Dispose() => Close(new ObjectDisposedException(nameof(BridgeClient)));
}
