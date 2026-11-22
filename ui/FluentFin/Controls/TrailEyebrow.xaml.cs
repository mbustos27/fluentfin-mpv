using System.ComponentModel;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0: the item page's eyebrow, the library it is in when reached from another section (TrailEyebrow.xaml). Nixie 1.7
/// (upnav): then an episode's series and season, a season's series (TopNavigation.Trail), each a button to that page.
/// </summary>
public sealed partial class TrailEyebrow : UserControl
{
	private readonly TopNavigation _top = App.GetService<TopNavigation>();

	// 1.7 upnav: the crumbs shown (a click opens what it showed, not a trail the top bar moved on to since)
	private ItemTrail _trail = ItemTrail.None;

	public TrailEyebrow()
	{
		InitializeComponent();
		Loaded += (_, _) =>
		{
			_top.PropertyChanged -= OnTopChanged;
			_top.PropertyChanged += OnTopChanged;
			Show();
		};
		Unloaded += (_, _) => _top.PropertyChanged -= OnTopChanged;
	}

	private void OnTopChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(TopNavigation.Eyebrow) or nameof(TopNavigation.Trail))
		{
			DispatcherQueue.TryEnqueue(Show);
		}
	}

	/// <summary>A crumb as its button shows it: its words, and the name Narrator reads.</summary>
	internal readonly record struct CrumbView(string Text, string Name);

	/// <summary>
	/// What the three buttons show (the library, the series, the season), null for one hidden: the library by its title (its
	/// help text says what it is), the series and season by the trail's words and Narrator names ("Go to Season 3").
	/// </summary>
	internal static CrumbView?[] Views(string? library, ItemTrail trail) =>
	[
		library is null ? null : new CrumbView(library, library),
		trail.Series is { } series ? new CrumbView(series.Label, series.Name) : null,
		trail.Season is { } season ? new CrumbView(season.Label, season.Name) : null,
	];

	private void Show()
	{
		_trail = _top.Trail;
		var views = Views(_top.Eyebrow?.Title, _trail);
		Set(LibraryButton, LibraryLabel, views[0]);
		Set(SeriesButton, SeriesLabel, views[1]);
		Set(SeasonButton, SeasonLabel, views[2]);
		Visibility = views.Any(v => v is not null) ? Visibility.Visible : Visibility.Collapsed;
	}

	// a crumb's words and Narrator name, or hidden
	private static void Set(Button button, TextBlock label, CrumbView? view)
	{
		label.Text = view?.Text ?? "";
		AutomationProperties.SetName(button, view?.Name ?? "");
		ToolTipService.SetToolTip(button, label.IsTextTrimmed && view is not null ? view.Value.Text : null);
		button.Visibility = view is null ? Visibility.Collapsed : Visibility.Visible;
	}

	// 1.7 upnav: a name too long for the line (TrailPanel trims it) says itself whole in its tooltip
	private void OnLabelTrimmed(TextBlock sender, IsTextTrimmedChangedEventArgs args)
	{
		var button = ReferenceEquals(sender, SeriesLabel) ? SeriesButton : ReferenceEquals(sender, SeasonLabel) ? SeasonButton : LibraryButton;
		ToolTipService.SetToolTip(button, sender.IsTextTrimmed && sender.Text.Length > 0 ? sender.Text : null);
	}

	private void OnLibraryClick(object sender, RoutedEventArgs e)
	{
		if (_top.Eyebrow is { } library)
		{
			_top.Open(library);
		}
	}

	// 1.7 upnav: the series or season page, in the theme shown, once however often it is pressed (TopNavigation.OpenCrumb)
	private void OnSeriesClick(object sender, RoutedEventArgs e) => Open(_trail.Series);

	private void OnSeasonClick(object sender, RoutedEventArgs e) => Open(_trail.Season);

	private void Open(TrailCrumb? crumb)
	{
		if (crumb is not null)
		{
			_top.OpenCrumb(_trail, crumb);
		}
	}
}
