using CommunityToolkit.WinUI;
using FluentFin.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>
/// Cinematic's shelves are the shared <see cref="ShelfRow"/> (its keys, wheel, arrows, See all and Narrator's list) holding
/// <see cref="CinematicStill"/>s. A still keeps room around it for its 1.1x zoom (the row's scroll viewer clips at its edge),
/// so the row starts that much to the left of the page's margin; this lines the row's header up with the art again, and puts
/// the shelf's count after its title ("Up next  Continue watching and new episodes · 9").
/// </summary>
internal static class CinematicShelf
{
	private const string SummaryTag = "CinematicShelfSummary";

	public static void Fit(ShelfRow row, double inset, string? summary)
	{
		if (Header(row) is not { } header)
		{
			return;
		}

		header.Margin = new Thickness(inset, 0, 12 + inset, 0);
		if (summary is not null)
		{
			SetSummary(row, summary);
		}
	}

	public static void SetSummary(ShelfRow row, string summary)
	{
		if (Header(row) is not { } header)
		{
			return;
		}

		var text = header.Children.OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, SummaryTag));
		if (text is null)
		{
			text = new TextBlock { Tag = SummaryTag, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
			if (Application.Current.Resources.TryGetValue("CinematicShelfCountTextBlockStyle", out var style) && style is Style s)
			{
				text.Style = s;
			}

			Grid.SetColumn(text, 0);
			header.Children.Add(text);
			// after the title (the See all button, or the plain title), wherever its width settles
			foreach (var title in header.Children.OfType<FrameworkElement>().Where(e => e.Name is "SeeAllButton" or "TitleText"))
			{
				title.SizeChanged += (_, _) => Place(header, text);
			}
		}

		text.Text = summary;
		text.Visibility = summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		Place(header, text);
	}

	/// <summary>A shelf whose title the page draws itself (the season tabs above the episodes): the row's header row goes, its list keeps its name.</summary>
	public static void HideHeader(ShelfRow row)
	{
		if (Header(row) is { } header)
		{
			header.Visibility = Visibility.Collapsed;
		}
	}

	private static void Place(Grid header, TextBlock text)
	{
		var title = header.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Name is "SeeAllButton" or "TitleText" && e.Visibility == Visibility.Visible);
		var left = title is null ? 0 : title.ActualWidth + title.Margin.Left + 10;
		text.Margin = new Thickness(left, 4, 0, 0);
	}

	private static Grid? Header(ShelfRow row) =>
		row.FindDescendant<Grid>(g => g.Name == "RowRoot") is { Children: [Grid header, ..] } ? header : null;
}
