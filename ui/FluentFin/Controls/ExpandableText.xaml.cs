using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// fluentfin-mpv: text that wraps and stops after <see cref="CollapsedLines"/> lines, with "Show more" / "Show less" when it is
/// longer (docs/research/ux-review.md I1). Screen readers get the whole text either way (a TextBlock's text is not trimmed for UI
/// Automation).
/// </summary>
public sealed partial class ExpandableText : UserControl
{
	public static readonly DependencyProperty TextProperty =
		DependencyProperty.Register(nameof(Text), typeof(string), typeof(ExpandableText), new PropertyMetadata("", (d, _) => ((ExpandableText)d).OnTextChanged()));

	public static readonly DependencyProperty CollapsedLinesProperty =
		DependencyProperty.Register(nameof(CollapsedLines), typeof(int), typeof(ExpandableText), new PropertyMetadata(4, (d, _) => ((ExpandableText)d).Apply()));

	private bool _expanded;

	public ExpandableText()
	{
		InitializeComponent();
		Apply();
	}

	public string Text
	{
		get => (string)GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}

	public int CollapsedLines
	{
		get => (int)GetValue(CollapsedLinesProperty);
		set => SetValue(CollapsedLinesProperty, value);
	}

	private void OnTextChanged()
	{
		// another item: start collapsed again
		_expanded = false;
		Apply();
	}

	private void Body_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => Apply();

	private void MoreButton_Click(object sender, RoutedEventArgs e)
	{
		_expanded = !_expanded;
		Apply();
	}

	private void Apply()
	{
		if (Body is null || MoreButton is null)
		{
			return;
		}

		var (maxLines, showToggle, toggleText) = ExpandableTextLayout.For(_expanded, Body.IsTextTrimmed, CollapsedLines);
		Body.MaxLines = maxLines;
		MoreButton.Content = toggleText;
		MoreButton.Visibility = showToggle ? Visibility.Visible : Visibility.Collapsed;
	}
}

/// <summary>fluentfin-mpv: <see cref="ExpandableText"/>'s state, apart from WinUI so it can be tested.</summary>
internal static class ExpandableTextLayout
{
	/// <summary>The lines shown, whether the toggle shows and its text: the toggle is there while the text is cut, and to cut it again.</summary>
	public static (int MaxLines, bool ShowToggle, string ToggleText) For(bool expanded, bool trimmed, int collapsedLines) =>
		expanded
			? (0, true, "Show less")
			: (Math.Max(0, collapsedLines), trimmed, "Show more");
}
