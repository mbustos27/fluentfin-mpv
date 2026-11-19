using FluentFin.Core.Shortcuts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

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

		// Nixie 1.3.1: F1 and ? toggle the sheet: pressed while it is open, they close it as Esc and Close do (the window's own key
		// handler never sees a key inside a dialog); the dialog gives the keyboard back to what had it
		AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
	}

	private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
	{
		var state = InputKeyboardSource.GetKeyStateForCurrentThread;
		bool Down(VirtualKey k) => state(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
		if (AppShortcuts.ClosesHelp((int)e.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift), Down(VirtualKey.Menu), e.KeyStatus.WasKeyDown))
		{
			e.Handled = true;
			Hide();
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
