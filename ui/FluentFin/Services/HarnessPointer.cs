using FluentFin.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.1, dev builds only: the off-screen harness's mouse (tools\ui-harness\offscreen\then-keys-matrix.ps1). WinUI reads
/// pointer input, which a script cannot post on a desktop nobody sees (posted clicks do nothing there; SendInput would reach
/// the user's desktop), so with <see cref="Variable"/>=1 a dev build takes a registered window message
/// (<see cref="MessageName"/>: wParam's low word the <see cref="Kind"/>, its high word a drag's end x; lParam a point on the
/// screen) and does what a click there does, through the same code: the control WinUI would focus on the press is focused
/// the way WinUI does it (only a button, a slider, a list item or a toggle that allows focus on interaction), a button is
/// clicked (its automation peer), the seek bar is pressed, moved and let go (MpvPlayerControls' own handlers), the video's
/// tap and double tap run, and the player's after-click step follows. A release package never listens.
/// </summary>
public static class HarnessPointer
{
	public const string Variable = "NIXIE_HARNESS_POINTER";

	public const string MessageName = "NixieHarnessPointer";

	/// <summary>What the harness asks for.</summary>
	public enum Kind
	{
		Click = 1,
		DoubleClick = 2,
		/// <summary>Press at the point, move to (the high word's x, the same y), let go.</summary>
		Drag = 3,
		/// <summary>
		/// Nixie 1.4.0: the pointer moves over the player at the point (what the player's own PointerMoved does), and the
		/// overlay's state is logged ("[harness] overlay ..."): the overlay stress of tools\ui-harness\offscreen\then-keys-matrix.ps1.
		/// </summary>
		Move = 4,
	}

	/// <summary>Whether the hook listens: never in a release package, otherwise only with <see cref="Variable"/> exactly 1.</summary>
	public static bool Listens(string? value, bool releasePackage) => !releasePackage && value == "1";

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern uint RegisterWindowMessage(string name);

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct POINT
	{
		public int X, Y;
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool ScreenToClient(nint hwnd, ref POINT point);

	private static WinUIEx.Messaging.WindowMessageMonitor? _monitor;

	public static void Attach(Window window)
	{
		if (_monitor is not null || !Listens(Environment.GetEnvironmentVariable(Variable), Core.AppVersion.IsReleasePackage))
		{
			return;
		}

		var message = RegisterWindowMessage(MessageName);
		var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
		_monitor = new WinUIEx.Messaging.WindowMessageMonitor(window);
		_monitor.WindowMessageReceived += (_, e) =>
		{
			if (e.Message.MessageId != message)
			{
				return;
			}

			e.Handled = true;
			e.Result = 1;
			var w = (long)e.Message.WParam;
			var l = (long)e.Message.LParam;
			var kind = (Kind)(w & 0xFFFF);
			var toX = (short)((w >> 16) & 0xFFFF);
			var point = new POINT { X = (short)(l & 0xFFFF), Y = (short)((l >> 16) & 0xFFFF) };
			var toPoint = new POINT { X = toX, Y = point.Y };
			ScreenToClient(hwnd, ref point);
			ScreenToClient(hwnd, ref toPoint);
			window.DispatcherQueue.TryEnqueue(() => Run(window, kind, point, toPoint));
		};
		Log().LogInformation("[harness] the pointer hook listens ({Variable}=1, a dev build)", Variable);
	}

	private static ILogger Log() => App.GetService<ILogger<MainWindow>>();

	private static async void Run(Window window, Kind kind, POINT client, POINT clientTo)
	{
		try
		{
			if (window.Content is not UIElement content || content.XamlRoot is not { } root)
			{
				return;
			}

			var scale = root.RasterizationScale > 0 ? root.RasterizationScale : 1;
			var at = new Windows.Foundation.Point(client.X / scale, client.Y / scale);
			var hit = VisualTreeHelper.FindElementsInHostCoordinates(at, content).FirstOrDefault();
			Log().LogInformation("[harness] {Kind} at {X},{Y}: {Hit}", kind, (int)at.X, (int)at.Y, Describe(hit));
			if (hit is null)
			{
				return;
			}

			switch (kind)
			{
				case Kind.Click:
					await Click(hit, at, content, second: false);
					break;
				case Kind.DoubleClick:
					await Click(hit, at, content, second: false);
					await Task.Delay(90);
					await Click(hit, at, content, second: true);
					break;
				case Kind.Drag:
					await Drag(hit, at, new Windows.Foundation.Point(clientTo.X / scale, clientTo.Y / scale), content);
					break;
				case Kind.Move when Ancestor<MpvPlayerControls>(hit) is { } player:
					player.HarnessPointerMoved();
					Log().LogInformation("[harness] overlay {State}", player.OverlayState());
					break;
			}
		}
		catch (Exception ex)
		{
			Log().LogWarning(ex, "[harness] the pointer hook failed");
		}
	}

	/// <summary>
	/// One click: the press (WinUI's focus on press, the seek bar's press), the release (a button's click, the seek bar's seek),
	/// the video's tap (the second click of a double click: its double tap), then the player's after-click step.
	/// </summary>
	private static async Task Click(UIElement hit, Windows.Foundation.Point at, UIElement content, bool second)
	{
		var player = Ancestor<MpvPlayerControls>(hit);
		var target = PressTarget(hit);
		FocusOnPress(target);
		var seek = player is not null && target is Slider { Name: MpvPlayerKeys.SeekBarName } slider ? slider : null;
		if (seek is not null)
		{
			player!.SeekBarPressed();
			seek.Value = ValueAt(seek, at, content);
		}

		await Task.Delay(40);
		if (seek is not null)
		{
			player!.SeekBarReleased();
		}
		else if (target is ButtonBase { IsEnabled: true } button
			&& FrameworkElementAutomationPeer.CreatePeerForElement(button)?.GetPattern(PatternInterface.Invoke) is Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke)
		{
			invoke.Invoke();
		}
		// Nixie 1.4.0 (the library stream): a switch (a ToggleButton has no Invoke) turns, as a click turns it
		else if (target is ButtonBase { IsEnabled: true } toggle
			&& FrameworkElementAutomationPeer.CreatePeerForElement(toggle)?.GetPattern(PatternInterface.Toggle) is Microsoft.UI.Xaml.Automation.Provider.IToggleProvider turn)
		{
			turn.Toggle();
		}

		if (player is not null)
		{
			Windows.Foundation.Point Position(UIElement relative) => content.TransformToVisual(relative).TransformPoint(at);
			_ = second ? player.DoubleTapVideo(hit, Position) : player.TapVideo(hit, Position);
			player.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, player.KeepKeyboardAfterPointer);
		}
	}

	private static async Task Drag(UIElement hit, Windows.Foundation.Point from, Windows.Foundation.Point to, UIElement content)
	{
		var player = Ancestor<MpvPlayerControls>(hit);
		if (player is null || PressTarget(hit) is not Slider { Name: MpvPlayerKeys.SeekBarName } seek)
		{
			Log().LogInformation("[harness] the drag did not start on the seek bar");
			return;
		}

		FocusOnPress(seek);
		player.SeekBarPressed();
		for (var i = 0; i <= 6; i++)
		{
			var x = from.X + (to.X - from.X) * i / 6;
			seek.Value = ValueAt(seek, new Windows.Foundation.Point(x, from.Y), content);
			await Task.Delay(40);
		}

		player.SeekBarReleased();
		player.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, player.KeepKeyboardAfterPointer);
	}

	/// <summary>The control a press at <paramref name="hit"/> goes to: the nearest button, slider, list item or toggle above it.</summary>
	private static Control? PressTarget(DependencyObject hit)
	{
		for (var node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is ButtonBase or Slider or SelectorItem or ToggleSwitch)
			{
				return (Control)node;
			}

			if (node is MpvPlayerControls)
			{
				return null;
			}
		}

		return null;
	}

	/// <summary>What WinUI does on a press: the control takes the focus, in the pointer's state, if it allows focus on interaction.</summary>
	private static void FocusOnPress(Control? target)
	{
		if (target is { IsEnabled: true, IsTabStop: true, AllowFocusOnInteraction: true })
		{
			target.Focus(FocusState.Pointer);
		}
	}

	private static double ValueAt(Slider slider, Windows.Foundation.Point at, UIElement content)
	{
		var x = content.TransformToVisual(slider).TransformPoint(at).X;
		var fraction = slider.ActualWidth > 0 ? Math.Clamp(x / slider.ActualWidth, 0, 1) : 0;
		return slider.Minimum + (slider.Maximum - slider.Minimum) * fraction;
	}

	private static T? Ancestor<T>(DependencyObject element) where T : DependencyObject
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is T found)
			{
				return found;
			}
		}

		return null;
	}

	private static string Describe(UIElement? element) => element switch
	{
		null => "nothing",
		FrameworkElement { Name.Length: > 0 } named => $"{element.GetType().Name} {named.Name}",
		_ => element.GetType().Name,
	};
}
