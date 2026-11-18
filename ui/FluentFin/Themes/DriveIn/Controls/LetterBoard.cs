using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: an opaque letter board, the theme's panel: a deep board with faint grooves, a dark rim and a coral hairline,
/// marquee bulbs around it (<see cref="BulbFrame"/>). Content sits on it opaque (the theme's rule: posters and text on plain
/// ground). Styled in DriveIn.xaml (DriveInLetterBoardStyle, the implicit style); high contrast: a plain bordered panel.
/// </summary>
public sealed partial class LetterBoard : ContentControl
{
	public static readonly DependencyProperty HasBulbsProperty =
		DependencyProperty.Register(nameof(HasBulbs), typeof(bool), typeof(LetterBoard), new PropertyMetadata(true));

	public static readonly DependencyProperty HasSideBulbsProperty =
		DependencyProperty.Register(nameof(HasSideBulbs), typeof(bool), typeof(LetterBoard), new PropertyMetadata(true));

	/// <summary>Whether the board has its marquee bulbs (the sign and the item board do; the showtime board's rows do not).</summary>
	public bool HasBulbs { get => (bool)GetValue(HasBulbsProperty); set => SetValue(HasBulbsProperty, value); }

	/// <summary>Whether the left and right edges have bulbs too.</summary>
	public bool HasSideBulbs { get => (bool)GetValue(HasSideBulbsProperty); set => SetValue(HasSideBulbsProperty, value); }

	public LetterBoard()
	{
		IsTabStop = false;
		HorizontalContentAlignment = HorizontalAlignment.Stretch;
		VerticalContentAlignment = VerticalAlignment.Stretch;
	}

	/// <summary>The board's bulbs (a part of its template), for a chase burst.</summary>
	public BulbFrame? Bulbs => GetTemplateChild("Bulbs") as BulbFrame;
}

/// <summary>
/// Drive-in: a row's marquee header, a letter board with bulbs along its top and bottom and the row's name in the display
/// face, upper case. A level-2 heading for Narrator, named in the row's own words (<see cref="Text"/>: "Continue Watching");
/// the board shows <see cref="DisplayText"/>, the same in capitals. Styled by DriveInMarqueeStyle (DriveIn.xaml).
/// </summary>
public sealed partial class MarqueeSign : Control
{
	public static readonly DependencyProperty TextProperty =
		DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeSign), new PropertyMetadata("", (d, e) => ((MarqueeSign)d).DisplayText = Upper(e.NewValue as string)));

	public static readonly DependencyProperty LevelProperty =
		DependencyProperty.Register(nameof(Level), typeof(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel), typeof(MarqueeSign),
			new PropertyMetadata(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2));

	/// <summary>The heading's level: 2 for a row, 1 for a page's own name (the Library's).</summary>
	public Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel Level
	{
		get => (Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel)GetValue(LevelProperty);
		set => SetValue(LevelProperty, value);
	}

	public static readonly DependencyProperty DisplayTextProperty =
		DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(MarqueeSign), new PropertyMetadata(""));

	/// <summary>The row's name as it is said and as the harness and Narrator find it ("Continue Watching").</summary>
	public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

	/// <summary>What the board shows: <see cref="Text"/> in capitals.</summary>
	public string DisplayText { get => (string)GetValue(DisplayTextProperty); private set => SetValue(DisplayTextProperty, value); }

	public MarqueeSign()
	{
		IsTabStop = false;
	}

	/// <summary>A marquee's letters: the name in capitals (the board has no lower-case letters).</summary>
	public static string Upper(string? text) => (text ?? "").ToUpper(System.Globalization.CultureInfo.CurrentCulture);
}
