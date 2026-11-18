using FluentFin.Core.Theming;
using FluentFin.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;

namespace FluentFin.Controls;

/// <summary>
/// Draws <see cref="AmbientBackdrop.Current"/> under the pages and fades (600 ms) when it changes. With no artwork, the dusk scene
/// (1.1.4, <see cref="DuskScene"/>): in full where a page's text all sits on a glass card (sign-in and the server page, which are
/// not inside the shell, and empty states that ask for it), as a strip of hills elsewhere. The scene's starbursts glint, its sun
/// breathes and its hills sway in Full mode while the window is active; it is still in Reduced and Solid (Solid also drops the
/// scanlines); high contrast shows the deep colour alone.
/// </summary>
public sealed partial class AmbientBackdropView : UserControl
{
	/// <summary>The width the artwork is decoded at: small, and scaled up, so it is soft and cheap.</summary>
	public const int DecodeWidth = 10;

	/// <summary>The size the BlurHash is drawn at: a smooth colour field.</summary>
	public const int HashWidth = 32;
	public const int HashHeight = 18;

	public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(600);

	/// <summary>The drift: the artwork zooms between these scales, slowly, in Full mode only.</summary>
	public const float DriftFrom = 1.06f;
	public const float DriftTo = 1.16f;
	public static readonly TimeSpan DriftDuration = TimeSpan.FromSeconds(48);

	/// <summary>
	/// Whether the window's own backdrop should be desktop acrylic. 1.1.4: never: with no artwork the dusk scene covers the whole
	/// window (and Solid keeps Mica), so desktop acrylic would cost GPU time for nothing anyone sees.
	/// </summary>
	public static bool WantsDesktopAcrylic(Uri? artwork, GlassModeKind mode) => false;

	/// <summary>
	/// Which scene shows: none in high contrast; in full outside the shell (the server page and sign-in, even over the server's splash
	/// image) or when a page with no artwork asks (an empty state); none over artwork; else the strip.
	/// </summary>
	public static DuskSceneKind SceneFor(Uri? artwork, bool highContrast, bool insideShell, bool fullRequested) =>
		highContrast ? DuskSceneKind.None
		: !insideShell ? DuskSceneKind.Full
		: artwork is not null ? DuskSceneKind.None
		: fullRequested ? DuskSceneKind.Full
		: DuskSceneKind.Strip;

	/// <summary>Whether the backdrop moves (the artwork's drift, the scene's life): Full mode, Windows' animations on, the window active.</summary>
	public static bool Moves(GlassModeKind mode, bool animations, bool windowActive) => mode == GlassModeKind.Full && animations && windowActive;

	/// <summary>Whether the CRT touches (scanlines and vignette) show: the full scene, and not in Solid.</summary>
	public static bool ShowsCrt(DuskSceneKind scene, GlassModeKind mode) => scene == DuskSceneKind.Full && mode != GlassModeKind.Solid;

	/// <summary>How far each hill sways, far to near, in the hills' 1000-wide drawing: the nearer, the further, so the hills part slowly.</summary>
	public static readonly float[] HillSway = [3f, 5f, 7f];

	/// <summary>The time a hill takes to sway one way, and the sun's breath (its rings grow to this scale and back).</summary>
	public static readonly TimeSpan SwayDuration = TimeSpan.FromSeconds(48);
	public const float SunBreath = 1.04f;

	/// <summary>A starburst's glint: it shrinks to this scale and back, over 3 to 6 s, each at its own pace.</summary>
	public const float TwinkleScale = 0.85f;

	private Storyboard? _fade;
	private Uri? _shown;
	private DuskSceneKind _scene = DuskSceneKind.None;
	// when the scene moves (FluentFin.Core.Theming.SceneMotion decides: the full scene only, for a while); this view only acts
	private readonly SceneMotion _motion = new();
	private DispatcherQueueTimer? _settle;
	private Frame? _setupFrame;
	private readonly List<FrameworkElement> _stars = [];
	private FrameworkElement? _sun;
	private SpriteVisual? _scanlines;

	public AmbientBackdropView()
	{
		InitializeComponent();
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
	}

	/// <summary>The decoded-small source for an artwork address.</summary>
	public static BitmapImage SourceFor(Uri uri) => new() { DecodePixelWidth = DecodeWidth, UriSource = uri };

	/// <summary>
	/// What the backdrop draws: the BlurHash as a 32x18 picture (a smooth colour field, no recognisable shapes), else, with no hash,
	/// the artwork decoded 10 px wide and stretched.
	/// </summary>
	private static ImageSource SourceFor(Uri uri, string? hash)
	{
		if (BlurHash.ToBgra(hash, HashWidth, HashHeight) is { } pixels)
		{
			var bitmap = new WriteableBitmap(HashWidth, HashHeight);
			pixels.CopyTo(0, bitmap.PixelBuffer, 0, pixels.Length);
			bitmap.Invalidate();
			return bitmap;
		}

		return SourceFor(uri);
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		AmbientBackdrop.Changed += OnBackdropChanged;
		AmbientBackdrop.FullSceneChanged += OnFullSceneChanged;
		GlassMode.Changed += OnModeChanged;
		TitleTint.Changed += OnTintChanged;
		GlassActivity.Changed += OnActivityChanged;
		ActualThemeChanged += OnThemeChanged;
		GlassActivity.Attach(App.MainWindow, App.GetService<Microsoft.Extensions.Logging.ILogger<AmbientBackdropView>>());
		// the window's setup frame (the server page, sign-in, then the shell): the full scene until the shell is in it
		_setupFrame = (Parent as Panel)?.Children.OfType<Frame>().FirstOrDefault();
		if (_setupFrame is not null)
		{
			_setupFrame.Navigated += OnSetupNavigated;
		}

		UpdateTint();
		Show(AmbientBackdrop.Current, animate: false);
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		AmbientBackdrop.Changed -= OnBackdropChanged;
		AmbientBackdrop.FullSceneChanged -= OnFullSceneChanged;
		GlassMode.Changed -= OnModeChanged;
		TitleTint.Changed -= OnTintChanged;
		GlassActivity.Changed -= OnActivityChanged;
		ActualThemeChanged -= OnThemeChanged;
		if (_setupFrame is not null)
		{
			_setupFrame.Navigated -= OnSetupNavigated;
		}

		StopSceneMotion();
		_motion.Stopped();
		_fade?.Stop();
		// Translation can be stopped (or animated) only once it is enabled on the element; otherwise it throws
		ElementCompositionPreview.SetIsTranslationEnabled(Drift, true);
		var visual = ElementCompositionPreview.GetElementVisual(Drift);
		visual.StopAnimation("Scale");
		visual.StopAnimation("Translation");
	}

	private void OnTintChanged(object? sender, EventArgs e) => OnUi(UpdateTint);

	/// <summary>The title's hue as a clean mid colour, at <see cref="TitleTint.MaxOpacity"/>: all the colour a title gives the window.</summary>
	private void UpdateTint()
	{
		var c = OkLab.ToRgb(0.62, 0.12, TitleTint.Hue);
		TintBrush.Color = Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
		Tint.Opacity = TitleTint.MaxOpacity;
	}

	private void OnBackdropChanged(object? sender, Uri? uri) => Show(uri, animate: true);

	private void OnFullSceneChanged(object? sender, EventArgs e) => OnUi(UpdateMode);

	private void OnSetupNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) => UpdateMode();

	private void OnActivityChanged(object? sender, EventArgs e) => OnUi(() =>
	{
		// back in front: the full scene may move again for a while
		if (GlassActivity.IsActive)
		{
			_motion.BackInFront();
		}

		UpdateMode();
	});

	private void OnThemeChanged(FrameworkElement sender, object args) => BuildScene();

	// GlassMode.Changed may come from another thread
	private void OnModeChanged(object? sender, EventArgs e) => OnUi(UpdateMode);

	private void OnUi(Action action)
	{
		if (DispatcherQueue.HasThreadAccess)
		{
			action();
		}
		else
		{
			DispatcherQueue.TryEnqueue(() => action());
		}
	}

	private bool InsideShell => _setupFrame?.Content is Views.ShellPage;

	private void UpdateMode()
	{
		GlassModeKind mode;
		bool highContrast;
		try
		{
			mode = GlassMode.Current;
			highContrast = GlassMode.IsHighContrast;
		}
		catch (Exception)
		{
			mode = GlassModeKind.Solid;
			highContrast = false;
		}

		// Solid: no artwork (nothing to dim); the scene stays, as opaque shapes. High contrast: the deep colour alone.
		var solid = mode == GlassModeKind.Solid;
		var scene = SceneFor(_shown, highContrast, InsideShell, AmbientBackdrop.FullSceneRequested);
		var visibility = solid || scene != DuskSceneKind.None ? Visibility.Collapsed : Visibility.Visible;
		BackImage.Visibility = visibility;
		FrontImage.Visibility = visibility;
		var overArtwork = !solid && _shown is not null && scene == DuskSceneKind.None ? Visibility.Visible : Visibility.Collapsed;
		Dim.Visibility = overArtwork;
		Tint.Visibility = overArtwork;
		if (scene != _scene)
		{
			_scene = scene;
			BuildScene();
		}

		Scene.Visibility = scene == DuskSceneKind.None ? Visibility.Collapsed : Visibility.Visible;
		var crt = ShowsCrt(scene, mode) ? Visibility.Visible : Visibility.Collapsed;
		Vignette.Visibility = crt;
		// no scanlines where their strength is 0 (noon: the art pass)
		Scanlines.Visibility = crt == Visibility.Visible && DuskScene.ScanlineAlpha(ActualTheme == ElementTheme.Light) > 0 ? Visibility.Visible : Visibility.Collapsed;
		UpdateDrift();
		UpdateSceneMotion();
	}

	private bool MotionNow => SafeMotion.Allowed && Moves(GlassMode.Current, true, GlassActivity.IsActive);

	private void OnSceneSizeChanged(object sender, SizeChangedEventArgs e) => LayoutScene();

	/// <summary>Colours and shapes for the scene now wanted, in the theme in use.</summary>
	private void BuildScene()
	{
		StopSceneMotion();
		_motion.Rebuilt();
		SkyArt.Children.Clear();
		_stars.Clear();
		_sun = null;
		if (_scene == DuskSceneKind.None)
		{
			return;
		}

		var light = ActualTheme == ElementTheme.Light;
		var full = _scene == DuskSceneKind.Full;
		static Windows.UI.Color C(Rgb c, double alpha = 1) => Windows.UI.Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

		var sky = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
		if (full)
		{
			// bands, each flat across its middle, meeting the next over a short soft edge (DuskScene.BandEdge): banded, not hard-edged
			var bands = DuskScene.SkyBands(light);
			for (var i = 0; i < bands.Count; i++)
			{
				var (color, from, to) = bands[i];
				sky.GradientStops.Add(new GradientStop { Color = C(color), Offset = i == 0 ? from : from + DuskScene.BandEdge });
				sky.GradientStops.Add(new GradientStop { Color = C(color), Offset = i == bands.Count - 1 ? to : to - DuskScene.BandEdge });
			}
		}
		else
		{
			var (top, bottom) = DuskScene.SoftSky(light);
			sky.GradientStops.Add(new GradientStop { Color = C(top), Offset = 0 });
			sky.GradientStops.Add(new GradientStop { Color = C(bottom), Offset = 1 });
		}

		Sky.Fill = sky;
		FarHill.Fill = new SolidColorBrush(C(DuskScene.Hill(DuskHill.Far, light)));
		MesaHill.Fill = new SolidColorBrush(C(DuskScene.Hill(DuskHill.Mesa, light)));
		NearHill.Fill = new SolidColorBrush(C(DuskScene.Hill(DuskHill.Near, light)));
		Saguaro.Fill = new SolidColorBrush(C(DuskScene.Hill(DuskHill.Near, light)));
		Hills.Opacity = full ? 1 : DuskScene.StripOpacity(light);
		// the strip is a quiet horizon: rolling hills only, no mesa (the art pass: its flat-topped blocks read as slabs behind rows)
		MesaHill.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
		Saguaro.Visibility = full ? Visibility.Visible : Visibility.Collapsed;

		if (full)
		{
			// the sun: rings, then the striped disc
			var sun = new Canvas { IsHitTestVisible = false };
			var (sunTop, sunBottom) = DuskScene.SunGradient(light);
			for (var i = 0; i < DuskScene.RingOpacity.Length; i++)
			{
				sun.Children.Add(new Ellipse { Stroke = new SolidColorBrush(C(DuskScene.RingColor(light), DuskScene.RingOpacity[i])), StrokeThickness = 1.25, Tag = 1.2 + i * 0.2 });
			}

			sun.Children.Add(new Path
			{
				Fill = new LinearGradientBrush
				{
					StartPoint = new Point(0.5, 0),
					EndPoint = new Point(0.5, 1),
					GradientStops = { new GradientStop { Color = C(sunTop), Offset = 0 }, new GradientStop { Color = C(sunBottom), Offset = 1 } },
				},
				Tag = "disc",
			});
			SkyArt.Children.Add(sun);
			_sun = sun;

			foreach (var star in DuskScene.Stars)
			{
				var path = new Path { Fill = new SolidColorBrush(C(DuskScene.StarColor(light))), Opacity = DuskScene.StarOpacity, IsHitTestVisible = false, Tag = star };
				SkyArt.Children.Add(path);
				_stars.Add(path);
			}

			Vignette.Fill = new RadialGradientBrush
			{
				Center = new Point(0.5, 0.5),
				GradientOrigin = new Point(0.5, 0.5),
				RadiusX = 0.75,
				RadiusY = 0.75,
				GradientStops =
				{
					new GradientStop { Color = C(DuskScene.Night, 0), Offset = 0.55 },
					new GradientStop { Color = C(DuskScene.Night, DuskScene.VignetteAlpha(light)), Offset = 1 },
				},
			};
		}

		LayoutScene();
		UpdateSceneMotion();
	}

	/// <summary>Sizes and places what depends on the window's size: the hills' height, the sun, the stars, the saguaro, the scanlines.</summary>
	private void LayoutScene()
	{
		var width = Scene.ActualWidth;
		var height = Scene.ActualHeight;
		if (_scene == DuskSceneKind.None || width < 1 || height < 1)
		{
			return;
		}

		var full = _scene == DuskSceneKind.Full;
		var hills = height * (full ? DuskScene.SceneHillsHeight : DuskScene.StripHillsHeight);
		Hills.Height = hills;
		if (!full)
		{
			return;
		}

		// one saguaro, a little smaller than the hills are tall (its outline is 92 x 224)
		var cactus = hills * 0.66;
		Saguaro.Height = cactus;
		Saguaro.Width = cactus * 92 / 224;
		Saguaro.Margin = new Thickness(width * 0.16, 0, 0, hills * 0.1);

		if (_sun is Canvas sun)
		{
			var r = height * DuskScene.SunRadius;
			var cx = width * DuskScene.SunX;
			// the hills' crests are about 70 % of their height up; the disc sinks SunSink of its height behind them
			var cy = height - hills * 0.70 + r * (2 * DuskScene.SunSink - 1);
			Canvas.SetLeft(sun, cx);
			Canvas.SetTop(sun, cy);
			foreach (var child in sun.Children)
			{
				if (child is Ellipse ring && ring.Tag is double scale)
				{
					ring.Width = ring.Height = r * 2 * scale;
					Canvas.SetLeft(ring, -r * scale);
					Canvas.SetTop(ring, -r * scale);
				}
				else if (child is Path disc)
				{
					disc.Data = StripedSun(r);
					Canvas.SetLeft(disc, -r);
					Canvas.SetTop(disc, -r);
				}
			}
		}

		foreach (var element in _stars)
		{
			if (element is Path path && path.Tag is ValueTuple<double, double, double, int, bool> star)
			{
				var (x, y, radius, points, alternate) = star;
				var r = Math.Max(4, height * radius);
				path.Data = SparkleGeometry(r, alternate);
				Canvas.SetLeft(path, width * x - r);
				Canvas.SetTop(path, height * y - r);
				path.Width = path.Height = r * 2;
				ElementCompositionPreview.GetElementVisual(path).CenterPoint = new Vector3((float)r, (float)r, 0);
			}
		}

		UpdateScanlines();
	}

	/// <summary>Scanlines: one compositor gradient that wraps every 3 DIP, snapped to whole device pixels so no moire shows.</summary>
	private void UpdateScanlines()
	{
		var compositor = ElementCompositionPreview.GetElementVisual(Scanlines).Compositor;
		var scale = XamlRoot?.RasterizationScale ?? 1;
		var period = (float)(Math.Max(1, Math.Round(DuskScene.ScanlinePeriod * scale)) / scale);
		if (_scanlines is null)
		{
			_scanlines = compositor.CreateSpriteVisual();
			_scanlines.RelativeSizeAdjustment = Vector2.One;
			ElementCompositionPreview.SetElementChildVisual(Scanlines, _scanlines);
		}

		var line = Windows.UI.Color.FromArgb((byte)Math.Round(DuskScene.ScanlineAlpha(ActualTheme == ElementTheme.Light) * 255), 0, 0, 0);
		var lines = compositor.CreateLinearGradientBrush();
		lines.MappingMode = CompositionMappingMode.Absolute;
		lines.ExtendMode = CompositionGradientExtendMode.Wrap;
		lines.StartPoint = Vector2.Zero;
		lines.EndPoint = new Vector2(0, period);
		lines.ColorStops.Add(compositor.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(0, 0, 0, 0)));
		lines.ColorStops.Add(compositor.CreateColorGradientStop(0.66f, Windows.UI.Color.FromArgb(0, 0, 0, 0)));
		lines.ColorStops.Add(compositor.CreateColorGradientStop(0.67f, line));
		lines.ColorStops.Add(compositor.CreateColorGradientStop(1f, line));
		_scanlines.Brush = lines;
	}

	/// <summary>The striped sun as one geometry: its slices, wide at the top, thinner and further apart toward the horizon.</summary>
	private static PathGeometry StripedSun(double r)
	{
		var geometry = new PathGeometry();
		foreach (var (top, bottom) in DuskScene.SunBands(r))
		{
			var dxTop = Math.Sqrt(Math.Max(0, r * r - (top - r) * (top - r)));
			var dxBottom = Math.Sqrt(Math.Max(0, r * r - (bottom - r) * (bottom - r)));
			var arc = new Size(r, r);
			var figure = new PathFigure { StartPoint = new Point(r - dxTop, top), IsClosed = true, IsFilled = true };
			figure.Segments.Add(new LineSegment { Point = new Point(r + dxTop, top) });
			figure.Segments.Add(new ArcSegment { Point = new Point(r + dxBottom, bottom), Size = arc, SweepDirection = SweepDirection.Clockwise });
			figure.Segments.Add(new LineSegment { Point = new Point(r - dxBottom, bottom) });
			figure.Segments.Add(new ArcSegment { Point = new Point(r - dxTop, top), Size = arc, SweepDirection = SweepDirection.Clockwise });
			geometry.Figures.Add(figure);
		}

		return geometry;
	}

	/// <summary>A four-point sparkle with curved sides; with <paramref name="withDiagonal"/>, a smaller one turned 45 degrees inside it.</summary>
	private static PathGeometry SparkleGeometry(double r, bool withDiagonal)
	{
		var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
		void Add(double radius, double waist, double angle)
		{
			var (start, curves) = DuskScene.Sparkle(radius, waist, angle);
			var offset = r - radius; // centred in the 2r square
			var figure = new PathFigure { StartPoint = new Point(start.X + offset, start.Y + offset), IsClosed = true, IsFilled = true };
			foreach (var (control, end) in curves)
			{
				figure.Segments.Add(new QuadraticBezierSegment { Point1 = new Point(control.X + offset, control.Y + offset), Point2 = new Point(end.X + offset, end.Y + offset) });
			}

			geometry.Figures.Add(figure);
		}

		Add(r, 0.14, 0);
		if (withDiagonal)
		{
			Add(r * 0.55, 0.2, 45);
		}

		return geometry;
	}

	/// <summary>Carries out SceneMotion's step for the scene shown now.</summary>
	private void UpdateSceneMotion()
	{
		switch (_motion.Update(_scene, MotionNow))
		{
			case SceneMotionStep.Start:
				StartSceneMotion();
				break;
			case SceneMotionStep.Stop:
				StopSceneMotion();
				break;
		}
	}

	/// <summary>The scene's slow life (Full mode, window active): starbursts glint, the sun's rings breathe, the hills sway; settles after SceneMotion.Duration.</summary>
	private void StartSceneMotion()
	{
		_settle ??= DispatcherQueue.CreateTimer();
		_settle.Stop();
		_settle.Interval = SceneMotion.Duration;
		_settle.IsRepeating = false;
		_settle.Tick -= OnSettle;
		_settle.Tick += OnSettle;
		_settle.Start();
		var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
		var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
		Vector3KeyFrameAnimation Loop(Vector3 from, Vector3 to, double seconds)
		{
			var animation = compositor.CreateVector3KeyFrameAnimation();
			animation.InsertKeyFrame(0, from, ease);
			animation.InsertKeyFrame(1, to, ease);
			animation.Duration = TimeSpan.FromSeconds(seconds);
			animation.IterationBehavior = AnimationIterationBehavior.Forever;
			animation.Direction = AnimationDirection.Alternate;
			return animation;
		}

		var hills = new UIElement[] { FarHill, MesaHill, NearHill };
		for (var i = 0; i < hills.Length; i++)
		{
			ElementCompositionPreview.SetIsTranslationEnabled(hills[i], true);
			var from = i % 2 == 0 ? -HillSway[i] : HillSway[i];
			ElementCompositionPreview.GetElementVisual(hills[i]).StartAnimation("Translation",
				Loop(new Vector3(from, 0, 0), new Vector3(-from, 0, 0), SwayDuration.TotalSeconds + i * 5));
		}

		if (_sun is not null)
		{
			ElementCompositionPreview.GetElementVisual(_sun).StartAnimation("Scale", Loop(Vector3.One, new Vector3(SunBreath, SunBreath, 1), 30));
		}

		var seconds = 3.0;
		foreach (var star in _stars)
		{
			var visual = ElementCompositionPreview.GetElementVisual(star);
			visual.StartAnimation("Scale", Loop(Vector3.One, new Vector3(TwinkleScale, TwinkleScale, 1), seconds));
			seconds = seconds >= 6 ? 3.2 : seconds + 0.7;
		}
	}

	/// <summary>The full scene's time is up: each moving part eases to its rest pose (no jump mid-cycle), then nothing moves.</summary>
	private void OnSettle(DispatcherQueueTimer sender, object args)
	{
		_settle?.Stop();
		if (_motion.Settle() != SceneMotionStep.Ease)
		{
			return;
		}

		var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
		void Ease(Visual visual, string property, Vector3 rest)
		{
			var home = compositor.CreateVector3KeyFrameAnimation();
			home.InsertKeyFrame(1, rest);
			home.Duration = TimeSpan.FromMilliseconds(1200);
			visual.StartAnimation(property, home);
		}

		foreach (var hill in new UIElement[] { FarHill, MesaHill, NearHill })
		{
			Ease(ElementCompositionPreview.GetElementVisual(hill), "Translation", Vector3.Zero);
		}

		if (_sun is not null)
		{
			Ease(ElementCompositionPreview.GetElementVisual(_sun), "Scale", Vector3.One);
		}

		foreach (var star in _stars)
		{
			Ease(ElementCompositionPreview.GetElementVisual(star), "Scale", Vector3.One);
		}
	}

	private void StopSceneMotion()
	{
		_settle?.Stop();
		foreach (var hill in new UIElement[] { FarHill, MesaHill, NearHill })
		{
			ElementCompositionPreview.SetIsTranslationEnabled(hill, true);
			var visual = ElementCompositionPreview.GetElementVisual(hill);
			visual.StopAnimation("Translation");
			visual.Properties.InsertVector3("Translation", Vector3.Zero);
		}

		if (_sun is not null)
		{
			var sun = ElementCompositionPreview.GetElementVisual(_sun);
			sun.StopAnimation("Scale");
			sun.Scale = Vector3.One;
		}

		foreach (var star in _stars)
		{
			var visual = ElementCompositionPreview.GetElementVisual(star);
			visual.StopAnimation("Scale");
			visual.Scale = Vector3.One;
		}
	}

	private void OnDriftSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDrift();

	/// <summary>The slow zoom and sway, in Full mode with artwork shown; nothing in Reduced or Solid.</summary>
	private void UpdateDrift()
	{
		ElementCompositionPreview.SetIsTranslationEnabled(Drift, true);
		var visual = ElementCompositionPreview.GetElementVisual(Drift);
		visual.StopAnimation("Scale");
		visual.StopAnimation("Translation");
		if (!MotionNow || _shown is null || Drift.ActualWidth < 1)
		{
			visual.Scale = Vector3.One;
			return;
		}

		ElementCompositionPreview.SetIsTranslationEnabled(Drift, true);
		visual.CenterPoint = new Vector3((float)Drift.ActualWidth / 2, (float)Drift.ActualHeight / 2, 0);
		var compositor = visual.Compositor;
		var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));

		var zoom = compositor.CreateVector3KeyFrameAnimation();
		zoom.InsertKeyFrame(0, new Vector3(DriftFrom, DriftFrom, 1), ease);
		zoom.InsertKeyFrame(1, new Vector3(DriftTo, DriftTo, 1), ease);
		zoom.Duration = DriftDuration;
		zoom.IterationBehavior = AnimationIterationBehavior.Forever;
		zoom.Direction = AnimationDirection.Alternate;
		visual.StartAnimation("Scale", zoom);

		// a sway of up to 2% of the width, kept inside what the zoom hides (the zoom adds 3% on each side at least)
		var sway = (float)Drift.ActualWidth * 0.02f;
		var move = compositor.CreateVector3KeyFrameAnimation();
		move.InsertKeyFrame(0, new Vector3(-sway, 0, 0), ease);
		move.InsertKeyFrame(1, new Vector3(sway, 0, 0), ease);
		move.Duration = TimeSpan.FromSeconds(DriftDuration.TotalSeconds * 1.3);
		move.IterationBehavior = AnimationIterationBehavior.Forever;
		move.Direction = AnimationDirection.Alternate;
		visual.StartAnimation("Translation", move);
	}

	private void Show(Uri? uri, bool animate)
	{
		_fade?.Stop();
		_fade = null;
		_shown = uri;
		UpdateMode();

		// a fade cut short: what was coming in is what stays
		if (FrontImage.Opacity > 0.5)
		{
			BackImage.Source = FrontImage.Source;
			BackImage.Opacity = 1;
		}

		FrontImage.Opacity = 0;

		if (uri is null)
		{
			FadeOut(animate);
			return;
		}

		if (!animate)
		{
			BackImage.Source = SourceFor(uri, AmbientBackdrop.BlurHash);
			BackImage.Opacity = 1;
			FrontImage.Source = null;
			return;
		}

		// shown once decoded (OnFrontImageOpened), so a slow image never leaves a hole
		FrontImage.Source = SourceFor(uri, AmbientBackdrop.BlurHash);
		if (FrontImage.Source is WriteableBitmap)
		{
			// a drawn bitmap is ready at once: no ImageOpened to wait for
			StartFade();
		}
	}

	private void OnFrontImageOpened(object sender, RoutedEventArgs e)
	{
		if (_shown is null || FrontImage.Source is not BitmapImage { UriSource: { } source } || source != _shown)
		{
			return;
		}

		StartFade();
	}

	private void StartFade()
	{
		var fade = new Storyboard();
		var animation = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
		Storyboard.SetTarget(animation, FrontImage);
		Storyboard.SetTargetProperty(animation, "Opacity");
		fade.Children.Add(animation);
		fade.Completed += (_, _) =>
		{
			if (_fade != fade)
			{
				return;
			}

			// the back layer takes over, ready for the next fade
			BackImage.Source = FrontImage.Source;
			BackImage.Opacity = 1;
			FrontImage.Opacity = 0;
			_fade = null;
		};
		_fade = fade;
		fade.Begin();
	}

	private void FadeOut(bool animate)
	{
		if (!animate || BackImage.Opacity == 0)
		{
			BackImage.Opacity = 0;
			BackImage.Source = null;
			FrontImage.Source = null;
			return;
		}

		var fade = new Storyboard();
		var animation = new DoubleAnimation { To = 0, Duration = FadeDuration };
		Storyboard.SetTarget(animation, BackImage);
		Storyboard.SetTargetProperty(animation, "Opacity");
		fade.Children.Add(animation);
		fade.Completed += (_, _) =>
		{
			if (_fade != fade)
			{
				return;
			}

			BackImage.Opacity = 0;
			BackImage.Source = null;
			FrontImage.Source = null;
			_fade = null;
		};
		_fade = fade;
		fade.Begin();
	}
}
