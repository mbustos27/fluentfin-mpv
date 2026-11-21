using System.Text;
using System.Text.RegularExpressions;

namespace FluentFin.Core.Updates;

/// <summary>Nixie 1.5: a piece of a line in the release notes: plain text, bold, code, or a link (opened in the browser only on a click).</summary>
public sealed record NotesInline(string Text, bool Bold = false, bool Code = false, string? Link = null);

public enum NotesBlockKind
{
	Heading,
	Bullet,
	Paragraph,
}

/// <param name="Level">A heading's level (1 to 6), or a bullet's depth (0 for the first).</param>
public sealed record NotesBlock(NotesBlockKind Kind, int Level, IReadOnlyList<NotesInline> Inlines)
{
	public string PlainText => string.Concat(Inlines.Select(i => i.Text));
}

/// <summary>
/// Nixie 1.5: "What's new": the GitHub release's notes (its body, Markdown as CHANGELOG.md writes it) as simple blocks:
/// headings, bullets (nested by indent), paragraphs; **bold**, `code`, [links](https://…) and bare https links. Anything else is
/// kept as its text (no HTML, no images). A link is kept only when it is https (or http) with a host, so a click can open
/// nothing but a web page.
/// </summary>
public static partial class ReleaseNotes
{
	[GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")]
	private static partial Regex HeadingLine();

	[GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
	private static partial Regex BulletLine();

	[GeneratedRegex(@"^(\s*)\d+[.)]\s+(.*)$")]
	private static partial Regex NumberedLine();

	[GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__|`(?<code>[^`]+)`|\[(?<text>[^\]]+)\]\((?<url>(?:[^()\s]|\([^()\s]*\))+)(?:\s+""[^""]*"")?\)|(?<bare>https?://[^\s<>()]+[^\s<>().,;:!?'""])")]
	private static partial Regex Inline();

	[GeneratedRegex(@"<[^>]+>")]
	private static partial Regex Tag();

	/// <summary>The blocks of <paramref name="markdown"/>; empty for none. At most <paramref name="maxBlocks"/> (a runaway body is cut).</summary>
	public static IReadOnlyList<NotesBlock> Parse(string? markdown, int maxBlocks = 400)
	{
		var blocks = new List<NotesBlock>();
		if (string.IsNullOrWhiteSpace(markdown))
		{
			return blocks;
		}

		var paragraph = new StringBuilder();
		void Flush()
		{
			if (paragraph.Length > 0)
			{
				blocks.Add(new NotesBlock(NotesBlockKind.Paragraph, 0, Inlines(paragraph.ToString())));
				paragraph.Clear();
			}
		}

		var inFence = false;
		foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
		{
			if (blocks.Count >= maxBlocks)
			{
				break;
			}

			var line = Tag().Replace(raw.TrimEnd(), "");
			if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
			{
				Flush();
				inFence = !inFence;
				continue;
			}

			if (inFence)
			{
				if (line.Trim().Length > 0)
				{
					blocks.Add(new NotesBlock(NotesBlockKind.Paragraph, 0, [new NotesInline(line.Trim(), Code: true)]));
				}

				continue;
			}

			if (line.Trim().Length == 0 || line.Trim() is "---" or "***" or "___")
			{
				Flush();
				continue;
			}

			if (HeadingLine().Match(line) is { Success: true } heading)
			{
				Flush();
				blocks.Add(new NotesBlock(NotesBlockKind.Heading, heading.Groups[1].Length, Inlines(heading.Groups[2].Value)));
				continue;
			}

			var bullet = BulletLine().Match(line);
			if (!bullet.Success)
			{
				bullet = NumberedLine().Match(line);
			}

			if (bullet.Success)
			{
				Flush();
				var indent = bullet.Groups[1].Value.Replace("\t", "    ").Length;
				blocks.Add(new NotesBlock(NotesBlockKind.Bullet, Math.Min(3, indent / 2), Inlines(bullet.Groups[2].Value)));
				continue;
			}

			// a line under a bullet, indented: more of that bullet
			if (paragraph.Length == 0 && blocks.Count > 0 && blocks[^1].Kind == NotesBlockKind.Bullet && raw.StartsWith(' '))
			{
				var last = blocks[^1];
				blocks[^1] = last with { Inlines = [.. last.Inlines, new NotesInline(" "), .. Inlines(line.Trim())] };
				continue;
			}

			if (paragraph.Length > 0)
			{
				paragraph.Append(' ');
			}

			paragraph.Append(line.Trim());
		}

		Flush();
		return blocks;
	}

	/// <summary>A link target the notes may open: an absolute http(s) address with a host and no credentials; else null.</summary>
	public static string? SafeLink(string? url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
		&& uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;

	private static List<NotesInline> Inlines(string text)
	{
		var inlines = new List<NotesInline>();
		var at = 0;
		foreach (Match m in Inline().Matches(text))
		{
			if (m.Index > at)
			{
				inlines.Add(new NotesInline(text[at..m.Index]));
			}

			if (m.Groups["bold"].Success)
			{
				inlines.Add(new NotesInline(m.Groups["bold"].Value, Bold: true));
			}
			else if (m.Groups["code"].Success)
			{
				inlines.Add(new NotesInline(m.Groups["code"].Value, Code: true));
			}
			else if (m.Groups["text"].Success)
			{
				var link = SafeLink(m.Groups["url"].Value);
				inlines.Add(new NotesInline(m.Groups["text"].Value, Link: link));
			}
			else
			{
				var bare = m.Groups["bare"].Value;
				inlines.Add(new NotesInline(bare, Link: SafeLink(bare)));
			}

			at = m.Index + m.Length;
		}

		if (at < text.Length)
		{
			inlines.Add(new NotesInline(text[at..]));
		}

		return inlines;
	}
}
