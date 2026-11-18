using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: a number in nixie tubes ("44", "1:12", "S1·E03", "0:50:52"). Each digit sits in its own tube (a warm dark glass
/// cell with a faint unlit 8 behind, and a honeycomb mesh on the larger sizes); letters and separators glow outside the
/// tubes. The glyphs are made at run time (<see cref="NixieGlyphs"/>) and lit by a stroke stack, never a blur effect: small
/// tubes (under 30 px) get only a tight halo (the theme rule for text under 24 px), larger ones a glow and a halo.
/// Orange is a number you read: a readout is never a control. Narrator reads it as one text, <see cref="Spoken"/> ("44
/// minutes left") or else its <see cref="Text"/>. High contrast: the system's text colour in plain bordered cells, no glow.
/// </summary>
public sealed partial class NixieReadout : StackPanel
{
	public static readonly DependencyProperty TextProperty =
		DependencyProperty.Register(nameof(Text), typeof(string), typeof(NixieReadout), new PropertyMetadata("", (d, _) => ((NixieReadout)d).Rebuild()));

	public static readonly DependencyProperty DigitHeightProperty =
		DependencyProperty.Register(nameof(DigitHeight), typeof(double), typeof(NixieReadout), new PropertyMetadata(22d, (d, _) => ((NixieReadout)d).Rebuild()));

	public static readonly DependencyProperty IsLitProperty =
		DependencyProperty.Register(nameof(IsLit), typeof(bool), typeof(NixieReadout), new PropertyMetadata(true, (d, _) => ((NixieReadout)d).Rebuild()));

	public static readonly DependencyProperty SpokenProperty =
		DependencyProperty.Register(nameof(Spoken), typeof(string), typeof(NixieReadout), new PropertyMetadata(""));

	/// <summary>The characters to show (unsupported ones are skipped; see <see cref="NixieGlyphs.Supported"/>).</summary>
	public string Text
	{
		get => (string)GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}

	/// <summary>A tube's height in DIP: 22 small (cards, the sign), 40 medium (the showtime board), 56 large (the season tubes).</summary>
	public double DigitHeight
	{
		get => (double)GetValue(DigitHeightProperty);
		set => SetValue(DigitHeightProperty, value);
	}

	/// <summary>Unlit: the digits in the tubes' resting colour (#B39890, still 4.5:1 on the glass), no glow: a season not picked.</summary>
	public bool IsLit
	{
		get => (bool)GetValue(IsLitProperty);
		set => SetValue(IsLitProperty, value);
	}

	/// <summary>What Narrator reads ("44 minutes left"); the text itself when empty.</summary>
	public string Spoken
	{
		get => (string)GetValue(SpokenProperty);
		set => SetValue(SpokenProperty, value);
	}

	// the tube's colours (the concept kit: lit #FFC9A8 over a #FF7A50 glow; the core near white; the resting digit #B39890)
	internal static readonly Color Core = Color.FromArgb(255, 0xFF, 0xE6, 0xD2);
	internal static readonly Color Lit = Color.FromArgb(255, 0xFF, 0xC9, 0xA8);
	internal static readonly Color Glow = Color.FromArgb(255, 0xFF, 0x7A, 0x50);
	internal static readonly Color Halo = Color.FromArgb(255, 0xFF, 0x5A, 0x28);
	internal static readonly Color Rest = Color.FromArgb(255, 0xB3, 0x98, 0x90);
	internal static readonly Color Ghost = Color.FromArgb(28, 0xFF, 0xC8, 0xAA);
	internal static readonly Color Glass = Color.FromArgb(255, 0x14, 0x05, 0x0A);

	/// <summary>The cell's width and the glyph's height, as shares of the tube's height (measured on the concept's tubes).</summary>
	public const double CellWidthShare = 0.66, GlyphShare = 0.58;

	/// <summary>Under this height a tube is small: a tight halo only, no mesh.</summary>
	public const double SmallBelow = 30;

	public NixieReadout()
	{
		Orientation = Orientation.Horizontal;
		IsHitTestVisible = false;
		VerticalAlignment = VerticalAlignment.Center;
		Loaded += (_, _) => Rebuild();
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new NixieReadoutAutomationPeer(this);

	internal string SpokenText => string.IsNullOrWhiteSpace(Spoken) ? Text ?? "" : Spoken;

	/// <summary>The stroke widths of the stack for a tube of <paramref name="height"/>: core, glow, halo (0 when not drawn).</summary>
	public static (double Core, double Glow, double Halo) Strokes(double height) =>
		height < SmallBelow
			? (Math.Max(1.25, height * 0.068), Math.Max(1.25, height * 0.068) + 2.2, 0)
			: (height * 0.062, height * 0.062 * 2.6, height * 0.062 * 5);

	/// <summary>The gap between two cells.</summary>
	public static double Gap(double height) => Math.Round(Math.Max(2, height * 0.11));

	private void Rebuild()
	{
		Children.Clear();
		var text = Text ?? "";
		var h = Math.Max(8, DigitHeight);
		Spacing = Gap(h);
		var highContrast = IsHighContrast();
		var glyphHeight = h * GlyphShare;
		var (core, glow, halo) = Strokes(h);
		var lit = IsLit;

		foreach (var c in text)
		{
			if (NixieGlyphs.For(c) is not { } glyph)
			{
				continue;
			}

			var glyphWidth = glyph.Advance / NixieGlyphs.BoxHeight * glyphHeight;
			var cellWidth = glyph.InTube ? Math.Round(h * CellWidthShare) : Math.Ceiling(glyphWidth + core);
			var cell = new Grid { Width = cellWidth, Height = h };
			AutomationProperties.SetAccessibilityView(cell, AccessibilityView.Raw);
			var x = (cellWidth - glyphWidth) / 2;
			var y = (h - glyphHeight) / 2;

			if (glyph.InTube)
			{
				cell.Children.Add(Tube(h, cellWidth, highContrast));
				if (!highContrast && h >= SmallBelow)
				{
					cell.Children.Add(Mesh(cellWidth, h));
				}

				if (!highContrast && NixieGlyphs.For('8') is { } eight)
				{
					var ghostX = (cellWidth - eight.Advance / NixieGlyphs.BoxHeight * glyphHeight) / 2;
					cell.Children.Add(Wire(NixieGlyphs.Geometry(eight, glyphHeight, ghostX, y), Ghost, core, 1));
				}
			}

			if (highContrast)
			{
				cell.Children.Add(Wire(NixieGlyphs.Geometry(glyph, glyphHeight, x, y), SystemColor("SystemColorWindowTextColor", Core), core, 1));
			}
			else if (!lit)
			{
				cell.Children.Add(Wire(NixieGlyphs.Geometry(glyph, glyphHeight, x, y), Rest, core, 1));
			}
			else
			{
				if (halo > 0)
				{
					cell.Children.Add(Wire(NixieGlyphs.Geometry(glyph, glyphHeight, x, y), Halo, halo, 0.16));
				}

				cell.Children.Add(Wire(NixieGlyphs.Geometry(glyph, glyphHeight, x, y), Glow, glow, h < SmallBelow ? 0.5 : 0.42));
				cell.Children.Add(Wire(NixieGlyphs.Geometry(glyph, glyphHeight, x, y), h < SmallBelow ? Lit : Core, core, 1));
			}

			Children.Add(cell);
		}
	}

	private static Path Wire(Geometry geometry, Color color, double thickness, double opacity) => new()
	{
		Data = geometry,
		Stroke = new SolidColorBrush(color),
		Fill = color == default ? null : new SolidColorBrush(color),
		StrokeThickness = thickness,
		StrokeStartLineCap = PenLineCap.Round,
		StrokeEndLineCap = PenLineCap.Round,
		StrokeLineJoin = PenLineJoin.Round,
		Opacity = opacity,
		IsHitTestVisible = false,
	};

	private static Border Tube(double h, double w, bool highContrast)
	{
		var radius = h < SmallBelow ? 4 : h < 48 ? 6 : 8;
		if (highContrast)
		{
			return new Border
			{
				CornerRadius = new CornerRadius(radius),
				BorderThickness = new Thickness(1),
				BorderBrush = new SolidColorBrush(SystemColor("SystemColorWindowTextColor", Core)),
				Background = new SolidColorBrush(SystemColor("SystemColorWindowColor", Glass)),
			};
		}

		var body = new RadialGradientBrush
		{
			Center = new Windows.Foundation.Point(0.5, 0.3),
			GradientOrigin = new Windows.Foundation.Point(0.5, 0.3),
			RadiusX = 0.9,
			RadiusY = 0.75,
		};
		body.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 0x4A, 0x1A, 0x14) });
		body.GradientStops.Add(new GradientStop { Offset = 0.7, Color = Color.FromArgb(255, 0x1E, 0x08, 0x08) });
		body.GradientStops.Add(new GradientStop { Offset = 1, Color = Glass });
		var rim = new LinearGradientBrush { StartPoint = new(0.5, 0), EndPoint = new(0.5, 1) };
		rim.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(90, 0xFF, 0xDC, 0xC8) });
		rim.GradientStops.Add(new GradientStop { Offset = 0.5, Color = Color.FromArgb(40, 0xFF, 0xA0, 0x78) });
		rim.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(24, 0xFF, 0xA0, 0x78) });
		return new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), BorderBrush = rim, Background = body };
	}

	/// <summary>The anode's honeycomb: two sets of fine diagonals at ±60 degrees, faint, inside the tube.</summary>
	private static Path Mesh(double w, double h)
	{
		var lines = new GeometryGroup();
		var step = Math.Max(3.5, h / 11);
		var slope = Math.Tan(Math.PI / 6); // 60 degrees from the horizontal: dx per dy
		for (var start = -h; start < w + h; start += step)
		{
			lines.Children.Add(new LineGeometry { StartPoint = new(start, 0), EndPoint = new(start + h * slope, h) });
			lines.Children.Add(new LineGeometry { StartPoint = new(start, h), EndPoint = new(start + h * slope, 0) });
		}

		return new Path
		{
			Data = lines,
			Stroke = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xBE, 0x96)),
			StrokeThickness = 0.6,
			Opacity = 0.09,
			Margin = new Thickness(3),
			Clip = new RectangleGeometry { Rect = new(0, 0, Math.Max(0, w - 6), Math.Max(0, h - 6)) },
			IsHitTestVisible = false,
		};
	}

	private static bool IsHighContrast()
	{
		try { return GlassMode.IsHighContrast; }
		catch (Exception) { return false; }
	}

	private static Color SystemColor(string key, Color fallback) =>
		Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color ? color : fallback;
}

/// <summary>A readout is one text for Narrator ("44 minutes left"); its tubes are not read one by one.</summary>
internal sealed partial class NixieReadoutAutomationPeer(NixieReadout owner) : FrameworkElementAutomationPeer(owner)
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

	protected override string GetClassNameCore() => nameof(NixieReadout);

	protected override string GetNameCore()
	{
		var name = AutomationProperties.GetName(owner);
		return string.IsNullOrEmpty(name) ? owner.SpokenText : name;
	}

	protected override IList<AutomationPeer>? GetChildrenCore() => null;

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;
}
