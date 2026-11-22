namespace FluentFin.Core.Shortcuts;

/// <summary>
/// 1.6.1 (fix queue, found at the 1.5 integration): "/" puts the keyboard in the search box (<see cref="AppShortcut.FocusSearch"/>)
/// on its key-down, and the same press's character ("/", made from the key-down as a real keyboard's is) now and then reached the
/// box after the focus did: the search read "/Signal". The press that opens the box arms this; the box's next text change, when
/// it is that "/" typed over the box's selection (or at its caret), is cancelled. Any other key, the change itself, or three seconds
/// gone by disarms it, so a "/" the user types into the box afterwards ("AC/DC") is never taken.
/// </summary>
public sealed class SlashSwallow(TimeProvider? clock = null)
{
	/// <summary>How long after the press its character may still come.</summary>
	public static TimeSpan Window { get; } = TimeSpan.FromSeconds(3);

	private readonly TimeProvider _clock = clock ?? TimeProvider.System;
	private long? _armedAt;

	/// <summary>The "/" press that opened the box.</summary>
	public void Arm() => _armedAt = _clock.GetTimestamp();

	/// <summary>Another key, or the box didn't open: the next "/" is the user's.</summary>
	public void Disarm() => _armedAt = null;

	/// <summary>Whether the press's character may still come.</summary>
	public bool IsArmed => _armedAt is { } at && _clock.GetElapsedTime(at) <= Window;

	/// <summary>
	/// Whether the box's text changing from <paramref name="oldText"/> (with <paramref name="selectionLength"/> characters
	/// selected from <paramref name="selectionStart"/>) to <paramref name="newText"/> is the opening press's "/": then it is
	/// to be cancelled, and this disarms. Any other change disarms too.
	/// </summary>
	public bool Swallows(string? oldText, int selectionStart, int selectionLength, string? newText)
	{
		if (!IsArmed)
		{
			_armedAt = null;
			return false;
		}

		_armedAt = null;
		oldText ??= "";
		newText ??= "";
		var start = Math.Clamp(selectionStart, 0, oldText.Length);
		var length = Math.Clamp(selectionLength, 0, oldText.Length - start);
		return newText == string.Concat(oldText.AsSpan(0, start), "/", oldText.AsSpan(start + length));
	}
}
