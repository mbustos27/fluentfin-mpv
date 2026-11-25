using System.ComponentModel;
using CommunityToolkit.WinUI;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Controls;

/// <summary>
/// 1.1.4 (states): what Narrator is told when a load state appears (no WinUI here, so FluentFin.Tests can call it).
/// </summary>
public static class LoadStateAnnouncement
{
	/// <summary>
	/// A message's title and what to do ("Couldn't load Home. The app couldn't get it from … select Try again."), the loading
	/// text where the view shows the ring, else nothing.
	/// </summary>
	public static string? For(LoadStatus status, string title, string message, bool showsLoading, string loadingText) => status switch
	{
		LoadStatus.Failed or LoadStatus.Empty when !string.IsNullOrWhiteSpace(title) =>
			string.IsNullOrWhiteSpace(message) ? title : $"{title.TrimEnd('.')}. {message}",
		LoadStatus.Loading when showsLoading && !string.IsNullOrWhiteSpace(loadingText) => loadingText,
		_ => null,
	};
}

/// <summary>fluentfin-mpv: shows a <see cref="LoadState"/>: a message with Retry, a warning bar, or (optionally) a ring.</summary>
public sealed partial class LoadStateView : UserControl
{
	/// <summary>The id of the Narrator notification a message or the loading text is announced with (one at a time).</summary>
	public const string AnnouncementId = "LoadState";

	[GeneratedDependencyProperty]
	public partial LoadState? State { get; set; }

	/// <summary>Shows a progress ring while loading (pages that have no skeleton of their own).</summary>
	[GeneratedDependencyProperty]
	public partial bool ShowsLoading { get; set; }

	[GeneratedDependencyProperty(DefaultValue = "Loading…")]
	public partial string LoadingText { get; set; }

	public LoadStateView()
	{
		InitializeComponent();
		Loaded += OnLoaded;
		Unloaded += (_, _) =>
		{
			Watch(null); // Nixie 1.7.5 (review Major 3)
			FluentFin.Services.AmbientBackdrop.ReleaseFullScene(this);
		};
	}

	/// <summary>
	/// 1.2.0 (the glass stream's hook, wired at the merge): while the view shows an empty page or a failure in place of the
	/// content, the window's backdrop is the full dusk scene (the sign-in's), not the hill strip; it is given back when the
	/// message goes or the view unloads. The message already sits on a glass panel, so its text is not on the sky bands.
	/// </summary>
	internal static bool WantsFullScene(bool loaded, LoadStatus? status) => loaded && status is LoadStatus.Empty or LoadStatus.Failed;

	private void UpdateScene()
	{
		if (WantsFullScene(IsLoaded, State?.Status))
		{
			FluentFin.Services.AmbientBackdrop.RequestFullScene(this);
		}
		else
		{
			FluentFin.Services.AmbientBackdrop.ReleaseFullScene(this);
		}
	}

	private LoadState? _watched;

	partial void OnStateChanged(LoadState? newValue)
	{
		Watch(IsLoaded ? newValue : null);
		UpdateScene();
	}

	/// <summary>
	/// Nixie 1.7.5 (review Major 3): the state is heard only while the view is in the window. A page's state belongs to its view
	/// model, which Back keeps and hands to a new page: heard from the first page on, it kept that page alive, one more for every
	/// Back. On Loaded the view hears it again and shows it as it is.
	/// </summary>
	private void Watch(LoadState? state)
	{
		if (_watched is not null)
		{
			_watched.PropertyChanged -= OnStatePropertyChanged;
		}

		_watched = state;
		if (state is not null)
		{
			state.PropertyChanged += OnStatePropertyChanged;
		}
	}

	/// <summary>
	/// 1.1.4 (states): one layer of glass. Inside a glass section panel already (a Settings group), the message is drawn
	/// flat; elsewhere it is a glass panel of its own (the sign-in card's look).
	/// </summary>
	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		Watch(State); // Nixie 1.7.5 (review Major 3)
		UpdateScene();
		if (IsInsideGlassSurface(this))
		{
			MessageSurface.Style = (Style)Resources["FlatSurfaceStyle"];
			MessageSurface.Padding = new Thickness(0, 8, 0, 8);
		}
	}

	/// <summary>Whether an ancestor of <paramref name="element"/> is a glass section panel (GlassSurfaceStyle's template).</summary>
	internal static bool IsInsideGlassSurface(DependencyObject element)
	{
		var glass = Application.Current.Resources.TryGetValue("GlassSurfaceTemplate", out var template) ? template : null;
		for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
		{
			if (parent is ContentControl { Template: { } t } && ReferenceEquals(t, glass))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Narrator hears a failure or an empty page when it appears (WCAG 4.1.3): its title and what to do, as one
	/// notification, without moving focus, and "Loading…" where this view shows the ring. When the focus was on
	/// something the new state took away (nothing has it now), it goes to Try again, so the keyboard is not lost.
	/// </summary>
	private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(LoadState.Status) || State is not { } state)
		{
			return;
		}

		DispatcherQueue.TryEnqueue(UpdateScene);

		DispatcherQueue.TryEnqueue(() =>
		{
			if (State is not { } now || !ReferenceEquals(now, state))
			{
				return;
			}

			if (LoadStateAnnouncement.For(now.Status, now.Title, now.Message, ShowsLoading, LoadingText) is { } text)
			{
				var target = now.HasMessage ? (UIElement)TitleText : LoadingTextBlock;
				if ((FrameworkElementAutomationPeer.FromElement(target) ?? FrameworkElementAutomationPeer.CreatePeerForElement(target)) is { } peer)
				{
					peer.RaiseNotificationEvent(AutomationNotificationKind.Other,
						now.HasMessage ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
						text, AnnouncementId);
				}
			}

			if (now.HasMessage && IsLoaded && XamlRoot is { } root && FocusIsLost(root))
			{
				RetryButton.Focus(FocusState.Programmatic);
			}
		});
	}

	private static bool FocusIsLost(XamlRoot root) =>
		FocusManager.GetFocusedElement(root) is not UIElement focused || focused is FrameworkElement { IsLoaded: false } || focused.Visibility != Visibility.Visible;

	public string GlyphOf(LoadStatus status) => status switch
	{
		LoadStatus.Failed => char.ConvertFromUtf32(0xE783), // Segoe Fluent Icons: Error
		_ => char.ConvertFromUtf32(0xE8B7), // Folder: nothing here
	};

	public Visibility IsRingShown(bool isLoading, bool showsLoading) => isLoading && showsLoading ? Visibility.Visible : Visibility.Collapsed;
}
