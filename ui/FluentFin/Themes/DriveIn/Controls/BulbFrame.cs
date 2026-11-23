using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: the marquee bulbs around a board or along a letter board's edges. Every bulb of a frame is one figure of a few
/// Paths (a faint halo, a glow, a warm core: no effect, no image), split into two alternating sets so the frame can chase
/// in a short burst (<see cref="Chase"/>, at most 5 seconds, only when motion is allowed). Decorative: Narrator and the
/// pointer never see it; high contrast draws none.
/// </summary>
public sealed partial class BulbFrame : Grid
{
	public static readonly DependencyProperty PitchProperty = Register(nameof(Pitch), 18d);
	public static readonly DependencyProperty InsetProperty = Register(nameof(Inset), 14d);
	public static readonly DependencyProperty OffsetProperty = Register(nameof(Offset), 8d);
	public static readonly DependencyProperty RadiusProperty = Register(nameof(Radius), 2d);
	public static readonly DependencyProperty SidesProperty = Register(nameof(Sides), true);
	public static readonly DependencyProperty SideOpacityProperty = Register(nameof(SideOpacity), 0.55);

	private static DependencyProperty Register<T>(string name, T fallback) =>
		DependencyProperty.Register(name, typeof(T), typeof(BulbFrame), new PropertyMetadata(fallback, (d, _) => ((BulbFrame)d).Rebuild()));

	/// <summary>Bulb to bulb, in DIP.</summary>
	public double Pitch { get => (double)GetValue(PitchProperty); set => SetValue(PitchProperty, value); }

	/// <summary>How far from the corners the rows start and end.</summary>
	public double Inset { get => (double)GetValue(InsetProperty); set => SetValue(InsetProperty, value); }

	/// <summary>How far in from each edge the bulbs' centres sit (0: on the edge itself).</summary>
	public double Offset { get => (double)GetValue(OffsetProperty); set => SetValue(OffsetProperty, value); }

	/// <summary>A bulb's lit core.</summary>
	public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

	/// <summary>Whether the left and right edges have bulbs too (a board does; a marquee header has top and bottom only).</summary>
	public bool Sides { get => (bool)GetValue(SidesProperty); set => SetValue(SidesProperty, value); }

	/// <summary>The side rows are dimmer, so the top and bottom read as the sign's lights.</summary>
	public double SideOpacity { get => (double)GetValue(SideOpacityProperty); set => SetValue(SideOpacityProperty, value); }

	internal static readonly Color Core = Color.FromArgb(255, 0xFF, 0xF3, 0xE8);
	internal static readonly Color Warm = Color.FromArgb(255, 0xF5, 0xA8, 0xA5);

	private readonly List<UIElement> _odd = [];
	private readonly DispatcherTimer _chase = new() { Interval = TimeSpan.FromMilliseconds(220) };
	private int _chaseSteps;

	/// <summary>A chase burst's length: a few blinks, never more than 5 seconds (the theme guide's limit).</summary>
	public static readonly TimeSpan ChaseLength = TimeSpan.FromSeconds(3.5);

	public BulbFrame()
	{
		IsHitTestVisible = false;
		AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
		SizeChanged += (_, _) => Rebuild();
		_chase.Tick += (_, _) =>
		{
			if (App.IsClosing)
			{
				return; // 1.7.3 fix round (the close crash's class): nothing in the window's teardown
			}

			_chaseSteps--;
			var dim = _chaseSteps > 0 && _chaseSteps % 2 == 1;
			foreach (var odd in _odd)
			{
				odd.Opacity = dim ? 0.25 : 1;
			}

			if (_chaseSteps <= 0)
			{
				_chase.Stop();
			}
		};
		Unloaded += (_, _) => _chase.Stop();
	}

	/// <summary>One burst of the chase (the odd bulbs blink against the even ones), only while motion is allowed.</summary>
	public void Chase()
	{
		if (!MotionAllowed() || _odd.Count == 0)
		{
			return;
		}

		_chaseSteps = (int)(ChaseLength.TotalMilliseconds / _chase.Interval.TotalMilliseconds);
		_chase.Start();
	}

	/// <summary>The bulbs' centres along one edge from <paramref name="from"/> to <paramref name="to"/>: evenly spread, about <paramref name="pitch"/> apart, both ends lit.</summary>
	public static IReadOnlyList<double> Positions(double from, double to, double pitch)
	{
		if (to - from < 0 || pitch <= 0)
		{
			return [];
		}

		var count = Math.Max(1, (int)Math.Round((to - from) / pitch));
		var step = (to - from) / count;
		return [.. Enumerable.Range(0, count + 1).Select(i => from + i * step)];
	}

	private void Rebuild()
	{
		Children.Clear();
		_odd.Clear();
		var w = ActualWidth;
		var h = ActualHeight;
		if (w <= 0 || h <= 0 || HighContrast())
		{
			return;
		}

		var top = Positions(Inset, w - Inset, Pitch);
		var main = new List<Point>();
		main.AddRange(top.Select(x => new Point(x, Offset)));
		main.AddRange(top.Select(x => new Point(x, h - Offset)));
		AddSet(main, 1);

		if (Sides && h - 2 * Inset > Pitch)
		{
			var side = Positions(Inset + Pitch * 0.5, h - Inset - Pitch * 0.5, Pitch);
			var points = side.Select(y => new Point(Offset, y)).Concat(side.Select(y => new Point(w - Offset, y))).ToList();
			AddSet(points, SideOpacity);
		}
	}

	private void AddSet(List<Point> points, double opacity)
	{
		for (var phase = 0; phase < 2; phase++)
		{
			var set = points.Where((_, i) => i % 2 == phase).ToList();
			if (set.Count == 0)
			{
				continue;
			}

			var group = new Grid { Opacity = opacity, IsHitTestVisible = false };
			group.Children.Add(Dots(set, Radius * 2.2, Warm, 0.2));
			group.Children.Add(Dots(set, Radius * 1.5, Warm, 0.6));
			group.Children.Add(Dots(set, Radius, Core, 1));
			Children.Add(group);
			if (phase == 1)
			{
				_odd.Add(group);
			}
		}
	}

	private static Path Dots(IEnumerable<Point> centres, double radius, Color color, double opacity)
	{
		var group = new GeometryGroup();
		foreach (var c in centres)
		{
			group.Children.Add(new EllipseGeometry { Center = c, RadiusX = radius, RadiusY = radius });
		}

		return new Path { Data = group, Fill = new SolidColorBrush(color), Opacity = opacity, IsHitTestVisible = false };
	}

	internal static bool HighContrast()
	{
		try { return GlassMode.IsHighContrast; }
		catch (Exception) { return false; }
	}

	internal static bool MotionAllowed()
	{
		try { return GlassMode.MotionAllowed; }
		catch (Exception) { return false; }
	}
}

/// <summary>Drive-in: a letter board's faint horizontal grooves (every few DIP), drawn once per size; none in high contrast.</summary>
public sealed partial class BoardLines : Grid
{
	public static readonly DependencyProperty PitchProperty =
		DependencyProperty.Register(nameof(Pitch), typeof(double), typeof(BoardLines), new PropertyMetadata(8d, (d, _) => ((BoardLines)d).Rebuild()));

	public double Pitch { get => (double)GetValue(PitchProperty); set => SetValue(PitchProperty, value); }

	public BoardLines()
	{
		IsHitTestVisible = false;
		AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
		SizeChanged += (_, _) => Rebuild();
	}

	private void Rebuild()
	{
		Children.Clear();
		if (ActualWidth <= 0 || ActualHeight <= 0 || Pitch < 2 || BulbFrame.HighContrast())
		{
			return;
		}

		var lines = new GeometryGroup();
		for (var y = Pitch / 2; y < ActualHeight; y += Pitch)
		{
			lines.Children.Add(new RectangleGeometry { Rect = new Rect(0, Math.Round(y), ActualWidth, 1) });
		}

		Children.Add(new Path { Data = lines, Fill = new SolidColorBrush(Color.FromArgb(10, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false });
	}
}
