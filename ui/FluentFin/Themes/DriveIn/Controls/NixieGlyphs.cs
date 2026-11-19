using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: the nixie tube's glyphs, made at run time. Each character is a cathode: a few wire strokes (lines and cubic
/// curves) in a 10 x 16 design box, drawn by <see cref="NixieReadout"/> as a stroke stack (a wide faint glow, a narrow one, a
/// bright core) with no image atlas and no blur effect, so any number of digits stays crisp at any display scale and costs
/// no GPU effect. The shapes follow a condensed sans figure (round 0, flagged 1, flat-topped 3, open 4), as real tubes do.
/// </summary>
public static class NixieGlyphs
{
	/// <summary>The design box: every digit and letter is drawn in 10 x 16 units (the box's height is the glyph's height).</summary>
	public const double BoxWidth = 10, BoxHeight = 16;

	/// <summary>One character: its advance in design units, its strokes, its dots (centre and radius), and whether it sits in a tube (digits do).</summary>
	public sealed record Glyph(char Char, double Advance, IReadOnlyList<Stroke> Strokes, IReadOnlyList<(Point Centre, double Radius)> Dots, bool InTube);

	/// <summary>One open or closed wire: a start point and its segments (a segment of one point is a line, of three a cubic curve).</summary>
	public sealed record Stroke(Point Start, IReadOnlyList<Point[]> Segments, bool Closed);

	private static readonly Dictionary<char, Glyph> s_glyphs = Build();

	/// <summary>The characters a readout can show: digits, ':', '·', '-', '–', '−', '/', '.', 'S', 'E', 'H', and a space.</summary>
	public static IEnumerable<char> Supported => s_glyphs.Keys;

	/// <summary>The glyph for <paramref name="c"/>, or null for a character the tubes cannot show (a readout skips it).</summary>
	public static Glyph? For(char c) => s_glyphs.TryGetValue(char.ToUpperInvariant(c), out var glyph) ? glyph : null;

	/// <summary>Whether every character of <paramref name="text"/> has a glyph.</summary>
	public static bool CanShow(string? text) => text is not null && text.All(c => For(c) is not null);

	/// <summary>
	/// The glyph's wire as geometry, scaled so its box is <paramref name="height"/> pixels tall, its box's top-left at
	/// (<paramref name="x"/>, <paramref name="y"/>). A new geometry every call: WinUI does not share one between shapes.
	/// </summary>
	public static Geometry Geometry(Glyph glyph, double height, double x, double y)
	{
		var k = height / BoxHeight;
		Point P(Point p) => new(x + p.X * k, y + p.Y * k);

		var path = new PathGeometry { FillRule = FillRule.Nonzero };
		foreach (var stroke in glyph.Strokes)
		{
			var figure = new PathFigure { StartPoint = P(stroke.Start), IsClosed = stroke.Closed, IsFilled = false };
			foreach (var segment in stroke.Segments)
			{
				figure.Segments.Add(segment.Length == 1
					? new LineSegment { Point = P(segment[0]) }
					: new BezierSegment { Point1 = P(segment[0]), Point2 = P(segment[1]), Point3 = P(segment[2]) });
			}

			path.Figures.Add(figure);
		}

		if (glyph.Dots.Count == 0)
		{
			return path;
		}

		var group = new GeometryGroup { FillRule = FillRule.Nonzero };
		if (glyph.Strokes.Count > 0)
		{
			group.Children.Add(path);
		}

		foreach (var (centre, radius) in glyph.Dots)
		{
			var c = P(centre);
			group.Children.Add(new EllipseGeometry { Center = c, RadiusX = radius * k, RadiusY = radius * k });
		}

		return group;
	}

	// ---- the cathodes -------------------------------------------------------------------------------------------------

	private static Dictionary<char, Glyph> Build()
	{
		var glyphs = new Dictionary<char, Glyph>();

		void Add(char c, double advance, bool tube, string wire, params (double X, double Y, double R)[] dots) =>
			glyphs[c] = new Glyph(c, advance, Parse(wire), [.. dots.Select(d => (new Point(d.X, d.Y), d.R))], tube);

		// digits: the tube's ten cathodes
		Add('0', 10, true, "M5,0.9 C8.3,0.9 9.1,3.9 9.1,8 C9.1,12.1 8.3,15.1 5,15.1 C1.7,15.1 0.9,12.1 0.9,8 C0.9,3.9 1.7,0.9 5,0.9 Z");
		Add('1', 10, true, "M2.9,3.7 L5.7,0.9 L5.7,15.1");
		Add('2', 10, true, "M1.3,4.3 C1.4,2.2 3,0.9 5,0.9 C7.2,0.9 8.7,2.3 8.7,4.4 C8.7,6.5 7.4,7.8 5.6,9.5 L1.1,15.1 L9,15.1");
		Add('3', 10, true, "M1.4,0.9 L8.5,0.9 L4.4,6.3 C7.2,6.2 9,8.1 9,10.7 C9,13.4 7.1,15.1 4.9,15.1 C3,15.1 1.6,14.2 1,12.7");
		Add('4', 10, true, "M7,15.1 L7,0.9 L1,11 L9.2,11");
		Add('5', 10, true, "M8.4,0.9 L2.1,0.9 L1.5,7 C2.4,6.2 3.6,5.8 5,5.8 C7.4,5.8 9,7.7 9,10.4 C9,13.2 7.2,15.1 4.8,15.1 C2.9,15.1 1.6,14.3 1,12.8");
		Add('6', 10, true, "M7.9,1.6 C7.1,1.1 6.2,0.9 5.3,0.9 C2.5,0.9 1,3.6 1,8.4 L1,10.6 C1,13.4 2.6,15.1 5,15.1 C7.4,15.1 9,13.4 9,10.6 C9,7.9 7.4,6.2 5,6.2 C3,6.2 1.6,7.4 1,9");
		Add('7', 10, true, "M1,0.9 L9,0.9 L3.7,15.1");
		Add('8', 10, true, "M5,7.3 C3,7.3 1.6,6 1.6,4.1 C1.6,2.2 3,0.9 5,0.9 C7,0.9 8.4,2.2 8.4,4.1 C8.4,6 7,7.3 5,7.3 Z M5,7.3 C2.6,7.3 1,8.8 1,11.2 C1,13.6 2.6,15.1 5,15.1 C7.4,15.1 9,13.6 9,11.2 C9,8.8 7.4,7.3 5,7.3 Z");
		Add('9', 10, true, "M2.1,14.4 C2.9,14.9 3.8,15.1 4.7,15.1 C7.5,15.1 9,12.4 9,7.6 L9,5.4 C9,2.6 7.4,0.9 5,0.9 C2.6,0.9 1,2.6 1,5.4 C1,8.1 2.6,9.8 5,9.8 C7,9.8 8.4,8.6 9,7");

		// the letters a code needs (S1 E03, 1 H 58 MIN) and the separators: lit, but outside a tube
		Add('S', 10, false, "M8.4,2.7 C7.8,1.5 6.6,0.9 5,0.9 C2.9,0.9 1.4,2.1 1.4,4 C1.4,6.2 3.2,7 5,7.6 C7.2,8.3 8.8,9.4 8.8,11.6 C8.8,13.8 7.2,15.1 5,15.1 C3.1,15.1 1.7,14.4 1,13");
		Add('E', 10, false, "M8.6,0.9 L1.4,0.9 L1.4,15.1 L8.6,15.1 M1.4,8 L7.4,8");
		Add('H', 10, false, "M1.4,0.9 L1.4,15.1 M8.6,0.9 L8.6,15.1 M1.4,8 L8.6,8");
		Add(':', 4.6, false, "", (2.3, 5.4, 1.05), (2.3, 11.4, 1.05));
		Add('·', 4.6, false, "", (2.3, 8.2, 1.05));
		Add('.', 4.6, false, "", (2.3, 14.6, 1.05));
		Add('-', 7, false, "M1.2,8.4 L5.8,8.4");
		Add('–', 7, false, "M1.2,8.4 L5.8,8.4");
		Add('−', 7, false, "M1.2,8.4 L5.8,8.4");
		Add('/', 8, false, "M6.8,0.9 L1.2,15.1");
		Add(' ', 4, false, "");
		return glyphs;
	}

	/// <summary>Reads a wire in absolute M, L, C and Z commands (the subset the glyphs above use).</summary>
	internal static IReadOnlyList<Stroke> Parse(string wire)
	{
		var strokes = new List<Stroke>();
		if (string.IsNullOrWhiteSpace(wire))
		{
			return strokes;
		}

		var tokens = Tokens(wire).ToList();
		var i = 0;
		Point? start = null;
		var segments = new List<Point[]>();
		Point Next()
		{
			var p = new Point(double.Parse(tokens[i], CultureInfo.InvariantCulture), double.Parse(tokens[i + 1], CultureInfo.InvariantCulture));
			i += 2;
			return p;
		}

		void Close(bool closed)
		{
			if (start is { } s)
			{
				strokes.Add(new Stroke(s, [.. segments], closed));
			}

			start = null;
			segments = [];
		}

		while (i < tokens.Count)
		{
			switch (tokens[i++])
			{
				case "M":
					Close(false);
					start = Next();
					break;
				case "L":
					segments.Add([Next()]);
					break;
				case "C":
					segments.Add([Next(), Next(), Next()]);
					break;
				case "Z":
					Close(true);
					break;
				default:
					throw new FormatException($"nixie glyph: unexpected '{tokens[i - 1]}' in \"{wire}\"");
			}
		}

		Close(false);
		return strokes;
	}

	private static IEnumerable<string> Tokens(string wire)
	{
		var number = new System.Text.StringBuilder();
		foreach (var c in wire)
		{
			if (char.IsLetter(c))
			{
				if (number.Length > 0) { yield return number.ToString(); number.Clear(); }
				yield return c.ToString();
			}
			else if (c is ',' or ' ')
			{
				if (number.Length > 0) { yield return number.ToString(); number.Clear(); }
			}
			else
			{
				number.Append(c);
			}
		}

		if (number.Length > 0)
		{
			yield return number.ToString();
		}
	}
}
