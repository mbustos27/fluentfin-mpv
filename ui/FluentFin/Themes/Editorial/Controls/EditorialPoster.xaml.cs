using System.Numerics;
using FluentFin.Controls;
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
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>
/// Something that shows an item's details in place (a shelf, the Library's grid): a poster inside it opens there instead of
/// going to the item's page.
/// </summary>
public interface IEditorialDetailsHost
{
	/// <summary>Opens <paramref name="item"/>'s details under its row, or closes them when they are open already.</summary>
	void ToggleDetails(BaseItemViewModel item, EditorialPoster poster);

	/// <summary>Whether <paramref name="item"/>'s details are the ones open.</summary>
	bool IsOpen(BaseItemViewModel item);
}

/// <summary>Editorial's poster card (see EditorialPoster.xaml).</summary>
public sealed partial class EditorialPoster : UserControl
{
	public static readonly DependencyProperty ItemProperty =
		DependencyProperty.Register(nameof(Item), typeof(BaseItemViewModel), typeof(EditorialPoster), new PropertyMetadata(null, (d, _) => ((EditorialPoster)d).Apply()));

	public static readonly DependencyProperty PosterWidthProperty =
		DependencyProperty.Register(nameof(PosterWidth), typeof(double), typeof(EditorialPoster), new PropertyMetadata(144.0, (d, _) => ((EditorialPoster)d).Resize()));

	public static readonly DependencyProperty IsOpenProperty =
		DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(EditorialPoster), new PropertyMetadata(false, (d, _) => ((EditorialPoster)d).UpdateRing()));

	private bool _pointerOver;
	private bool _keyboardFocus;
	private int _decodedHeight;

	public EditorialPoster()
	{
		InitializeComponent();
		Loaded += (_, _) =>
		{
			ItemUserData.Changed += OnUserDataChanged;
			IsOpen = Item is { } item && FindHost() is { } host && host.IsOpen(item);
		};
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;
		AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), false);
		GotFocus += OnGotFocus;
		LostFocus += (_, _) =>
		{
			_keyboardFocus = false;
			UpdateRing();
		};
		RootGrid.Tapped += OnTapped;
		RootGrid.PointerEntered += (_, _) => { _pointerOver = true; Lift(); };
		RootGrid.PointerExited += (_, _) => { _pointerOver = false; Lift(); };
		RootGrid.PointerCanceled += (_, _) => { _pointerOver = false; Lift(); };
		ContextRequested += OnContextRequested;
		Resize();
	}

	public BaseItemViewModel? Item
	{
		get => GetValue(ItemProperty) as BaseItemViewModel;
		set => SetValue(ItemProperty, value);
	}

	/// <summary>The artwork's width; its height is 1.5 times that (2:3).</summary>
	public double PosterWidth
	{
		get => (double)GetValue(PosterWidthProperty);
		set => SetValue(PosterWidthProperty, value);
	}

	/// <summary>Its details are open under the row: the ring stays and the notch points at them.</summary>
	public bool IsOpen
	{
		get => (bool)GetValue(IsOpenProperty);
		set => SetValue(IsOpenProperty, value);
	}

	private double ArtHeight => Math.Round(PosterWidth * 1.5);

	private void Resize()
	{
		Width = PosterWidth;
		Art.Height = FocusGlow.Height = ArtHeight;
		FocusRing.Height = ArtHeight + 8;
		FocusRing.VerticalAlignment = VerticalAlignment.Top;
		FocusGlow.VerticalAlignment = VerticalAlignment.Top;
		ApplyImage();
	}

	private void Apply()
	{
		var dto = Item?.Dto;
		TitleText.Text = EditorialCards.Title(dto);
		MetaText.Text = EditorialCards.PosterMeta(dto);
		MetaText.Visibility = MetaText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		ApplyUserData();
		_decodedHeight = 0;
		ApplyImage();
		IsOpen = Item is { } item && IsLoaded && FindHost() is { } host && host.IsOpen(item);
	}

	private void ApplyImage()
	{
		var dto = Item?.Dto;
		// decoded at the size it is drawn (at 150 % display scale), once per item and size
		var height = (int)Math.Round(ArtHeight * 1.5);
		if (dto is null || height == _decodedHeight)
		{
			return;
		}

		_decodedHeight = height;
		Picture.BlurHashImageSource = BaseItemDtoConverters.GetBlurHash(dto, ImageType.Primary, height)!;
		Picture.ImageSource = BaseItemDtoConverters.GetImage(dto, ImageType.Primary, height)!;
	}

	private void ApplyUserData()
	{
		var dto = Item?.Dto;
		WatchedMark.Visibility = EditorialCards.Watched(dto) ? Visibility.Visible : Visibility.Collapsed;
		var progress = EditorialCards.Progress(dto);
		ProgressTrack.Visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
		ProgressFill.Width = Math.Max(4, (PosterWidth - 20) * progress / 100);
		if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, "", ItemLabels.CardName(Item));
		}
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (Item is not { Dto.Id: { } id } item || id != e.ItemId)
		{
			return;
		}

		item.Dto.UserData = e.UserData;
		item.UserData = UserItemDataViewModel.FromDto(e.UserData);
		ApplyUserData();
	}

	/// <summary>The nearest shelf or grid that shows details in place, if any.</summary>
	private IEditorialDetailsHost? FindHost()
	{
		for (var node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is IEditorialDetailsHost host)
			{
				return host;
			}
		}

		return null;
	}

	/// <summary>What a click, Enter or Narrator's Invoke does: the details in place, else (no host) the item's page.</summary>
	internal void Open()
	{
		if (Item is not { } item)
		{
			return;
		}

		if (FindHost() is { } host)
		{
			host.ToggleDetails(item, this);
		}
		else
		{
			App.Commands.DisplayDto(item.Dto);
		}
	}

	private void OnTapped(object sender, TappedRoutedEventArgs e)
	{
		e.Handled = true;
		Focus(FocusState.Pointer);
		Open();
	}

	private void OnKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA && ReferenceEquals(e.OriginalSource, this))
		{
			if (!e.KeyStatus.WasKeyDown)
			{
				Open();
			}

			e.Handled = true;
		}
	}

	private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
	{
		if (Item?.Dto is not { } dto || new JellyfinFlyoutConverter { IncludePlay = true }.Convert(dto, typeof(object), null!, "") is not FlyoutBase flyout)
		{
			return;
		}

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

	private void OnGotFocus(object sender, RoutedEventArgs e)
	{
		_keyboardFocus = ReferenceEquals(e.OriginalSource, this) && FocusState is FocusState.Keyboard or FocusState.Programmatic;
		UpdateRing();
	}

	private void UpdateRing()
	{
		var ring = _keyboardFocus || IsOpen;
		FocusRing.Visibility = FocusGlow.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
		Notch.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
		Lift();
		if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
				IsOpen ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded, IsOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
		}
	}

	/// <summary>Hover and keyboard focus lift the artwork a little (1.04 on a spring, in Full mode only) and deepen its shadow.</summary>
	private void Lift()
	{
		var lifted = _pointerOver || _keyboardFocus;
		var visual = ElementCompositionPreview.GetElementVisual(Art);
		visual.CenterPoint = new Vector3((float)(PosterWidth / 2), (float)(ArtHeight / 2), 0);
		var scale = lifted && SafeMotion.Allowed ? 1.04f : 1f;
		if (SafeMotion.Allowed)
		{
			var spring = visual.Compositor.CreateSpringVector3Animation();
			spring.DampingRatio = 0.7f;
			spring.Period = TimeSpan.FromMilliseconds(50);
			spring.FinalValue = new Vector3(scale, scale, 1);
			visual.StartAnimation("Scale", spring);
			ElementCompositionPreview.GetElementVisual(FocusRing).Scale = Vector3.One;
		}
		else
		{
			visual.StopAnimation("Scale");
			visual.Scale = Vector3.One;
		}

		ArtShadow.BlurRadius = lifted ? 24 : 16;
		ArtShadow.Opacity = lifted ? 0.55 : 0.4;
		ArtShadow.Offset = lifted ? "0,10" : "0,6";
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new EditorialPosterAutomationPeer(this);
}

/// <summary>
/// Narrator reads an Editorial poster as the shared card is read (ItemLabels.CardName: "Glass Canyon, 2022, favorite"), an item
/// of its row's list ("2 of 12"); Invoke opens it (its details in place), and it says whether those are open.
/// </summary>
internal sealed partial class EditorialPosterAutomationPeer(EditorialPoster owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider, IExpandCollapseProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

	protected override string GetClassNameCore() => nameof(EditorialPoster);

	protected override string GetNameCore()
	{
		var name = AutomationProperties.GetName(owner);
		return string.IsNullOrEmpty(name) ? ItemLabels.CardName(owner.Item) : name;
	}

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override IList<AutomationPeer> GetChildrenCore() => [];

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface is PatternInterface.Invoke or PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);

	public void Invoke() => owner.Open();

	public ExpandCollapseState ExpandCollapseState => owner.IsOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

	public void Expand()
	{
		if (!owner.IsOpen)
		{
			owner.Open();
		}
	}

	public void Collapse()
	{
		if (owner.IsOpen)
		{
			owner.Open();
		}
	}
}
