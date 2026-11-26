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
/// Drive-in: a number in nixie tubes ("44", "1:12", "S1 E03", "0:50:52"). Each digit sits in its own tube (a warm dark glass
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

	/// <summary>The letters and separators outside the tubes at noon, on the pale page: a deep maroon ink (7:1 on the noon ground).</summary>
	internal static readonly Color Ink = Color.FromArgb(255, 0x6A, 0x1E, 0x32);

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
		ActualThemeChanged += (_, _) => Rebuild();
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new NixieReadoutAutomationPeer(this);

	internal string SpokenText => string.IsNullOrWhiteSpace(Spoken) ? Text ?? "" : Spoken;

	/// <summary>The stroke widths of the stack for a tube of <paramref name="height"/>: core, glow, halo (0 when not drawn).</summary>
	public static (double Core, double Glow, double Halo) Strokes(double height) =>
		height < SmallBelow
			? (Math.Max(1.25, height * 0.068), Math.Max(1.25, height * 0.068) + 2.2, 0)
			: (height * 0.07, height * 0.07 * 2.4, height * 0.07 * 3.8);

	/// <summary>The gap between two cells.</summary>
	public static double Gap(double height) => Math.Round(Math.Max(2, height * 0.11));

	// the cells drawn, by character, and what they were drawn for: a new text redraws only the characters that changed (the
	// player's counters change a digit a second)
	private readonly List<(char Char, Grid Cell)> _cells = [];
	private (double Height, bool Lit, bool HighContrast, bool Light) _drawnFor;

	private void Rebuild()
	{
		var text = Text ?? "";
		var h = Math.Max(8, DigitHeight);
		Spacing = Gap(h);
		var highContrast = IsHighContrast();
		var lit = IsLit;
		// at noon, on the pale page (not on a board or the screen, which stay dark), the letters and separators outside the
		// tubes are printed in a dark ink: a lit glyph with no tube behind it has no contrast on a pale ground
		var light = ActualTheme == ElementTheme.Light;
		var signature = (h, lit, highContrast, light);
		if (signature != _drawnFor)
		{
			_cells.Clear();
			_drawnFor = signature;
		}

		var shown = text.Where(c => NixieGlyphs.For(c) is not null).ToList();
		var cells = new List<(char, Grid)>(shown.Count);
		for (var i = 0; i < shown.Count; i++)
		{
			cells.Add(i < _cells.Count && _cells[i].Char == shown[i] ? _cells[i] : (shown[i], Cell(NixieGlyphs.For(shown[i])!, h, lit, highContrast, light)));
		}

		_cells.Clear();
		_cells.AddRange(cells);
		Children.Clear();
		foreach (var (_, cell) in _cells)
		{
			Children.Add(cell);
		}
	}

	private static Grid Cell(NixieGlyphs.Glyph glyph, double h, bool lit, bool highContrast, bool light)
	{
		var glyphHeight = h * GlyphShare;
		var (core, glow, halo) = Strokes(h);
		var glyphWidth = glyph.Advance / NixieGlyphs.BoxHeight * glyphHeight;
		var cellWidth = glyph.InTube ? Math.Round(h * CellWidthShare) : Math.Ceiling(glyphWidth + core);
		var cell = new Grid { Width = cellWidth, Height = h };
		AutomationProperties.SetAccessibilityView(cell, AccessibilityView.Raw);
		var x = (cellWidth - glyphWidth) / 2;
		var y = (h - glyphHeight) / 2;

		if (glyph.InTube)
		{
			cell.Children.Add(Tube(h, cellWidth, highContrast));
			// the anode's mesh only on the large tubes (the art pass: on the 40 px tubes it read as hatching)
			if (!highContrast && h >= 48)
			{
				cell.Children.Add(Mesh(cellWidth, h));
			}

			// the unlit cathodes behind: fainter behind a resting digit, which they would muddle (the art pass)
			if (!highContrast && NixieGlyphs.For('8') is { } eight)
			{
				var ghostX = (cellWidth - eight.Advance / NixieGlyphs.BoxHeight * glyphHeight) / 2;
				cell.Children.Add(Wire(eight, glyphHeight, ghostX, y, Ghost, core, lit ? 1 : 0.45));
			}
		}

		if (highContrast)
		{
			cell.Children.Add(Wire(glyph, glyphHeight, x, y, SystemColor("SystemColorWindowTextColor", Core), core, 1, shared: false));
		}
		else if (light && !glyph.InTube)
		{
			cell.Children.Add(Wire(glyph, glyphHeight, x, y, Ink, core, 1));
		}
		else if (!lit)
		{
			cell.Children.Add(Wire(glyph, glyphHeight, x, y, Rest, core, 1));
		}
		else
		{
			if (halo > 0)
			{
				cell.Children.Add(Wire(glyph, glyphHeight, x, y, Halo, halo, 0.12));
			}

			cell.Children.Add(Wire(glyph, glyphHeight, x, y, Glow, glow, h < SmallBelow ? 0.5 : 0.42));
			cell.Children.Add(Wire(glyph, glyphHeight, x, y, h < SmallBelow ? Lit : Core, core, 1));
		}

		return cell;
	}

	/// <summary>
	/// One layer of a glyph's stroke stack. Nixie 1.5 (season speed, the same drawing): a glyph without dots is one geometry
	/// from its markup with no fill (<see cref="NixieGlyphs.WireGeometry"/>: its open wires were never filled), and the
	/// colours are brushes shared by every readout on the thread (<paramref name="shared"/>), not two new ones per layer.
	/// </summary>
	private static Path Wire(NixieGlyphs.Glyph glyph, double glyphHeight, double x, double y, Color color, double thickness, double opacity, bool shared = true)
	{
		var dots = glyph.Dots.Count > 0;
		var brush = shared ? SharedBrush(color) : new SolidColorBrush(color);
		return new()
		{
			Data = NixieGlyphs.WireGeometry(glyph, glyphHeight, x, y),
			Stroke = brush,
			Fill = dots && color != default ? brush : null,
			StrokeThickness = thickness,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round,
			Opacity = opacity,
			IsHitTestVisible = false,
		};
	}

	// Nixie 1.5: the readouts' brushes, one per colour, per thread (a brush belongs to the thread that made it)
	[ThreadStatic]
	private static Dictionary<Color, SolidColorBrush>? t_brushes;

	[ThreadStatic]
	private static (Brush Body, Brush Rim)? t_tubeBrushes;

	private static SolidColorBrush SharedBrush(Color color)
	{
		var brushes = t_brushes ??= [];
		if (!brushes.TryGetValue(color, out var brush))
		{
			brushes[color] = brush = new SolidColorBrush(color);
		}

		return brush;
	}

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

		// Nixie 1.5: the glass's two brushes are the same for every tube: made once per thread, shared
		var (body, rim) = t_tubeBrushes ??= TubeBrushes();
		return new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), BorderBrush = rim, Background = body };
	}

	private static (Brush Body, Brush Rim) TubeBrushes()
	{
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
		return (body, rim);
	}

	/// <summary>
	/// The anode's honeycomb: two sets of fine diagonals at ±60 degrees, faint, inside the tube. Nixie 1.5: one geometry from
	/// its markup (<see cref="MeshMarkup"/>), not some 60 line geometries per tube; the same lines.
	/// </summary>
	private static Path Mesh(double w, double h)
	{
		return new Path
		{
			Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), MeshMarkup(w, h)),
			Stroke = SharedBrush(Color.FromArgb(255, 0xFF, 0xBE, 0x96)),
			StrokeThickness = 0.6,
			Opacity = 0.06,
			Margin = new Thickness(3),
			Clip = new RectangleGeometry { Rect = new(0, 0, Math.Max(0, w - 6), Math.Max(0, h - 6)) },
			IsHitTestVisible = false,
		};
	}

	/// <summary>The mesh's lines as path markup: from (start, 0) down to the right and from (start, h) up to the right.</summary>
	internal static string MeshMarkup(double w, double h)
	{
		var text = new System.Text.StringBuilder(2048);
		var step = Math.Max(3.5, h / 11);
		var slope = Math.Tan(Math.PI / 6); // 60 degrees from the horizontal: dx per dy
		static string N(double v) => NixieGlyphs.Number(v);
		for (var start = -h; start < w + h; start += step)
		{
			text.Append(text.Length > 0 ? " M" : "M").Append(N(start)).Append(",0 L").Append(N(start + h * slope)).Append(',').Append(N(h));
			text.Append(" M").Append(N(start)).Append(',').Append(N(h)).Append(" L").Append(N(start + h * slope)).Append(",0");
		}

		return text.ToString();
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
internal sealed partial class NixieReadoutAutomationPeer(NixieReadout element) : FrameworkElementAutomationPeer(element)
{
	// Nixie 1.8.1 (BH-175-M2): its element through Owner, never a field of the peer: UI Automation keeps a peer after its
	// element has left (Narrator, any assistive tool), and a peer holding the element held it and its whole tree
	private NixieReadout Element => Owner as NixieReadout ?? throw new Microsoft.UI.Xaml.Automation.ElementNotAvailableException();

	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

	protected override string GetClassNameCore() => nameof(NixieReadout);

	protected override string GetNameCore()
	{
		var name = AutomationProperties.GetName(Element);
		return string.IsNullOrEmpty(name) ? Element.SpokenText : name;
	}

	protected override IList<AutomationPeer>? GetChildrenCore() => null;

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;
}
