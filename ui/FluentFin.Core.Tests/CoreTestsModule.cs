using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentFin.Core.SmartCollections;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.3: the test assembly's start. Made by Nixie's log (<see cref="SmartCollectionCatalog.Log"/>) is set for every test,
/// as the app sets it at start: without one, a builder that threw in a test was silent (its kind just missing), as 1.7.2's
/// genre mixes were on the owner's index.
/// </summary>
internal static class CoreTestsModule
{
	[ModuleInitializer]
	internal static void Start() => SmartCollectionCatalog.Log = MadeByNixieLog.Instance;
}

/// <summary>
/// What Made by Nixie said in this test run: a builder's throw (once a kind, as in the app) and the census lines. A throw is
/// also written to the run's error output, so it is seen whichever test caused it.
/// </summary>
internal sealed class MadeByNixieLog : ILogger
{
	public static MadeByNixieLog Instance { get; } = new();

	private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

	private MadeByNixieLog()
	{
	}

	public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

	/// <summary>The builders' throws said so far in this run.</summary>
	public IReadOnlyList<string> Throws => [.. _entries.Where(x => x.Level >= LogLevel.Warning).Select(x => x.Message)];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		var message = formatter(state, exception);
		_entries.Enqueue((logLevel, message));
		if (logLevel >= LogLevel.Warning)
		{
			Console.Error.WriteLine($"[Made by Nixie, in a test] {message}");
		}
	}
}
