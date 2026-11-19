using System.ComponentModel;
using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: the theme's look on the shared row and card, without changing them. The shared <see cref="ShelfRow"/> and
/// <see cref="BaseItemDtoCard"/> keep everything they do (one Tab stop per row, arrows and the wheel, Back's remembered
/// card, the card's Narrator name, Invoke and menu); the theme dresses their named parts:
/// <list type="bullet">
/// <item>a row's title becomes a marquee board (<see cref="MarqueeSign"/>: still the level-2 heading, still "See all
/// &lt;row&gt;" when the row has one), with an optional line beside it ("5 in progress");</item>
/// <item>a card in progress gets its time left in nixie tubes on the artwork ("31 MIN LEFT"), and an episode's code under
/// its title in tubes ("S3 E04 The Lamp Room").</item>
/// </list>
/// The parts are found by the names the shared XAML gives them (<see cref="RowParts"/>, <see cref="CardParts"/>;
/// DriveInThemeTests fails if they are renamed); a part that is not found is left as it is.
/// </summary>
public static class DriveInRows
{
	/// <summary>The shared row's parts this dresses (ShelfRow.xaml).</summary>
	public static readonly string[] RowParts = ["TitleText", "SeeAllButton"];

	/// <summary>The shared card's parts this dresses (BaseItemDtoCard.xaml).</summary>
	public static readonly string[] CardParts = ["ContentOverlay", "TitleArea"];

	// ---- the row's marquee ------------------------------------------------------------------------------------------------

	public static readonly DependencyProperty MarqueeProperty =
		DependencyProperty.RegisterAttached("Marquee", typeof(bool), typeof(DriveInRows), new PropertyMetadata(false, OnMarqueeChanged));

	public static readonly DependencyProperty SubtitleProperty =
		DependencyProperty.RegisterAttached("Subtitle", typeof(string), typeof(DriveInRows), new PropertyMetadata("", (d, _) =>
		{
			if (Dressed(d) is { } dress)
			{
				dress.Subtitle.Text = GetSubtitle(d);
			}
		}));

	private static readonly DependencyProperty DressProperty =
		DependencyProperty.RegisterAttached("Dress", typeof(RowDress), typeof(DriveInRows), new PropertyMetadata(null));

	public static bool GetMarquee(DependencyObject d) => (bool)d.GetValue(MarqueeProperty);

	public static void SetMarquee(DependencyObject d, bool value) => d.SetValue(MarqueeProperty, value);

	public static string GetSubtitle(DependencyObject d) => (string)d.GetValue(SubtitleProperty);

	public static void SetSubtitle(DependencyObject d, string value) => d.SetValue(SubtitleProperty, value);

	private sealed record RowDress(MarqueeSign Sign, TextBlock Subtitle);

	private static RowDress? Dressed(DependencyObject d) => d.GetValue(DressProperty) as RowDress;

	private static void OnMarqueeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not ShelfRow row || e.NewValue is not true)
		{
			return;
		}

		row.Loaded += (_, _) => DressRow(row);
		row.RegisterPropertyChangedCallback(ShelfRow.HeaderProperty, (_, _) =>
		{
			if (Dressed(row) is { } dress)
			{
				dress.Sign.Text = row.Header;
			}
		});
		row.RegisterPropertyChangedCallback(ShelfRow.HasSeeAllProperty, (_, _) => Place(row));
	}

	private static void DressRow(ShelfRow row)
	{
		if (Dressed(row) is not null)
		{
			Place(row);
			return;
		}

		var sign = new MarqueeSign { Text = row.Header, VerticalAlignment = VerticalAlignment.Center };
		var subtitle = new TextBlock
		{
			Text = GetSubtitle(row),
			VerticalAlignment = VerticalAlignment.Center,
			FontSize = 14,
			Foreground = (Brush)Application.Current.Resources["GlassTextSecondaryBrush"],
		};
		row.SetValue(DressProperty, new RowDress(sign, subtitle));
		Place(row);
	}

	/// <summary>
	/// The marquee goes in the title's place. 1.4.0 (the library UX review, X13): the row's See all stays the shared Link at
	/// the header's right, as in every theme (it was the marquee itself, a 282 or 415 x 56 tile).
	/// </summary>
	private static void Place(ShelfRow row)
	{
		if (Dressed(row) is not { } dress || Find<TextBlock>(row, "TitleText") is not { Parent: Grid header } title)
		{
			return;
		}

		if (dress.Sign.Parent is StackPanel { Parent: Grid } placed && ReferenceEquals(placed.Parent, header))
		{
			title.Visibility = Visibility.Collapsed;
			return;
		}

		Detach(dress.Sign);
		Detach(dress.Subtitle);
		var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, VerticalAlignment = VerticalAlignment.Center };
		line.Children.Add(dress.Sign);
		line.Children.Add(dress.Subtitle);
		title.Visibility = Visibility.Collapsed;
		Grid.SetColumn(line, 0);
		header.Children.Add(line);
	}

	private static void Detach(FrameworkElement element)
	{
		switch (element.Parent)
		{
			case Panel panel:
				panel.Children.Remove(element);
				break;
			case ContentControl control when ReferenceEquals(control.Content, element):
				control.Content = null;
				break;
		}
	}

	// ---- the card's tubes ---------------------------------------------------------------------------------------------

	public static readonly DependencyProperty NixieProperty =
		DependencyProperty.RegisterAttached("Nixie", typeof(bool), typeof(DriveInRows), new PropertyMetadata(false, OnNixieChanged));

	public static bool GetNixie(DependencyObject d) => (bool)d.GetValue(NixieProperty);

	public static void SetNixie(DependencyObject d, bool value) => d.SetValue(NixieProperty, value);

	private sealed class CardDress
	{
		public Border? Chip;
		public NixieReadout? ChipTubes;
		public TextBlock? ChipUnit;
		public StackPanel? Code;
		public NixieReadout? CodeTubes;
		public TextBlock? CodeName;
		public TextBlock? Subtitle;
		public BaseItemViewModel? Watched;
		public PropertyChangedEventHandler? Handler;
	}

	private static readonly DependencyProperty CardDressProperty =
		DependencyProperty.RegisterAttached("CardDress", typeof(CardDress), typeof(DriveInRows), new PropertyMetadata(null));

	private static void OnNixieChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not BaseItemDtoCard card || e.NewValue is not true)
		{
			return;
		}

		card.Loaded += (_, _) => DressCard(card);
		card.Unloaded += (_, _) => Watch(card, null);
		card.RegisterPropertyChangedCallback(BaseItemDtoCard.ModelProperty, (_, _) => DressCard(card));
	}

	private static void DressCard(BaseItemDtoCard card)
	{
		if (!card.IsLoaded)
		{
			return;
		}


		if (card.GetValue(CardDressProperty) is not CardDress dress)
		{
			dress = new CardDress();
			card.SetValue(CardDressProperty, dress);
			// the time left on a still (16:9) card; a poster keeps its progress bar alone (the chip would cover its title art)
			if (card.Width > card.Height && Find<Grid>(card, "ContentOverlay") is { } canvas)
			{
				dress.ChipTubes = new NixieReadout { DigitHeight = 22 };
				dress.ChipUnit = new TextBlock
				{
					VerticalAlignment = VerticalAlignment.Center,
					FontSize = 12,
					FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
					CharacterSpacing = 80,
					Foreground = new SolidColorBrush(DriveInScene.C("#F8EFF0")),
				};
				AutomationProperties.SetAccessibilityView(dress.ChipUnit, AccessibilityView.Raw);
				var chipLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
				chipLine.Children.Add(dress.ChipTubes);
				chipLine.Children.Add(dress.ChipUnit);
				dress.Chip = new Border
				{
					Child = chipLine,
					Padding = new Thickness(4, 4, 7, 4),
					CornerRadius = new CornerRadius(6),
					Background = new SolidColorBrush(DriveInScene.C("#C70C0206")),
					HorizontalAlignment = HorizontalAlignment.Right,
					VerticalAlignment = VerticalAlignment.Bottom,
					Margin = new Thickness(0, 0, 10, 16),
					IsHitTestVisible = false,
				};
				// Narrator hears the card by its own name; the chip is what the eye reads
				AutomationProperties.SetAccessibilityView(dress.Chip, AccessibilityView.Raw);
				// 1.4.0: under the card's action strip (the overlay's first child), so the strip covers it while it shows
				canvas.Children.Insert(0, dress.Chip);
			}

			if (Find<StackPanel>(card, "TitleArea") is { Children.Count: >= 2 } area && area.Children[1] is TextBlock subtitle)
			{
				dress.Subtitle = subtitle;
				dress.CodeTubes = new NixieReadout { DigitHeight = 22 };
				dress.CodeName = new TextBlock
				{
					VerticalAlignment = VerticalAlignment.Center,
					TextTrimming = TextTrimming.CharacterEllipsis,
					TextWrapping = TextWrapping.NoWrap,
					Style = (Style)Application.Current.Resources["GlassMetaTextBlockStyle"],
				};
				dress.Code = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
				dress.Code.Children.Add(dress.CodeTubes);
				dress.Code.Children.Add(dress.CodeName);
				area.Children.Add(dress.Code);
			}
		}

		Watch(card, card.Model);
		Apply(card, dress);
	}

	private static void Watch(BaseItemDtoCard card, BaseItemViewModel? model)
	{
		if (card.GetValue(CardDressProperty) is not CardDress dress || ReferenceEquals(dress.Watched, model))
		{
			return;
		}

		if (dress.Watched is { } old && dress.Handler is { } previous)
		{
			old.PropertyChanged -= previous;
			if (old.UserData is { } data)
			{
				data.PropertyChanged -= previous;
			}
		}

		dress.Watched = model;
		dress.Handler = null;
		if (model is not null)
		{
			dress.Handler = (_, _) => card.DispatcherQueue.TryEnqueue(() => Apply(card, dress));
			model.PropertyChanged += dress.Handler;
			if (model.UserData is { } data)
			{
				data.PropertyChanged += dress.Handler;
			}
		}
	}

	private static void Apply(BaseItemDtoCard card, CardDress dress)
	{
		var dto = card.Model?.Dto;
		if (dress.Chip is not null && dress.ChipTubes is not null && dress.ChipUnit is not null)
		{
			var left = DriveInText.TimeLeft(DriveInText.Left(dto));
			dress.Chip.Visibility = left is null ? Visibility.Collapsed : Visibility.Visible;
			if (left is { } l)
			{
				dress.ChipTubes.Text = l.Digits;
				dress.ChipTubes.Spoken = l.Spoken;
				dress.ChipUnit.Text = l.Unit;
			}
		}

		if (dress.Code is not null && dress.CodeTubes is not null && dress.CodeName is not null && dress.Subtitle is not null)
		{
			var code = DriveInText.NixieCode(dto);
			var episode = code.Length > 0 && dto?.Type == Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode;
			dress.Code.Visibility = episode ? Visibility.Visible : Visibility.Collapsed;
			// the shared subtitle stays for everything else (a movie's year); for an episode the tubes and the name replace it
			dress.Subtitle.Opacity = episode ? 0 : 1;
			dress.Subtitle.Height = episode ? 0 : double.NaN;
			if (episode)
			{
				dress.CodeTubes.Text = code;
				dress.CodeTubes.Spoken = Core.ItemLabels.EpisodeCode(dto);
				dress.CodeName.Text = DriveInHome.EntryDetail(dto);
			}
		}
	}

	// ---- helpers --------------------------------------------------------------------------------------------------------

	/// <summary>The first element of type <typeparamref name="T"/> named <paramref name="name"/> under <paramref name="root"/>.</summary>
	internal static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
	{
		var count = VisualTreeHelper.GetChildrenCount(root);
		for (var i = 0; i < count; i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is T match && match.Name == name)
			{
				return match;
			}

			if (Find<T>(child, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}
}
