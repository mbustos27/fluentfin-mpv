using System.Numerics;
using FluentFin.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentFin.Controls;

/// <summary>
/// 1.1.4: the app's name as a lit nixie tube, for the title bar and the sign-in card. At display sizes (<see cref="NeonFontFrom"/>
/// and up) it is set in Monoton (SIL OFL 1.1, Assets\Fonts, shipped whole with its licence) and glows: a tight hot-pink glow and a
/// wide warm-orange halo, two composition shadows masked by the text itself, made once. Smaller (the title bar) it is the plain
/// display font in the tube's colour: a glow eats the edges of small letters. Full mode lights it with a short flicker the first
/// time it is shown (at most three dips, never to dark). Solid: no glow; high contrast: the system text colour, no glow. The text
/// is real text: Narrator reads it.
/// </summary>
public sealed partial class AppWordmark : Grid
{
	/// <summary>From this size up the wordmark is set in the neon font and glows (the font's own smallest readable size is about 40).</summary>
	public const double NeonFontFrom = 36;

	public const string NeonFont = "ms-appx:///Assets/Fonts/Monoton-Regular.ttf#Monoton";

	/// <summary>The tube's light: a warm pale orange on dark, crimson on the light theme's pale glass.</summary>
	public static Color TubeColor(bool light) => light ? Color.FromArgb(255, 0xBC, 0x1E, 0x47) : Color.FromArgb(255, 0xFF, 0xC4, 0xA6);

	/// <summary>The two glows, close in and wide: (colour, blur, opacity), dimmer on the light theme.</summary>
	/// <remarks>The art pass: tighter and fainter than first drawn (the wide orange halo read dirty); noon is one faint crimson glow.</remarks>
	public static (Color Color, float Blur, float Opacity)[] Glows(bool light) => light
		? [(Color.FromArgb(255, 0xBC, 0x1E, 0x47), 5f, 0.22f)]
		: [(Color.FromArgb(255, 0xF9, 0x69, 0x85), 5f, 0.8f), (Color.FromArgb(255, 0xFF, 0x7A, 0x50), 16f, 0.42f)];

	/// <summary>Letter spacing, in thousandths of an em: a sign's air at display size, a little at title-bar size.</summary>
	public static int Spacing(double size) => size >= NeonFontFrom ? 60 : 30;

	/// <summary>Whether the glow shows: display size, not Solid, not high contrast.</summary>
	public static bool Glows(double size, GlassModeKind mode, bool highContrast) => size >= NeonFontFrom && mode != GlassModeKind.Solid && !highContrast;

	public static readonly DependencyProperty TextProperty =
		DependencyProperty.Register(nameof(Text), typeof(string), typeof(AppWordmark), new PropertyMetadata("Nixie", (d, _) => ((AppWordmark)d).Apply()));

	public static readonly DependencyProperty SizeProperty =
		DependencyProperty.Register(nameof(Size), typeof(double), typeof(AppWordmark), new PropertyMetadata(64.0, (d, _) => ((AppWordmark)d).Apply()));

	private static bool _flickered;
	private readonly Border _glowHost = new() { IsHitTestVisible = false };
	private readonly TextBlock _text = new() { TextWrapping = TextWrapping.NoWrap, IsTextScaleFactorEnabled = true };
	private ContainerVisual? _glow;

	public AppWordmark()
	{
		IsHitTestVisible = false;
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(_glowHost, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
		Children.Add(_glowHost);
		Children.Add(_text);
		_text.SizeChanged += (_, _) => SizeGlow();
		Loaded += (_, _) =>
		{
			SafeMotion.Subscribe(OnModeChanged);
			ActualThemeChanged += OnThemeChanged;
			Apply();
			FlickerOnce();
		};
		Unloaded += (_, _) =>
		{
			SafeMotion.Unsubscribe(OnModeChanged);
			ActualThemeChanged -= OnThemeChanged;
		};
	}

	public string Text
	{
		get => (string)GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}

	/// <summary>The size of the letters, in DIP.</summary>
	public double Size
	{
		get => (double)GetValue(SizeProperty);
		set => SetValue(SizeProperty, value);
	}

	private void OnThemeChanged(FrameworkElement sender, object args) => Apply();

	private void OnModeChanged(object? sender, EventArgs e)
	{
		if (DispatcherQueue.HasThreadAccess)
		{
			Apply();
		}
		else
		{
			DispatcherQueue.TryEnqueue(Apply);
		}
	}

	private static (GlassModeKind Mode, bool HighContrast) ModeNow()
	{
		try { return (GlassMode.Current, GlassMode.IsHighContrast); }
		catch (Exception) { return (GlassModeKind.Solid, false); }
	}

	private void Apply()
	{
		var (mode, highContrast) = ModeNow();
		var light = ActualTheme == ElementTheme.Light;
		_text.Text = Text;
		_text.FontSize = Size;
		var neon = Size >= NeonFontFrom;
		_text.FontFamily = neon ? new FontFamily(NeonFont) : new FontFamily("Segoe UI Variable Display, Segoe UI");
		_text.FontWeight = neon ? FontWeights.Normal : FontWeights.SemiBold;
		_text.CharacterSpacing = Spacing(Size);
		if (highContrast)
		{
			_text.ClearValue(TextBlock.ForegroundProperty);
		}
		else
		{
			_text.Foreground = new SolidColorBrush(TubeColor(light));
		}

		var glows = Glows(Size, mode, highContrast);
		if (!glows)
		{
			if (_glow is not null)
			{
				ElementCompositionPreview.SetElementChildVisual(_glowHost, null);
				_glow = null;
			}

			return;
		}

		var compositor = ElementCompositionPreview.GetElementVisual(_glowHost).Compositor;
		_glow = compositor.CreateContainerVisual();
		var mask = _text.GetAlphaMask();
		foreach (var (color, blur, opacity) in Glows(light))
		{
			var shadow = compositor.CreateDropShadow();
			shadow.Color = color;
			shadow.BlurRadius = blur;
			shadow.Opacity = opacity;
			shadow.Offset = Vector3.Zero;
			shadow.Mask = mask;
			var sprite = compositor.CreateSpriteVisual();
			sprite.Shadow = shadow;
			_glow.Children.InsertAtTop(sprite);
		}

		ElementCompositionPreview.SetElementChildVisual(_glowHost, _glow);
		SizeGlow();
	}

	private void SizeGlow()
	{
		if (_glow is null)
		{
			return;
		}

		var size = new Vector2((float)_text.ActualWidth, (float)_text.ActualHeight);
		var offset = new Vector3((float)(_text.ActualOffset.X - _glowHost.ActualOffset.X), (float)(_text.ActualOffset.Y - _glowHost.ActualOffset.Y), 0);
		_glow.Size = size;
		foreach (var child in _glow.Children)
		{
			child.Size = size;
			child.Offset = offset;
		}
	}

	/// <summary>The tube lighting up, once per run, in Full mode with motion: on, a dip, on, a smaller dip, on (420 ms).</summary>
	private void FlickerOnce()
	{
		if (_flickered || !SafeMotion.Allowed || !Glows(Size, ModeNow().Mode, ModeNow().HighContrast))
		{
			return;
		}

		_flickered = true;
		var visual = ElementCompositionPreview.GetElementVisual(this);
		var flicker = visual.Compositor.CreateScalarKeyFrameAnimation();
		flicker.InsertKeyFrame(0f, 0f);
		flicker.InsertKeyFrame(80f / 420, 1f);
		flicker.InsertKeyFrame(140f / 420, 0.35f);
		flicker.InsertKeyFrame(220f / 420, 1f);
		flicker.InsertKeyFrame(300f / 420, 0.6f);
		flicker.InsertKeyFrame(1f, 1f);
		flicker.Duration = TimeSpan.FromMilliseconds(420);
		visual.StartAnimation("Opacity", flicker);
	}
}
