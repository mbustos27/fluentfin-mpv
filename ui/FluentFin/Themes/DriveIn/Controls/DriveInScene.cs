using System.Globalization;
using FluentFin.Core.Theming;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>Which half of the lot a <see cref="DriveInScene"/> draws.</summary>
public enum SceneLayer
{
	/// <summary>Behind the screen and the sign: the sky, the sun, the hills, the ground, the screen's legs and the sign's poles.</summary>
	Back,

	/// <summary>In front of them: the parked cars and their speaker posts.</summary>
	Front,
}

/// <summary>
/// Drive-in: the lot at dusk, the theme guide's scene in vector shapes (no image files): a banded sky with stars and
/// four-point sparkles, a striped sun whose slices are real gaps, three thin rings, rose hills, a crimson mesa and maroon
/// near hills, the ground, the outdoor screen's lattice legs, the sign's poles, and 1950s cars (rear view, tail fins, lit
/// tail lights) facing the screen beside their speaker posts. Drawn once per size and never animated (any continuous motion
/// redraws the whole window, acrylic included). Light theme: the noon version of the same scene. Solid: no scanlines.
/// High contrast: nothing (the page's plain system background). Decorative: Narrator and the pointer never see it.
/// </summary>
public sealed partial class DriveInScene : Canvas
{
	public static readonly DependencyProperty LayerProperty =
		DependencyProperty.Register(nameof(Layer), typeof(SceneLayer), typeof(DriveInScene), new PropertyMetadata(SceneLayer.Back, (d, _) => ((DriveInScene)d).Rebuild()));

	public static readonly DependencyProperty LayoutProperty =
		DependencyProperty.Register(nameof(Layout), typeof(DriveInLayout), typeof(DriveInScene), new PropertyMetadata(null, (d, _) => ((DriveInScene)d).Rebuild()));

	public SceneLayer Layer { get => (SceneLayer)GetValue(LayerProperty); set => SetValue(LayerProperty, value); }

	public DriveInLayout? Layout { get => (DriveInLayout?)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }

	public DriveInScene()
	{
		IsHitTestVisible = false;
		AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
		ActualThemeChanged += (_, _) => Rebuild();
		Loaded += (_, _) =>
		{
			Subscribe();
			Rebuild();
		};
		Unloaded += (_, _) => Unsubscribe();
	}

	private bool _subscribed;

	private void Subscribe()
	{
		if (_subscribed)
		{
			return;
		}

		try { GlassMode.Changed += OnModeChanged; _subscribed = true; }
		catch (Exception) { }
	}

	private void Unsubscribe()
	{
		if (!_subscribed)
		{
			return;
		}

		try { GlassMode.Changed -= OnModeChanged; }
		catch (Exception) { }
		_subscribed = false;
	}

	private void OnModeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Rebuild);

	private static GlassModeKind Mode()
	{
		try { return GlassMode.Current; }
		catch (Exception) { return GlassModeKind.Full; }
	}

	private void Rebuild()
	{
		Children.Clear();
		if (Layout is not { } layout || !IsLoaded || BulbFrame.HighContrast())
		{
			Width = Layout?.Width ?? 0;
			Height = Layout?.SceneHeight ?? 0;
			return;
		}

		Width = layout.Width;
		Height = layout.SceneHeight;
		// the sun's halo and rings never spill out of the scene (a header's strip: onto the posters)
		Clip = new RectangleGeometry { Rect = new Rect(0, 0, layout.Width, layout.SceneHeight) };
		var light = ActualTheme == ElementTheme.Light;
		if (Layer == SceneLayer.Back)
		{
			DrawBack(layout, light, Mode() != GlassModeKind.Solid);
		}
		else
		{
			DrawFront(layout, light);
		}
	}

	// ---- the colours ---------------------------------------------------------------------------------------------------

	/// <summary>The dusk sky's bands, top to bottom (the concept's banded sky, hot pink kept out: it means "you can act on it").</summary>
	public static readonly (double From, Color Color)[] DuskBands =
	[
		(0, C("#1C0512")), (0.14, C("#2A0717")), (0.30, C("#500723")), (0.45, C("#7A0F35")), (0.57, C("#A51A43")),
		(0.67, C("#C9305A")), (0.75, C("#DD5A6E")), (0.82, C("#EC8A86")), (0.89, C("#F5B3A6")),
	];

	/// <summary>Noon: the same bands, pale at the top (the top bar's dark text reads on them) and warm at the horizon.</summary>
	public static readonly (double From, Color Color)[] NoonBands =
	[
		(0, C("#FFF6F3")), (0.20, C("#FDE7E1")), (0.36, C("#F9C9BF")), (0.50, C("#F7B4A8")), (0.62, C("#F5A8A5")),
		(0.72, C("#F3928A")), (0.81, C("#EC7E84")), (0.90, C("#EC6A79")),
	];

	/// <summary>The ground under the lot, which the page's rows sit on (DriveInGroundBrush).</summary>
	public static Color Ground(bool light) => light ? C("#FBEFEA") : C("#1A0710");

	internal static Color C(string hex) => Color.FromArgb(
		hex.Length == 9 ? byte.Parse(hex[1..3], NumberStyles.HexNumber) : (byte)255,
		byte.Parse(hex[^6..^4], NumberStyles.HexNumber), byte.Parse(hex[^4..^2], NumberStyles.HexNumber), byte.Parse(hex[^2..], NumberStyles.HexNumber));

	private static Color A(Color color, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

	/// <summary>The sky's gradient stops from its bands: each band flat, meeting the next over a short soft edge.</summary>
	public static IReadOnlyList<(double Offset, Color Color)> SkyStops(IReadOnlyList<(double From, Color Color)> bands)
	{
		var stops = new List<(double, Color)>();
		for (var i = 0; i < bands.Count; i++)
		{
			var end = i < bands.Count - 1 ? bands[i + 1].From : 1;
			stops.Add((bands[i].From, bands[i].Color));
			stops.Add((Math.Max(bands[i].From, end - 0.018), bands[i].Color));
		}

		return stops;
	}

	// ---- the back layer ------------------------------------------------------------------------------------------------

	private void DrawBack(DriveInLayout l, bool light, bool scanlines)
	{
		var w = l.Width;
		var g = l.Ground;
		var h = l.SceneHeight;

		// the sky, down to the ground
		var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
		foreach (var (offset, color) in SkyStops(light ? NoonBands : DuskBands))
		{
			sky.GradientStops.Add(new GradientStop { Offset = offset, Color = color });
		}

		Add(new Rectangle { Width = w, Height = g, Fill = sky });

		// stars in the dark upper sky (dusk only), and the four-point sparkles
		var random = new Random(7);
		if (!light)
		{
			var stars = new GeometryGroup();
			var bright = new GeometryGroup();
			for (var i = 0; i < Math.Round(70 * w / 1920 + 20); i++)
			{
				var x = random.NextDouble() * w;
				var y = random.NextDouble() * g * 0.42;
				var big = random.NextDouble() > 0.85;
				(big ? bright : stars).Children.Add(new EllipseGeometry { Center = new Point(x, y), RadiusX = big ? 1.4 : 0.9, RadiusY = big ? 1.4 : 0.9 });
			}

			Add(new Path { Data = stars, Fill = new SolidColorBrush(A(C("#FFE9E4"), 0.55)) });
			Add(new Path { Data = bright, Fill = new SolidColorBrush(A(C("#FFE9E4"), 0.85)) });
		}

		foreach (var (fx, fy, size) in Sparkles(l))
		{
			Sparkle(fx, fy, size, light ? C("#E6385F") : C("#F5A8A5"));
		}

		// the sun: its halo, three rings, and the striped disc (its slices are gaps the sky shows through)
		var sun = l.Sun;
		var r = l.SunRadius;
		var halo = new RadialGradientBrush();
		halo.GradientStops.Add(new GradientStop { Offset = 0, Color = A(C("#F7AA9C"), light ? 0.35 : 0.45) });
		halo.GradientStops.Add(new GradientStop { Offset = 1, Color = A(C("#F7AA9C"), 0) });
		Add(new Ellipse { Width = r * 4.4, Height = r * 4.4, Fill = halo }, sun.X - r * 2.2, sun.Y - r * 2.2);
		var ring = light ? C("#BC1E47") : C("#F5A8A5");
		foreach (var (k, opacity) in new[] { (1.25, 0.35), (1.5, 0.22), (1.8, 0.12) })
		{
			Add(new Ellipse { Width = r * k * 2, Height = r * k * 2, Stroke = new SolidColorBrush(ring), StrokeThickness = 1.5, Opacity = opacity }, sun.X - r * k, sun.Y - r * k);
		}

		var disc = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
		disc.GradientStops.Add(new GradientStop { Offset = 0, Color = C("#FFF4EE") });
		disc.GradientStops.Add(new GradientStop { Offset = 0.45, Color = C("#F5A8A5") });
		disc.GradientStops.Add(new GradientStop { Offset = 1, Color = light ? C("#EC6A79") : C("#F96985") });
		Add(new Path { Data = SlicedSun(sun, r), Fill = disc });

		// faint scanlines over the dark upper sky (dusk only; Solid drops them)
		if (scanlines && !light)
		{
			var lines = new GeometryGroup();
			for (var y = 0.0; y < g * 0.42; y += 3)
			{
				lines.Children.Add(new RectangleGeometry { Rect = new Rect(0, y, w, 1) });
			}

			Add(new Path { Data = lines, Fill = new SolidColorBrush(A(Colors(0, 0, 0), 0.055)) });
		}

		// the hills: far rose, the crimson mesa, the near maroon; then the ground
		var sx = w / 1000;
		// the hills keep their height on a tall lot and shrink on a short strip (Compact Home, a header), so the sky still reads
		var hs = Math.Clamp(g / 600, 0.45, 1);
		double Y(double v) => g - 110 * hs + v * 1.1 * hs;
		string P(double x, double v) => string.Create(CultureInfo.InvariantCulture, $"{x * sx:0.#},{Y(v):0.#}");
		var bottom = string.Create(CultureInfo.InvariantCulture, $"L{w:0.#},{h:0.#} L0,{h:0.#} Z");
		var far = $"M{P(0, 20)} C{P(120, -18)} {P(220, -10)} {P(320, 6)} C{P(420, 22)} {P(520, -28)} {P(640, -14)} C{P(760, 0)} {P(860, 16)} {P(1000, -8)} {bottom}";
		var mesa = $"M{P(0, 42)} L{P(150, 42)} L{P(172, 14)} L{P(318, 14)} L{P(338, 34)} L{P(520, 34)} C{P(600, 32)} {P(680, 46)} {P(760, 42)} L{P(790, 20)} L{P(920, 20)} L{P(946, 44)} L{P(1000, 44)} {bottom}";
		var near = $"M{P(0, 70)} C{P(160, 58)} {P(300, 78)} {P(470, 70)} C{P(640, 62)} {P(820, 76)} {P(1000, 68)} {bottom}";
		// the far hills are opaque (the art pass: a translucent hill showed the sun's lower edge through it): the rose blended
		// into the horizon band once, here
		Add(new Path { Data = Geo(far), Fill = new SolidColorBrush(light ? C("#F1919C") : C("#EA5771")) });
		Add(new Path { Data = Geo(mesa), Fill = new SolidColorBrush(light ? C("#D9506E") : C("#BC1E47")) });
		Add(new Path { Data = Geo(near), Fill = new SolidColorBrush(light ? C("#A8324F") : C("#500723")) });

		var ground = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
		ground.GradientStops.Add(new GradientStop { Offset = 0, Color = light ? C("#7A2A3E") : C("#1A0710") });
		ground.GradientStops.Add(new GradientStop { Offset = light ? 0.55 : 1, Color = Ground(light) });
		Add(new Rectangle { Width = w, Height = Math.Max(0, h - g), Fill = ground }, 0, g);
		Add(new Rectangle { Width = w, Height = 2, Fill = new SolidColorBrush(A(C("#F7AA9C"), light ? 0.25 : 0.08)) }, 0, g - 2);

		// the screen's lattice legs and its crossbar; the sign's two poles
		var steel = light ? C("#5A2234") : C("#2A0A16");
		if (l.HasScreen)
		{
			var screen = l.Screen;
			var k = Math.Clamp(screen.Width / 1000, 0.5, 1.4);
			var leg = Math.Round(28 * k);
			var legs = new GeometryGroup();
			var braces = new GeometryGroup();
			var top = screen.Bottom;
			var foot = g + 6;
			foreach (var x in new[] { screen.X + screen.Width * 0.16, screen.X + screen.Width * 0.5 - leg / 2, screen.X + screen.Width * 0.84 - leg })
			{
				legs.Children.Add(new RectangleGeometry { Rect = new Rect(x, top, leg, Math.Max(0, foot - top)) });
				for (var y = top; y < foot - 6; y += 22 * k)
				{
					braces.Children.Add(new LineGeometry { StartPoint = new Point(x, y), EndPoint = new Point(x + leg, y + 22 * k) });
					braces.Children.Add(new LineGeometry { StartPoint = new Point(x + leg, y), EndPoint = new Point(x, y + 22 * k) });
				}
			}

			Add(new Path { Data = legs, Stroke = new SolidColorBrush(steel), StrokeThickness = Math.Max(3, 5 * k) });
			Add(new Path { Data = braces, Stroke = new SolidColorBrush(steel), StrokeThickness = Math.Max(2, 3 * k) });
			Add(new Rectangle { Width = screen.Width * 0.8, Height = Math.Max(4, 6 * k), Fill = new SolidColorBrush(steel) }, screen.X + screen.Width * 0.1, top + 16 * k);
		}

		if (l.HasSign)
		{
			var pole = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
			pole.GradientStops.Add(new GradientStop { Offset = 0, Color = steel });
			pole.GradientStops.Add(new GradientStop { Offset = 0.5, Color = light ? C("#7A3448") : C("#4A1A28") });
			pole.GradientStops.Add(new GradientStop { Offset = 1, Color = steel });
			var board = l.Board;
			foreach (var x in new[] { board.X + board.Width * 0.3, board.X + board.Width * 0.7 - 12 })
			{
				Add(new Rectangle { Width = 12, Height = Math.Max(0, g + 6 - board.Bottom), Fill = pole }, x, board.Bottom);
			}
		}
	}

	/// <summary>The sparkles' places (x, y, size): in the open sky, clear of the screen and the sign.</summary>
	internal static IEnumerable<(double X, double Y, double Size)> Sparkles(DriveInLayout l)
	{
		var w = l.Width;
		var candidates = new List<(double, double, double)>
		{
			(w * 0.035, l.Ground * 0.13, 14),
			(l.HasScreen && l.HasSign ? (l.Screen.Right + l.Board.Left) / 2 - 40 : w * 0.62, l.Ground * 0.16, 30),
			(l.HasScreen && l.HasSign ? (l.Screen.Right + l.Board.Left) / 2 + 20 : w * 0.66, l.Ground * 0.33, 16),
			(w * 0.98, l.Ground * 0.46, 14),
			(l.HasScreen && l.HasSign ? l.Screen.Right + 50 : w * 0.6, l.Ground * 0.48, 12),
		};
		foreach (var (x, y, size) in candidates)
		{
			var rect = new Rect(x - size, y - size, size * 2, size * 2);
			if (Overlaps(rect, l.Screen) || Overlaps(rect, l.Board) || Overlaps(rect, l.Arrow) || y < 24)
			{
				continue;
			}

			yield return (x, y, size);
		}
	}

	private static bool Overlaps(Rect a, Rect b) => !b.IsEmpty && a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

	/// <summary>A four-point sparkle with curved sides, and its soft glow (a larger faint copy: no effect).</summary>
	private void Sparkle(double x, double y, double size, Color color)
	{
		string Star(double s) => string.Create(CultureInfo.InvariantCulture,
			$"M{x:0.##},{y - s:0.##} Q{x + s * 0.12:0.##},{y - s * 0.12:0.##} {x + s:0.##},{y:0.##} Q{x + s * 0.12:0.##},{y + s * 0.12:0.##} {x:0.##},{y + s:0.##} Q{x - s * 0.12:0.##},{y + s * 0.12:0.##} {x - s:0.##},{y:0.##} Q{x - s * 0.12:0.##},{y - s * 0.12:0.##} {x:0.##},{y - s:0.##} Z");
		var half = size / 2;
		var glow = new RadialGradientBrush();
		glow.GradientStops.Add(new GradientStop { Offset = 0, Color = A(color, 0.45) });
		glow.GradientStops.Add(new GradientStop { Offset = 1, Color = A(color, 0) });
		Add(new Ellipse { Width = half * 1.6, Height = half * 1.6, Fill = glow }, x - half * 0.8, y - half * 0.8);
		Add(new Path { Data = Geo(Star(half)), Fill = new SolidColorBrush(color), Opacity = 0.9 });
	}

	/// <summary>The sun as horizontal slices of its disc, with five gaps that widen downwards (the concept's striped sun).</summary>
	public static Geometry SlicedSun(Point centre, double r)
	{
		var gaps = new List<(double Top, double Bottom)>();
		for (var i = 0; i < 5; i++)
		{
			var top = centre.Y - r * 0.42 + i * (r * 0.13 + i * r * 0.03);
			gaps.Add((top, top + r * 0.035 + i * r * 0.022));
		}

		var path = new PathGeometry();
		var from = centre.Y - r;
		foreach (var (top, bottom) in gaps.Append((centre.Y + r, centre.Y + r)))
		{
			var to = Math.Min(top, centre.Y + r);
			if (to - from > 0.5)
			{
				path.Figures.Add(Slice(centre, r, from, to));
			}

			from = Math.Max(from, bottom);
			if (from >= centre.Y + r)
			{
				break;
			}
		}

		return path;
	}

	private static PathFigure Slice(Point c, double r, double top, double bottom)
	{
		double Half(double y) => Math.Sqrt(Math.Max(0, r * r - (y - c.Y) * (y - c.Y)));
		var figure = new PathFigure { StartPoint = new Point(c.X - Half(top), top), IsClosed = true, IsFilled = true };
		figure.Segments.Add(new LineSegment { Point = new Point(c.X + Half(top), top) });
		figure.Segments.Add(new ArcSegment { Point = new Point(c.X + Half(bottom), bottom), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
		figure.Segments.Add(new LineSegment { Point = new Point(c.X - Half(bottom), bottom) });
		figure.Segments.Add(new ArcSegment { Point = new Point(c.X - Half(top), top), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
		return figure;
	}

	// ---- the front layer: the cars --------------------------------------------------------------------------------------

	private void DrawFront(DriveInLayout l, bool light)
	{
		foreach (var row in l.Cars)
		{
			var body = row.Near ? (light ? C("#3A1222") : C("#0E0308")) : (light ? C("#5A2234") : C("#22081A"));
			var glass = row.Near ? A(C("#F7AA9C"), 0.2) : A(C("#F96985"), 0.22);
			foreach (var x in row.Xs)
			{
				Car(x, row.Base, row.Scale, body, glass);
			}
		}
	}

	/// <summary>A 1950s sedan from behind: a low wide body, a rounded roof, two tail fins with their lights, its speaker post beside it.</summary>
	private void Car(double x, double baseline, double k, Color body, Color glass)
	{
		double X(double v) => x + v * k;
		double Y(double v) => baseline - 64 * k + v * k;
		string Pt(double vx, double vy) => string.Create(CultureInfo.InvariantCulture, $"{X(vx):0.##},{Y(vy):0.##}");

		// a contact shadow under the car, so it sits on the lot
		Add(new Ellipse { Width = 236 * k, Height = 12 * k, Fill = new SolidColorBrush(A(C("#000000"), 0.35)) }, X(-8), Y(60));

		// the speaker post and its speaker
		Add(new Rectangle { Width = 3 * k, Height = 70 * k, Fill = new SolidColorBrush(body) }, x - 15 * k, baseline - 70 * k);
		Add(new Rectangle { Width = 15 * k, Height = 10 * k, RadiusX = 2.5 * k, RadiusY = 2.5 * k, Fill = new SolidColorBrush(body) }, x - 21 * k, baseline - 78 * k);

		var shell = $"M{Pt(0, 64)} L{Pt(0, 34)} L{Pt(6, 14)} L{Pt(18, 30)} L{Pt(40, 30)} L{Pt(56, 8)} Q{Pt(62, 2)} {Pt(74, 2)} L{Pt(146, 2)} Q{Pt(158, 2)} {Pt(164, 8)} L{Pt(180, 30)} L{Pt(202, 30)} L{Pt(214, 14)} L{Pt(220, 34)} L{Pt(220, 64)} Z";
		Add(new Path { Data = Geo(shell), Fill = new SolidColorBrush(body), Stroke = new SolidColorBrush(A(C("#F7AA9C"), 0.34)), StrokeThickness = Math.Max(0.8, 1.2 * k) });
		Add(new Path { Data = Geo($"M{Pt(66, 9)} L{Pt(154, 9)} L{Pt(168, 28)} L{Pt(52, 28)} Z"), Fill = new SolidColorBrush(glass) });
		Add(new Rectangle { Width = 216 * k, Height = 6 * k, RadiusX = 3 * k, RadiusY = 3 * k, Fill = new SolidColorBrush(C("#5A2A38")) }, X(2), Y(48));
		Add(new Rectangle { Width = 36 * k, Height = 9 * k, RadiusX = 1.5 * k, RadiusY = 1.5 * k, Fill = new SolidColorBrush(C("#3A1524")) }, X(92), Y(36));
		foreach (var fin in new[] { 4.0, 211.0 })
		{
			Add(new Ellipse { Width = 20 * k, Height = 24 * k, Fill = new SolidColorBrush(A(C("#EC6A79"), 0.2)) }, X(fin + 3 - 10), Y(23 - 12));
			Add(new Rectangle { Width = 5 * k, Height = 12 * k, RadiusX = 2 * k, RadiusY = 2 * k, Fill = new SolidColorBrush(C("#FF8A8F")) }, X(fin), Y(17));
		}

		Add(new Rectangle { Width = 26 * k, Height = 6 * k, Fill = new SolidColorBrush(C("#08020A")) }, X(16), Y(62));
		Add(new Rectangle { Width = 26 * k, Height = 6 * k, Fill = new SolidColorBrush(C("#08020A")) }, X(178), Y(62));
	}

	// ---- helpers --------------------------------------------------------------------------------------------------------

	private static Color Colors(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

	private static Geometry Geo(string data) => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);

	private void Add(UIElement element, double x = 0, double y = 0)
	{
		if (element is FrameworkElement fe)
		{
			fe.IsHitTestVisible = false;
		}

		SetLeft(element, x);
		SetTop(element, y);
		Children.Add(element);
	}
}
