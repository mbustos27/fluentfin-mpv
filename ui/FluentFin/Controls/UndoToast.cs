using FluentFin.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>
/// 1.2.0 (Nixie button review, I4 and I24): after a whole series or season was marked (un)watched, a notice at the bottom of
/// the window for <see cref="ShowFor"/>: what happened and Undo (also Ctrl+Z while it shows), which puts each episode back as
/// it was (<see cref="ItemUserData.UndoOffered"/>). Narrator reads it when it appears; it stays while the pointer or the
/// keyboard is on it; Close takes it away. One at a time: a new one replaces the last. The cards and the item pages' heroes,
/// where the toggles are, start it listening (<see cref="Listen"/>).
/// </summary>
internal static class UndoToast
{
	public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(10);

	/// <summary>The notice's popup's Tag: it is not a menu (Services.ArrivalFocus.CountsAsMenu), a page's arrival does not wait it out.</summary>
	public const string NoticeTag = "NixieUndoNotice";

	private static bool s_listening;
	private static Popup? s_popup;
	private static DispatcherTimer? s_timer;
	private static KeyboardAccelerator? s_accelerator;
	private static UIElement? s_acceleratorHost;

	public static void Listen()
	{
		if (s_listening)
		{
			return;
		}

		s_listening = true;
		ItemUserData.UndoOffered += (_, offer) => App.MainWindow.DispatcherQueue.TryEnqueue(() => Show(offer.Message, offer));
	}

	/// <summary>Whether the notice should still show: its time is not up, or the pointer or the keyboard is on it.</summary>
	internal static bool StaysOpen(TimeSpan shown, bool pointerOver, bool focusWithin) => shown < ShowFor || pointerOver || focusWithin;

	private static void Show(string message, UndoOffer? offer)
	{
		Close();
		if (App.MainWindow.Content is not FrameworkElement root || root.XamlRoot is null)
		{
			return;
		}

		var text = new TextBlock { Text = message, MaxWidth = 460, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
		var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
		panel.Children.Add(text);
		if (offer is not null)
		{
			var undo = new Button { Content = "Undo", MinHeight = 32, VerticalAlignment = VerticalAlignment.Center };
			ToolTipService.SetToolTip(undo, "Undo (Ctrl+Z)");
			AutomationProperties.SetAcceleratorKey(undo, "Control+Z");
			undo.Click += (_, _) => Undo(offer);
			panel.Children.Add(undo);
		}

		var close = new Button
		{
			Content = new FontIcon { Glyph = "", FontSize = 12 },
			Width = 32,
			Height = 32,
			Padding = new Thickness(0),
			VerticalAlignment = VerticalAlignment.Center,
			Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
			BorderThickness = new Thickness(0),
		};
		AutomationProperties.SetName(close, "Close the notice");
		ToolTipService.SetToolTip(close, "Close");
		close.Click += (_, _) => Close();
		panel.Children.Add(close);

		var card = new Border
		{
			Child = panel,
			Padding = new Thickness(16, 8, 8, 8),
			CornerRadius = new CornerRadius(8),
			BorderThickness = new Thickness(1),
			Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
			BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
			RequestedTheme = root.ActualTheme,
		};
		AutomationProperties.SetName(card, message);
		AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Polite);

		var popup = new Popup { Child = card, XamlRoot = root.XamlRoot, Tag = NoticeTag };
		card.SizeChanged += (_, _) =>
		{
			popup.HorizontalOffset = Math.Max(0, (root.ActualWidth - card.ActualWidth) / 2);
			popup.VerticalOffset = Math.Max(0, root.ActualHeight - card.ActualHeight - 32);
		};
		popup.IsOpen = true;
		s_popup = popup;

		// Narrator hears it without the focus moving to it
		var peer = FrameworkElementAutomationPeer.FromElement(text) ?? FrameworkElementAutomationPeer.CreatePeerForElement(text);
		peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
			offer is null ? message : message + " Undo with Control+Z.", "NixieUndoNotice");

		if (offer is not null)
		{
			s_accelerator = new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control };
			s_accelerator.Invoked += (_, e) =>
			{
				e.Handled = true;
				Undo(offer);
			};
			s_acceleratorHost = root;
			root.KeyboardAccelerators.Add(s_accelerator);
		}

		var pointerOver = false;
		card.PointerEntered += (_, _) => pointerOver = true;
		card.PointerExited += (_, _) => pointerOver = false;
		var opened = DateTime.UtcNow;
		s_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
		s_timer.Tick += (_, _) =>
		{
			var focusWithin = FocusManager.GetFocusedElement(root.XamlRoot) is DependencyObject focused && IsWithin(focused, card);
			if (!StaysOpen(DateTime.UtcNow - opened, pointerOver, focusWithin))
			{
				Close();
			}
		};
		s_timer.Start();
	}

	private static async void Undo(UndoOffer offer)
	{
		Close();
		var all = await offer.Undo();
		Show(all ? "Undone." : "Some episodes could not be put back. Check your connection, then try again.", null);
	}

	private static bool IsWithin(DependencyObject element, DependencyObject container)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, container))
			{
				return true;
			}
		}

		return false;
	}

	private static void Close()
	{
		s_timer?.Stop();
		s_timer = null;
		if (s_accelerator is not null && s_acceleratorHost is not null)
		{
			s_acceleratorHost.KeyboardAccelerators.Remove(s_accelerator);
		}

		s_accelerator = null;
		s_acceleratorHost = null;
		if (s_popup is not null)
		{
			s_popup.IsOpen = false;
			s_popup = null;
		}
	}
}
