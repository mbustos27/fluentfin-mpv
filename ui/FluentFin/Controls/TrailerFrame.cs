using FluentFin.Services;
using FluentFin.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.3.1: the theatre window's frame around the trailer (its Content: the web view). Drawn by the active layout theme's
/// <c>TrailerFrameTemplate</c> (a ControlTemplate for this type; Drive-in's little drive-in, Cinematic's and Editorial's glass)
/// or, without one (Classic), by the shared glass frame (<c>Styles\TrailerFrame.xaml</c>). Every template has a
/// <c>PART_CloseButton</c> ("Close trailer") and sizes the video's place by <see cref="VideoWidth"/> and <see cref="VideoHeight"/>.
/// </summary>
public sealed partial class TrailerFrame : ContentControl
{
	/// <summary>The theme key of a layout theme's own frame (optional: <see cref="ThemeContract.OptionalKeys"/>).</summary>
	public const string TemplateKey = "TrailerFrameTemplate";

	public const string CloseButtonPart = "PART_CloseButton";

	/// <summary>Narrator's name of the Close button.</summary>
	public const string CloseName = "Close trailer";

	public static readonly DependencyProperty TitleProperty =
		DependencyProperty.Register(nameof(Title), typeof(string), typeof(TrailerFrame), new PropertyMetadata("", (d, e) =>
		{
			var frame = (TrailerFrame)d;
			frame.MarqueeTitle = Themes.DriveIn.Controls.MarqueeSign.Upper(e.NewValue as string);
		}));

	public static readonly DependencyProperty MarqueeTitleProperty =
		DependencyProperty.Register(nameof(MarqueeTitle), typeof(string), typeof(TrailerFrame), new PropertyMetadata(""));

	public static readonly DependencyProperty VideoWidthProperty =
		DependencyProperty.Register(nameof(VideoWidth), typeof(double), typeof(TrailerFrame), new PropertyMetadata(640d));

	public static readonly DependencyProperty VideoHeightProperty =
		DependencyProperty.Register(nameof(VideoHeight), typeof(double), typeof(TrailerFrame), new PropertyMetadata(360d));

	/// <summary>The trailer's title ("Heat").</summary>
	public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

	/// <summary>The title in capitals, for a marquee.</summary>
	public string MarqueeTitle { get => (string)GetValue(MarqueeTitleProperty); private set => SetValue(MarqueeTitleProperty, value); }

	public double VideoWidth { get => (double)GetValue(VideoWidthProperty); set => SetValue(VideoWidthProperty, value); }

	public double VideoHeight { get => (double)GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

	/// <summary>The template's Close button was pressed.</summary>
	public event EventHandler? CloseRequested;

	private Button? _close;

	public TrailerFrame()
	{
		DefaultStyleKey = typeof(TrailerFrame);
		IsTabStop = false;
	}

	/// <summary>
	/// The active theme's frame: its <see cref="TemplateKey"/> when it is a ControlTemplate for this type, else (no key, or a
	/// key of another kind) null: the shared default stays.
	/// </summary>
	internal static ControlTemplate? ThemeTemplate(object? found) =>
		found is ControlTemplate template && (template.TargetType is null || template.TargetType == typeof(TrailerFrame)) ? template : null;

	/// <summary>Takes the active theme's frame, or the shared one.</summary>
	public void ApplyTheme()
	{
		if (ThemeTemplate(ThemeResources.Find(TemplateKey)) is { } template)
		{
			Template = template;
		}
		else
		{
			ClearValue(TemplateProperty);
		}
	}

	protected override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		if (_close is not null)
		{
			_close.Click -= Close_Click;
		}

		_close = GetTemplateChild(CloseButtonPart) as Button;
		if (_close is not null)
		{
			_close.Click += Close_Click;
			AutomationProperties.SetName(_close, CloseName);
			ToolTipService.SetToolTip(_close, "Close trailer (Esc)");
		}
	}

	/// <summary>Puts the keyboard on the Close button (the window just opened); false when the template has none.</summary>
	public bool FocusClose(FocusState how) => _close?.Focus(how) ?? false;

	private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
