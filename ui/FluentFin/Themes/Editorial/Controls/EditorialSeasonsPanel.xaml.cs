using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>Editorial's Seasons panel (see EditorialSeasonsPanel.xaml).</summary>
public sealed partial class EditorialSeasonsPanel : UserControl
{
	public static readonly DependencyProperty SeasonsProperty =
		DependencyProperty.Register(nameof(Seasons), typeof(IReadOnlyList<EditorialSeasonLine>), typeof(EditorialSeasonsPanel), new PropertyMetadata(null, (d, _) => ((EditorialSeasonsPanel)d).Show()));

	public static readonly DependencyProperty WatchedProperty =
		DependencyProperty.Register(nameof(Watched), typeof(string), typeof(EditorialSeasonsPanel), new PropertyMetadata("", (d, e) => ((EditorialSeasonsPanel)d).WatchedText.Text = e.NewValue as string ?? ""));

	public EditorialSeasonsPanel()
	{
		InitializeComponent();
	}

	public IReadOnlyList<EditorialSeasonLine>? Seasons
	{
		get => GetValue(SeasonsProperty) as IReadOnlyList<EditorialSeasonLine>;
		set => SetValue(SeasonsProperty, value);
	}

	/// <summary>"2 of 24 watched".</summary>
	public string Watched
	{
		get => (string)GetValue(WatchedProperty);
		set => SetValue(WatchedProperty, value);
	}

	/// <summary>A season was picked (a click, Enter or Narrator's Select).</summary>
	public event EventHandler<BaseItemDto>? SeasonPicked;

	/// <summary>The picked line's tab, for the keyboard (the page puts it there after a pick).</summary>
	internal EditorialTabButton? SelectedTab => Lines.Children.OfType<EditorialTabButton>().FirstOrDefault(t => t.IsSelected);

	/// <summary>The lines again: the same seasons keep their tabs (and the keyboard on them), only their state changes.</summary>
	private void Show()
	{
		var lines = Seasons ?? [];
		var tabs = Lines.Children.OfType<EditorialTabButton>().ToList();
		var same = tabs.Count == lines.Count && tabs.Zip(lines).All(x => x.First.Content is EditorialSeasonLine old && old.Season.Id == x.Second.Season.Id);
		if (!same)
		{
			Lines.Children.Clear();
			tabs.Clear();
			foreach (var _ in lines)
			{
				var tab = new EditorialTabButton
				{
					Style = (Style)Resources["SeasonLineStyle"],
					ContentTemplate = (DataTemplate)Resources["SeasonLineTemplate"],
				};
				tab.Click += (sender, _) =>
				{
					if (((EditorialTabButton)sender).Content is EditorialSeasonLine line)
					{
						SeasonPicked?.Invoke(this, line.Season);
					}
				};
				Lines.Children.Add(tab);
				tabs.Add(tab);
			}
		}

		for (var i = 0; i < lines.Count; i++)
		{
			tabs[i].Content = lines[i];
			tabs[i].IsSelected = lines[i].IsCurrent;
			AutomationProperties.SetName(tabs[i], lines[i].AutomationName);
			AutomationProperties.SetPositionInSet(tabs[i], i + 1);
			AutomationProperties.SetSizeOfSet(tabs[i], lines.Count);
		}

		Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	public static double BarWidth(double fraction, double width) => Math.Max(3, width * Math.Clamp(fraction, 0, 1));
}
