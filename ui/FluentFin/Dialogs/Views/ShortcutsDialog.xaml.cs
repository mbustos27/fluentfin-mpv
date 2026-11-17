using FluentFin.Core.Shortcuts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Dialogs.Views;

/// <summary>Seaglass: the "?" sheet, a glass dialog listing every shortcut (<see cref="ShortcutCatalog"/>) by section.</summary>
public sealed partial class ShortcutsDialog : ContentDialog
{
	public ShortcutsDialog()
	{
		InitializeComponent();
		foreach (var section in ShortcutCatalog.Sections)
		{
			SectionsPanel.Children.Add(BuildSection(section));
		}
	}

	private UIElement BuildSection(ShortcutSection section)
	{
		var panel = new StackPanel { Spacing = 6 };
		var heading = new TextBlock { Text = section.Title, Style = (Style)Resources["SheetHeading"] };
		AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
		panel.Children.Add(heading);

		foreach (var entry in section.Entries)
		{
			var row = new Grid { ColumnSpacing = 16 };
			row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
			row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

			var keys = new TextBlock { Text = entry.Keys, Style = (Style)Resources["SheetKeys"] };
			var what = new TextBlock { Text = entry.Description, Style = (Style)Resources["SheetText"] };
			Grid.SetColumn(what, 1);
			row.Children.Add(keys);
			row.Children.Add(what);
			// one name per row for Narrator: "Search: Ctrl+F, Ctrl+E, /"
			AutomationProperties.SetName(row, $"{entry.Description}: {entry.Keys}");
			panel.Children.Add(row);
		}

		return panel;
	}
}
