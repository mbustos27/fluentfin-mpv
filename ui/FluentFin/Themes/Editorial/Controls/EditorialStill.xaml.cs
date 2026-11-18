using FluentFin.Converters;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>Editorial's episode still (see EditorialStill.xaml).</summary>
public sealed partial class EditorialStill : UserControl
{
	public static readonly DependencyProperty EpisodeProperty =
		DependencyProperty.Register(nameof(Episode), typeof(BaseItemDto), typeof(EditorialStill), new PropertyMetadata(null, (d, _) => ((EditorialStill)d).Apply()));

	/// <summary>The still's height over its width (the concept's 418 x 190).</summary>
	public const double Shape = 190.0 / 418.0;

	private bool _pointerOver;
	private bool _keyboardFocus;
	private int _decodedWidth;

	public EditorialStill()
	{
		InitializeComponent();
		Loaded += (_, _) => ItemUserData.Changed += OnUserDataChanged;
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;
		AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), false);
		GotFocus += (_, e) =>
		{
			_keyboardFocus = ReferenceEquals(e.OriginalSource, this) && FocusState is FocusState.Keyboard or FocusState.Programmatic;
			Update();
		};
		LostFocus += (_, _) => { _keyboardFocus = false; Update(); };
		RootGrid.PointerEntered += (_, _) => { _pointerOver = true; Update(); };
		RootGrid.PointerExited += (_, _) => { _pointerOver = false; Update(); };
		RootGrid.PointerCanceled += (_, _) => { _pointerOver = false; Update(); };
		RootGrid.Tapped += (_, e) =>
		{
			e.Handled = true;
			// the title opens the episode's page (as the shared episode row's does); anywhere else plays it
			if (IsIn(e.OriginalSource as DependencyObject, TitleText) && Episode is { } dto)
			{
				App.Commands.DisplayDto(dto);
				return;
			}

			Play();
		};
		ContextRequested += OnContextRequested;
		SizeChanged += (_, e) =>
		{
			var height = Math.Round(e.NewSize.Width * Shape);
			if (Math.Abs(Art.Height - height) >= 1)
			{
				Art.Height = FocusGlow.Height = height;
				FocusRing.Height = height + 8;
				FocusGlow.VerticalAlignment = VerticalAlignment.Top;
				ApplyImage();
			}
		};
	}

	public BaseItemDto? Episode
	{
		get => GetValue(EpisodeProperty) as BaseItemDto;
		set => SetValue(EpisodeProperty, value);
	}

	private void Apply()
	{
		var dto = Episode;
		NumberText.Text = EditorialCards.StillNumber(dto);
		TitleText.Text = dto?.Name?.Trim() ?? "";
		SynopsisText.Text = dto?.Overview?.Trim() ?? "";
		SynopsisText.Visibility = SynopsisText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		_decodedWidth = 0;
		ApplyImage();
		ApplyUserData();
	}

	private void ApplyImage()
	{
		var width = (int)Math.Round(Math.Max(ActualWidth, 300) * 1.5);
		if (Episode is not { } dto || Math.Abs(width - _decodedWidth) < 64)
		{
			return;
		}

		_decodedWidth = width;
		Picture.Source = ItemArtworkConverters.Thumb(dto, width);
	}

	private void ApplyUserData()
	{
		var dto = Episode;
		MetaText.Text = EditorialCards.StillMeta(dto);
		WatchedMark.Visibility = EditorialCards.Watched(dto) ? Visibility.Visible : Visibility.Collapsed;
		var progress = EditorialCards.Progress(dto);
		ProgressTrack.Visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
		ProgressTrack.UpdateLayout();
		ProgressFill.Width = Math.Max(4, ProgressTrack.ActualWidth * progress / 100);
		ProgressTrack.SizeChanged -= OnTrackSized;
		ProgressTrack.SizeChanged += OnTrackSized;
		if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, "", ItemLabels.EpisodeRowName(dto));
		}
	}

	private void OnTrackSized(object sender, SizeChangedEventArgs e) =>
		ProgressFill.Width = Math.Max(4, e.NewSize.Width * EditorialCards.Progress(Episode) / 100);

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (Episode is not { Id: { } id } dto || id != e.ItemId)
		{
			return;
		}

		dto.UserData = e.UserData;
		ApplyUserData();
	}

	/// <summary>Enter, a click and Narrator's Invoke play the episode (as the shared episode row does).</summary>
	internal void Play()
	{
		if (Episode is { } dto)
		{
			App.Commands.PlayDtoCommand.Execute(dto);
		}
	}

	private void OnKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA && ReferenceEquals(e.OriginalSource, this))
		{
			if (!e.KeyStatus.WasKeyDown)
			{
				Play();
			}

			e.Handled = true;
		}
	}

	private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
	{
		if (Episode is not { } dto || new JellyfinFlyoutConverter { IncludePlay = true }.Convert(dto, typeof(object), null!, "") is not FlyoutBase flyout)
		{
			return;
		}

		// the episode's own page, first in its menu (the keyboard's way there; a click on the title is the pointer's)
		EditorialMenus.WithPageEntry(flyout, "Episode details", dto);

		if (args.TryGetPosition(this, out var point))
		{
			flyout.ShowAt(this, new FlyoutShowOptions { Position = point });
		}
		else
		{
			flyout.ShowAt(this);
		}

		args.Handled = true;
	}

	private void Update()
	{
		FocusRing.Visibility = FocusGlow.Visibility = _keyboardFocus ? Visibility.Visible : Visibility.Collapsed;
		PlayGlyph.Opacity = _keyboardFocus || _pointerOver ? 1 : 0;
	}

	private static bool IsIn(DependencyObject? element, DependencyObject ancestor)
	{
		for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, ancestor))
			{
				return true;
			}
		}

		return false;
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new EditorialStillAutomationPeer(this);
}

/// <summary>Narrator reads a still as the shared episode row ("S1 E3 Sawdust, 15% watched", "3 of 8"); Invoke plays it.</summary>
internal sealed partial class EditorialStillAutomationPeer(EditorialStill owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

	protected override string GetClassNameCore() => nameof(EditorialStill);

	protected override string GetNameCore()
	{
		var name = AutomationProperties.GetName(owner);
		return string.IsNullOrEmpty(name) ? ItemLabels.EpisodeRowName(owner.Episode) : name;
	}

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override IList<AutomationPeer> GetChildrenCore() => [];

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

	public void Invoke() => owner.Play();
}
