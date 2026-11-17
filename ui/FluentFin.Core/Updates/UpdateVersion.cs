namespace FluentFin.Core.Updates;

/// <summary>
/// Seaglass: a release version, "1.2.3", "v1.2.3", "1.2.3-rc.1" or "1.2.3+build" (what GitHub tags and AppVersion hold).
/// Ordered like semantic versions: the numbers, then a pre-release ("-rc.1") before the same release without one.
/// </summary>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch, string Pre) : IComparable<UpdateVersion>
{
	public static bool TryParse(string? text, out UpdateVersion version)
	{
		version = default;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var s = text.Trim();
		if (s.StartsWith('v') || s.StartsWith('V'))
		{
			s = s[1..];
		}

		var plus = s.IndexOf('+');
		if (plus >= 0)
		{
			s = s[..plus];
		}

		var pre = "";
		var dash = s.IndexOf('-');
		if (dash >= 0)
		{
			pre = s[(dash + 1)..];
			s = s[..dash];
		}

		var parts = s.Split('.');
		if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)) || (dash >= 0 && pre.Length == 0))
		{
			return false;
		}

		if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)
			|| !int.TryParse(parts.Length == 3 ? parts[2] : "0", out var patch))
		{
			return false;
		}

		version = new UpdateVersion(major, minor, patch, pre);
		return true;
	}

	/// <summary>Whether <paramref name="candidate"/> is a newer release than <paramref name="current"/>; false when either is not a version.</summary>
	public static bool IsNewer(string? candidate, string? current) =>
		TryParse(candidate, out var c) && TryParse(current, out var n) && c.CompareTo(n) > 0;

	public int CompareTo(UpdateVersion other)
	{
		var byNumber = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
		if (byNumber != 0)
		{
			return byNumber;
		}

		// a release is newer than its own pre-releases
		if (Pre.Length == 0 || other.Pre.Length == 0)
		{
			return (Pre.Length == 0 ? 1 : 0) - (other.Pre.Length == 0 ? 1 : 0);
		}

		var a = Pre.Split('.');
		var b = other.Pre.Split('.');
		for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
		{
			var aNum = a[i].All(char.IsAsciiDigit) && a[i].Length > 0;
			var bNum = b[i].All(char.IsAsciiDigit) && b[i].Length > 0;
			var c = (aNum, bNum) switch
			{
				(true, true) => long.Parse(a[i]).CompareTo(long.Parse(b[i])),
				(true, false) => -1,
				(false, true) => 1,
				_ => string.CompareOrdinal(a[i], b[i]),
			};
			if (c != 0)
			{
				return c;
			}
		}

		return a.Length.CompareTo(b.Length);
	}

	public override string ToString() => Pre.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";
}

/// <summary>Seaglass: the once-a-day rule of the update check.</summary>
public static class UpdateGate
{
	public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

	/// <summary>
	/// Whether a check may go out now: never checked, or the last one was a day ago. A last check in the future (the clock was
	/// set back) is not a reason to wait: the check that follows stores the real time.
	/// </summary>
	public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
		lastCheck is not { } last || last > now || now - last >= Interval;

	/// <summary>An attempt may go out when none was made in the last hour (a clock set back does not block it).</summary>
	public static bool AttemptAllowed(DateTimeOffset? lastAttempt, DateTimeOffset now) =>
		lastAttempt is not { } last || last > now || now - last >= RetryInterval;

	public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);
}
