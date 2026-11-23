using Microsoft.Extensions.Logging;
using Serilog;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.7.3, the fix round's re-review (privacy): the app logs to its own file only, through the redacting formatter (no
/// token in the log). The host's default builder also adds the console, debug, event source and Windows Event Log providers;
/// the Event Log one wrote every Warning and Error to the Application log, unmasked. They are cleared first.
/// </summary>
public static class NixieLogging
{
	/// <summary>The app's logging: nothing but Nixie's own (Serilog's file, <c>App</c> configures it).</summary>
	public static void OnlyNixiesLog(ILoggingBuilder builder)
	{
		builder.ClearProviders();
		builder.AddSerilog();
	}
}
