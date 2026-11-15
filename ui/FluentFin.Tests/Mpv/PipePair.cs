using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// Real OS pipes in both directions, like the backend's stdin/stdout/stderr: the UI side gets a reader and a writer,
/// the test plays the backend on the other ends.
/// </summary>
internal sealed class PipePair : IDisposable
{
	private static readonly UTF8Encoding s_utf8 = new(false);

	private readonly AnonymousPipeServerStream _toBackendServer = new(PipeDirection.Out);
	private readonly AnonymousPipeClientStream _toBackendClient;
	private readonly AnonymousPipeServerStream _fromBackendServer = new(PipeDirection.Out);
	private readonly AnonymousPipeClientStream _fromBackendClient;
	private readonly AnonymousPipeServerStream _stderrServer = new(PipeDirection.Out);
	private readonly AnonymousPipeClientStream _stderrClient;

	public PipePair()
	{
		_toBackendClient = new AnonymousPipeClientStream(PipeDirection.In, _toBackendServer.ClientSafePipeHandle);
		_fromBackendClient = new AnonymousPipeClientStream(PipeDirection.In, _fromBackendServer.ClientSafePipeHandle);
		_stderrClient = new AnonymousPipeClientStream(PipeDirection.In, _stderrServer.ClientSafePipeHandle);

		UiWriter = new StreamWriter(_toBackendServer, s_utf8);
		UiReader = new StreamReader(_fromBackendClient, s_utf8);
		UiStderr = new StreamReader(_stderrClient, s_utf8);
		BackendReader = new StreamReader(_toBackendClient, s_utf8);
		BackendWriter = new StreamWriter(_fromBackendServer, s_utf8) { AutoFlush = true, NewLine = "\n" };
		BackendStderr = new StreamWriter(_stderrServer, s_utf8) { AutoFlush = true, NewLine = "\n" };
	}

	/// <summary>The backend's stdin, as the UI sees it.</summary>
	public StreamWriter UiWriter { get; }

	/// <summary>The backend's stdout, as the UI sees it.</summary>
	public StreamReader UiReader { get; }

	public StreamReader UiStderr { get; }

	public StreamReader BackendReader { get; }
	public StreamWriter BackendWriter { get; }
	public StreamWriter BackendStderr { get; }

	/// <summary>
	/// One line at a time: the fake's hello (sent unawaited), its responses and a test's events can come from different
	/// threads, and a StreamWriter throws when an async write starts before the previous one has finished.
	/// </summary>
	public async Task SendFromBackend(string line)
	{
		await _sendLock.WaitAsync();
		try
		{
			await BackendWriter.WriteLineAsync(line);
		}
		finally
		{
			_sendLock.Release();
		}
	}

	private readonly SemaphoreSlim _sendLock = new(1, 1);

	public async Task<string> ReadRawRequest(TimeSpan? timeout = null) =>
		await BackendReader.ReadLineAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(5)) ?? throw new EndOfStreamException();

	public async Task<JsonObject> ReadRequest(TimeSpan? timeout = null) => JsonNode.Parse(await ReadRawRequest(timeout))!.AsObject();

	// Only the pipe streams are ever closed here, never the StreamReader/StreamWriter over them: those are not thread-safe,
	// and one disposed while another thread writes or reads through it corrupts its buffer counters, so that write throws
	// ArgumentOutOfRangeException or spins forever. A process that exits only closes its handles, and so does
	// WindowsMpvBackendLauncher.BackendProcess.Dispose (the raw streams, never the writer the BridgeClient uses).

	/// <summary>The backend closes its output, as when it exits.</summary>
	public void CloseBackendOutput()
	{
		Close(_fromBackendServer);
		Close(_stderrServer);
	}

	/// <summary>The backend's end of its stdin closes, as when it exits: the UI's writes fail from then on.</summary>
	public void CloseBackendInput() => Close(_toBackendClient);

	public void Dispose()
	{
		// the backend's ends first: a UI write still blocked on a full pipe fails at once
		foreach (var stream in new Stream[] { _toBackendClient, _fromBackendServer, _stderrServer, _toBackendServer, _fromBackendClient, _stderrClient })
		{
			Close(stream);
		}
	}

	private static void Close(Stream stream)
	{
		try
		{
			stream.Dispose();
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
		{
		}
	}
}
