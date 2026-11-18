using FluentFin.Core.Mpv;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.2.0 (admin, review fix): the log file's lines with every access token masked (BridgeRedaction: ?api_key= /
/// ApiKey= / access_token= in a URL, Token="…" in an Authorization header, quoted "AccessToken" values). A failed request's
/// exception names its URL, and some URLs carry the token: none reaches the file.
/// </summary>
public sealed class RedactingLogFormatter : ITextFormatter
{
	/// <summary>Serilog's file sink default.</summary>
	public const string Template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

	private readonly MessageTemplateTextFormatter _inner = new(Template);

	public void Format(LogEvent logEvent, TextWriter output)
	{
		using var line = new StringWriter();
		_inner.Format(logEvent, line);
		output.Write(BridgeRedaction.Redact(line.ToString()));
	}
}
