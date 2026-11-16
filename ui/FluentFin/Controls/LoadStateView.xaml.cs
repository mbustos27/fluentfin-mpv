using System.ComponentModel;
using CommunityToolkit.WinUI;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>fluentfin-mpv: shows a <see cref="LoadState"/>: a message with Retry, a warning bar, or (optionally) a ring.</summary>
public sealed partial class LoadStateView : UserControl
{
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
	}

	private LoadState? _watched;

	partial void OnStateChanged(LoadState? newValue)
	{
		if (_watched is not null)
		{
			_watched.PropertyChanged -= OnStatePropertyChanged;
		}

		_watched = newValue;
		if (newValue is not null)
		{
			newValue.PropertyChanged += OnStatePropertyChanged;
		}
	}

	/// <summary>Narrator hears a failure or an empty page when it appears (WCAG 4.1.3), without moving focus.</summary>
	private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(LoadState.Status) || State is not { HasMessage: true })
		{
			return;
		}

		DispatcherQueue.TryEnqueue(() =>
		{
			if (FrameworkElementAutomationPeer.FromElement(TitleText) is { } peer)
			{
				peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
			}
		});
	}

	public string GlyphOf(LoadStatus status) => status switch
	{
		LoadStatus.Failed => char.ConvertFromUtf32(0xE783), // Segoe Fluent Icons: Error
		_ => char.ConvertFromUtf32(0xE8B7), // Folder: nothing here
	};

	public Visibility IsRingShown(bool isLoading, bool showsLoading) => isLoading && showsLoading ? Visibility.Visible : Visibility.Collapsed;
}
