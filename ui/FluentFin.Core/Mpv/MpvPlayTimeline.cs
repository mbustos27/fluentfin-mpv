using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Mpv;

/// <summary>
/// fluentfin-mpv (startup-perf): the <c>[perf]</c> lines of one play with the mpv player, in milliseconds since Play was
/// pressed (<see cref="PerfLog.PlayPressed"/>), or since the player page opened when nothing was pressed (a remote play):
/// page opened, backend started or already running, connected, play sent, then from the backend's events the first
/// <c>opening</c>, the first <c>playing</c> (mpv shows the first frame then, give or take one 0.5 s sample of the backend's
/// monitor) and the first <c>position</c> past zero. Each step is logged once. Never throws.
/// </summary>
public sealed class MpvPlayTimeline
{
	/// <summary>A press longer ago than this did not open this page.</summary>
	public static readonly TimeSpan PressWindow = TimeSpan.FromSeconds(30);

	private readonly ILogger _logger;
	private readonly long _generation;
	private readonly long _origin;
	private readonly string _since;
	private long _sent;
	private string? _itemId;
	private int _opening;
	private int _playing;
	private int _position;

	private MpvPlayTimeline(ILogger logger, long generation, long origin, string since)
	{
		_logger = logger;
		_generation = generation;
		_origin = origin;
		_since = since;
	}

	/// <summary>A player page opened (generation <paramref name="generation"/>).</summary>
	public static MpvPlayTimeline Begin(ILogger logger, long generation)
	{
		var now = Stopwatch.GetTimestamp();
		var timeline = PerfLog.TakePlayPressed(PressWindow) is { } pressed
			? new MpvPlayTimeline(logger, generation, pressed, "after Play was pressed")
			: new MpvPlayTimeline(logger, generation, now, "after the player page opened");
		timeline.Step("player page opened");
		return timeline;
	}

	public long ElapsedMs => (long)Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;

	/// <summary>One step of the start, logged as it happens.</summary>
	public void Step(string step)
	{
		try
		{
			_logger.LogInformation("[perf] play (generation {Generation}): {Step} at {Ms} ms {Since}", _generation, step, ElapsedMs, _since);
		}
		catch (Exception)
		{
			// a timestamp never breaks a play
		}
	}

	/// <summary>
	/// <c>play</c> (or <c>play_file</c>, <paramref name="itemId"/> null) is about to be written: from now on the item's states
	/// are this play's (the first <c>opening</c> can come before the answer, docs/BRIDGE.md §3).
	/// </summary>
	public void Sending(string? itemId)
	{
		_itemId = itemId is null ? null : Normalize(itemId);
		Interlocked.Exchange(ref _sent, Stopwatch.GetTimestamp());
	}

	/// <summary>Every backend event (reader thread): the first opening, playing and position of this play are steps.</summary>
	public void OnEvent(BridgeEvent e)
	{
		try
		{
			var sent = Interlocked.Read(ref _sent);
			if (sent == 0)
			{
				return;
			}

			var data = e.Data as JsonObject;
			if (e.Name == Bridge.Events.State && data?["state"]?.GetValue<string>() is { } state)
			{
				var item = data["itemId"] is JsonValue value && value.TryGetValue(out string? id) && id is not null ? Normalize(id) : null;
				if (_itemId is not null && item != _itemId)
				{
					return;
				}

				if (state == Bridge.States.Opening && Interlocked.Exchange(ref _opening, 1) == 0)
				{
					Step("opening");
				}
				else if (state == Bridge.States.Playing && Interlocked.Exchange(ref _playing, 1) == 0)
				{
					Step($"playing ({(long)Stopwatch.GetElapsedTime(sent).TotalMilliseconds} ms after play was sent)");
				}
			}
			else if (e.Name == Bridge.Events.Position && Volatile.Read(ref _playing) == 1 &&
					 data?["positionTicks"] is JsonValue ticks && ticks.TryGetValue(out long position) && position > 0 &&
					 Interlocked.Exchange(ref _position, 1) == 0)
			{
				Step("first position");
			}
		}
		catch (Exception)
		{
			// an event this does not read is not this timeline's
		}
	}

	private static string Normalize(string id) => id.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
}
