using FluentFin.Converters;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>
/// Cinematic's title art: the item's Logo image (its show's for an episode) when the server has one, else the title typeset
/// in one of four faces (<see cref="CinematicLabels.StyleFor"/>), at most two balanced lines, shrinking to 72 % of its size
/// before it would wrap a third time or overflow. On artwork only: the text is light in every theme (the art's dark band and
/// the scrims keep it at 4.5:1), the system text colour in high contrast.
/// </summary>
public sealed partial class CinematicTitleArt : Grid
{
	private readonly Image _logo = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
	private readonly TextBlock _text = new()
	{
		TextWrapping = TextWrapping.WrapWholeWords,
		MaxLines = 2,
		TextTrimming = TextTrimming.CharacterEllipsis,
		LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
		TextLineBounds = TextLineBounds.Tight,
		VerticalAlignment = VerticalAlignment.Bottom,
		IsTextScaleFactorEnabled = false,
	};

	private double _shown = double.NaN;

	public CinematicTitleArt()
	{
		Children.Add(_logo);
		Children.Add(_text);
		_text.SetBinding(TextBlock.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
		SizeChanged += (_, e) =>
		{
			if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
			{
				Fit();
			}
		};
		Apply();
	}

	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(BaseItemDto), typeof(CinematicTitleArt), new PropertyMetadata(null, (d, _) => ((CinematicTitleArt)d).Apply()));

	/// <summary>The item whose logo or title is drawn.</summary>
	public BaseItemDto? Dto
	{
		get => (BaseItemDto?)GetValue(DtoProperty);
		set => SetValue(DtoProperty, value);
	}

	public static readonly DependencyProperty TitleProperty =
		DependencyProperty.Register(nameof(Title), typeof(string), typeof(CinematicTitleArt), new PropertyMetadata(null, (d, _) => ((CinematicTitleArt)d).Apply()));

	/// <summary>The words to typeset (null: <see cref="CinematicLabels.TitleArtText"/> of <see cref="Dto"/>).</summary>
	public string? Title
	{
		get => (string?)GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public static readonly DependencyProperty SizeProperty =
		DependencyProperty.Register(nameof(Size), typeof(double), typeof(CinematicTitleArt), new PropertyMetadata(24.0, (d, _) => ((CinematicTitleArt)d).Apply()));

	/// <summary>The type size the faces are scaled from (the mockups' 104 on the hero, 24 on a still).</summary>
	public double Size
	{
		get => (double)GetValue(SizeProperty);
		set => SetValue(SizeProperty, value);
	}

	public static readonly DependencyProperty LogoMaxHeightProperty =
		DependencyProperty.Register(nameof(LogoMaxHeight), typeof(double), typeof(CinematicTitleArt), new PropertyMetadata(48.0, (d, e) => ((CinematicTitleArt)d)._logo.MaxHeight = (double)e.NewValue));

	public double LogoMaxHeight
	{
		get => (double)GetValue(LogoMaxHeightProperty);
		set => SetValue(LogoMaxHeightProperty, value);
	}

	public static readonly DependencyProperty LogoDecodeWidthProperty =
		DependencyProperty.Register(nameof(LogoDecodeWidth), typeof(int), typeof(CinematicTitleArt), new PropertyMetadata(320, (d, _) => ((CinematicTitleArt)d).Apply()));

	/// <summary>How wide the logo is decoded (its drawn width, not the server's).</summary>
	public int LogoDecodeWidth
	{
		get => (int)GetValue(LogoDecodeWidthProperty);
		set => SetValue(LogoDecodeWidthProperty, value);
	}

	public static readonly DependencyProperty UseLogoProperty =
		DependencyProperty.Register(nameof(UseLogo), typeof(bool), typeof(CinematicTitleArt), new PropertyMetadata(true, (d, _) => ((CinematicTitleArt)d).Apply()));

	public bool UseLogo
	{
		get => (bool)GetValue(UseLogoProperty);
		set => SetValue(UseLogoProperty, value);
	}

	public static readonly DependencyProperty IsHeadingProperty =
		DependencyProperty.Register(nameof(IsHeading), typeof(bool), typeof(CinematicTitleArt), new PropertyMetadata(false, (d, _) => ((CinematicTitleArt)d).Apply()));

	/// <summary>The page's heading (item pages): Narrator reads the logo by the title, as a level-1 heading; else decoration.</summary>
	public bool IsHeading
	{
		get => (bool)GetValue(IsHeadingProperty);
		set => SetValue(IsHeadingProperty, value);
	}

	public static readonly DependencyProperty ForegroundProperty =
		DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(CinematicTitleArt), new PropertyMetadata(null));

	public Brush? Foreground
	{
		get => (Brush?)GetValue(ForegroundProperty);
		set => SetValue(ForegroundProperty, value);
	}

	/// <summary>Whether the logo image is shown (else the typeset title).</summary>
	public bool ShowsLogo => _logo.Visibility == Visibility.Visible;

	private void Apply()
	{
		var title = Title ?? CinematicLabels.TitleArtText(Dto);
		var logo = UseLogo && Dto is { } dto ? ItemArtworkConverters.Logo(dto, LogoDecodeWidth) : null;
		_logo.Source = ItemArtworkConverters.Keep(_logo.Source, logo);
		_logo.MaxHeight = LogoMaxHeight;
		_logo.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
		_text.Visibility = logo is null && title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		var style = CinematicLabels.StyleFor(title);
		var (family, weight, stretch, italic, scale, spacing, caps) = Face(style);
		_text.FontFamily = family;
		_text.FontWeight = weight;
		_text.FontStretch = stretch;
		_text.FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
		_text.CharacterSpacing = spacing;
		_text.Text = caps ? title.ToUpper(System.Globalization.CultureInfo.CurrentCulture) : title;
		_scale = scale;
		_shown = double.NaN;

		var view = IsHeading ? AccessibilityView.Content : AccessibilityView.Raw;
		AutomationProperties.SetAccessibilityView(_logo, view);
		AutomationProperties.SetAccessibilityView(_text, view);
		AutomationProperties.SetHeadingLevel(_logo, IsHeading ? AutomationHeadingLevel.Level1 : AutomationHeadingLevel.None);
		AutomationProperties.SetHeadingLevel(_text, IsHeading ? AutomationHeadingLevel.Level1 : AutomationHeadingLevel.None);
		// Narrator reads the title as written (the typeset faces may draw it in capitals); the logo keeps the shared
		// item heading's automation id
		AutomationProperties.SetName(_logo, title);
		AutomationProperties.SetName(_text, title);
		AutomationProperties.SetAutomationId(_logo, IsHeading ? "LogoImage" : "");
		Fit();
	}

	private double _scale = 1;

	private static readonly FontFamily Bahnschrift = new("Bahnschrift");
	private static readonly FontFamily Serif = new("Georgia");
	private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");

	/// <summary>The four faces (Windows' own fonts: Bahnschrift, Georgia, Segoe UI Variable; nothing shipped).</summary>
	private static (FontFamily Family, Windows.UI.Text.FontWeight Weight, Windows.UI.Text.FontStretch Stretch, bool Italic, double Scale, int Spacing, bool Caps) Face(TitleArtStyle style) => style switch
	{
		TitleArtStyle.Condensed => (Bahnschrift, FontWeights.Bold, Windows.UI.Text.FontStretch.Condensed, false, 1.04, 20, true),
		TitleArtStyle.Serif => (Serif, FontWeights.Normal, Windows.UI.Text.FontStretch.Normal, true, 1.0, -10, false),
		TitleArtStyle.Light => (Display, FontWeights.Normal, Windows.UI.Text.FontStretch.Normal, false, 0.84, 80, true),
		_ => (Bahnschrift, FontWeights.SemiBold, Windows.UI.Text.FontStretch.Normal, false, 0.8, 90, true),
	};

	/// <summary>At most two lines and never wider than the room: the size steps down 6 % at a time, to 72 % at the least.</summary>
	private void Fit()
	{
		if (_text.Visibility != Visibility.Visible)
		{
			return;
		}

		var width = ActualWidth > 0 ? ActualWidth : MaxWidth;
		var full = Size * _scale;
		if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
		{
			SetSize(full);
			return;
		}

		var size = full;
		for (var i = 0; i < 12; i++)
		{
			SetSize(size);
			_text.MaxLines = 0;
			_text.Measure(new Size(width, double.PositiveInfinity));
			var lines = Math.Round(_text.DesiredSize.Height / (size * 0.98));
			var tooWide = LongestWordWidth(size) > width + 1;
			_text.MaxLines = 2;
			if ((lines <= 2 && !tooWide) || size * 0.94 < full * 0.72)
			{
				break;
			}

			size *= 0.94;
		}
	}

	private double LongestWordWidth(double size)
	{
		var longest = (_text.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(w => w.Length).FirstOrDefault();
		if (string.IsNullOrEmpty(longest))
		{
			return 0;
		}

		var probe = new TextBlock { Text = longest, FontFamily = _text.FontFamily, FontWeight = _text.FontWeight, FontStretch = _text.FontStretch, FontStyle = _text.FontStyle, FontSize = size, CharacterSpacing = _text.CharacterSpacing, IsTextScaleFactorEnabled = false };
		probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		return probe.DesiredSize.Width;
	}

	private void SetSize(double size)
	{
		if (size == _shown)
		{
			return;
		}

		_shown = size;
		_text.FontSize = size;
		_text.LineHeight = Math.Round(size * 0.98);
	}
}
