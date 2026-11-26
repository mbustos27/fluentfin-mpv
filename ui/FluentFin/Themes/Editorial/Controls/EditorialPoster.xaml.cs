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
public sealed partial class EditorialPoster : UserControl, FluentFin.Controls.ICaptionPrefixed
{
	/// <summary>Nixie 1.4.0 (item 15, LibraryProbe): the 2:3 artwork, measured against the loading placeholders.</summary>
	internal FrameworkElement ArtElement => Art;

	public static readonly DependencyProperty ItemProperty =
		DependencyProperty.Register(nameof(Item), typeof(BaseItemViewModel), typeof(EditorialPoster), new PropertyMetadata(null, (d, _) => ((EditorialPoster)d).Apply()));

	public static readonly DependencyProperty PosterWidthProperty =
		DependencyProperty.Register(nameof(PosterWidth), typeof(double), typeof(EditorialPoster), new PropertyMetadata(144.0, (d, _) => ((EditorialPoster)d).Resize()));

	public static readonly DependencyProperty IsOpenProperty =
		DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(EditorialPoster), new PropertyMetadata(false, (d, e) => ((EditorialPoster)d).OnIsOpenChanged((bool)e.OldValue, (bool)e.NewValue)));

	/// <summary>
	/// 1.6 colpage (LB1): the poster's place in a set whose order means something ("1"), set in its caption in Editorial's
	/// numbering ("01 · Wild Canyons", as Home's "01 … 05") and said first by Narrator ("1, Wild Canyons, 1979"); empty: none.
	/// </summary>
	public static readonly DependencyProperty CaptionPrefixProperty =
		DependencyProperty.Register(nameof(CaptionPrefix), typeof(string), typeof(EditorialPoster), new PropertyMetadata("", (d, _) => ((EditorialPoster)d).ApplyTitle()));

	public string CaptionPrefix
	{
		get => (string)GetValue(CaptionPrefixProperty);
		set => SetValue(CaptionPrefixProperty, value);
	}

	/// <summary>
	/// 1.6 colpage (theme polish): the list view's line: a small poster (64 wide), then the title, "1979 · 1h 52m · PG", a short
	/// line from the overview, and the state at the end ("Watched", "45% watched", "5 left"); the same stop, ring, details in
	/// place and Narrator name as the grid's poster.
	/// </summary>
	public static readonly DependencyProperty ListFormProperty =
		DependencyProperty.Register(nameof(ListForm), typeof(bool), typeof(EditorialPoster), new PropertyMetadata(false, (d, _) => ((EditorialPoster)d).ApplyForm()));

	public bool ListForm
	{
		get => (bool)GetValue(ListFormProperty);
		set => SetValue(ListFormProperty, value);
	}

	private void ApplyForm()
	{
		var list = ListForm;
		// the columns only in the list form: an Auto column in the poster took a long title's width and shifted the art
		RootGrid.ColumnDefinitions.Clear();
		if (list)
		{
			RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		}

		Grid.SetRow(TitleArea, list ? 0 : 1);
		Grid.SetColumn(TitleArea, list ? 1 : 0);
		TitleArea.Padding = list ? new Thickness(20, 0, 0, 0) : new Thickness(1, 8, 1, 0);
		TitleArea.VerticalAlignment = list ? VerticalAlignment.Center : VerticalAlignment.Stretch;
		TitleText.FontSize = list ? 18 : 15;
		ShortText.Visibility = list && ShortText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		StateText.Visibility = list && StateText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		ListBack.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
		ApplyUserData(); // the marks on the art, or the words at the line's end
		Grid.SetColumnSpan(FocusRing, list ? 3 : 1);
		Grid.SetColumnSpan(OpenRim, list ? 3 : 1);
		HorizontalAlignment = list ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
		Resize();
	}

	/// <summary>Editorial's numbering: "01" for "1", "12" for "12" (a number), the prefix as it is otherwise.</summary>
	public static string Numbered(string? prefix) =>
		int.TryParse(prefix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n.ToString("00", System.Globalization.CultureInfo.InvariantCulture) : prefix ?? "";

	private void ApplyTitle()
	{
		var title = EditorialCards.Title(Item?.Dto);
		TitleText.Inlines.Clear();
		if (!string.IsNullOrEmpty(CaptionPrefix))
		{
			TitleText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{Numbered(CaptionPrefix)} · ", Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GlassTextSecondaryBrush"] });
		}

		TitleText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = title });
	}

	private bool _pointerOver;
	private bool _keyboardFocus;
	private bool _keyboardElsewhere;
	private int _decodedHeight;

	public EditorialPoster()
	{
		InitializeComponent();
		Loaded += (_, _) =>
		{
			FluentFin.Controls.CardCaption.Pull(this); // 1.6 colpage (LB1): a place its entry gives
			ItemUserData.Changed += OnUserDataChanged;
			CardFocus.Changed += OnCardFocusChanged;
			_keyboardElsewhere = CardFocus.IsElsewhere(this);
			IsOpen = Item is { } item && FindHost() is { } host && host.IsOpen(item);
		};
		Unloaded += (_, _) =>
		{
			ItemUserData.Changed -= OnUserDataChanged;
			CardFocus.Changed -= OnCardFocusChanged;
			CardFocus.Release(this);
		};
		AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), false);
		GotFocus += OnGotFocus;
		LostFocus += (_, _) =>
		{
			if (FocusState != FocusState.Unfocused)
			{
				return;
			}

			_keyboardFocus = false;
			CardFocus.Release(this);
			UpdateRing();
		};
		RootGrid.Tapped += OnTapped;
		RootGrid.PointerEntered += (_, _) => { _pointerOver = true; UpdateRing(); };
		RootGrid.PointerExited += (_, _) => { _pointerOver = false; UpdateRing(); };
		RootGrid.PointerCanceled += (_, _) => { _pointerOver = false; UpdateRing(); };
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

	private double ArtWidthNow => ListForm ? FluentFin.Core.ViewModels.EditorialListForm.ArtWidth : PosterWidth;

	private double ArtHeight => Math.Round(ArtWidthNow * 1.5);

	private void Resize()
	{
		// 1.6 colpage: the list form is as wide as its line, its art the list's small poster
		Width = ListForm ? double.NaN : PosterWidth;
		Art.Width = ListForm ? FluentFin.Core.ViewModels.EditorialListForm.ArtWidth : double.NaN;
		Art.Height = ArtHeight;
		// Nixie 1.4.0 (the release's layout cycle): the picture has its size before its image arrives (it grew from nothing as
		// the image opened, which laid the Library's grid out again: after a far scroll, every image re-made its rows for 30 s)
		Picture.Width = ArtWidthNow;
		Picture.Height = ArtHeight;
		// the list form's ring and rim go round the whole line (the art and its words)
		FocusRing.Height = ListForm ? double.NaN : ArtHeight + 8;
		FocusRing.Margin = ListForm ? new Thickness(-12, -8, -12, -8) : new Thickness(-4);
		FocusRing.VerticalAlignment = ListForm ? VerticalAlignment.Stretch : VerticalAlignment.Top;
		OpenRim.Height = ListForm ? double.NaN : ArtHeight + 6;
		OpenRim.Margin = ListForm ? new Thickness(-11, -7, -11, -7) : new Thickness(-3);
		OpenRim.VerticalAlignment = ListForm ? VerticalAlignment.Stretch : VerticalAlignment.Top;
		ApplyImage();
	}

	private void Apply()
	{
		var dto = Item?.Dto;
		ApplyTitle();
		MetaText.Text = ListForm ? FluentFin.Core.ViewModels.EditorialListForm.Facts(dto) : EditorialCards.PosterMeta(dto);
		MetaText.Visibility = MetaText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		// 1.6 colpage: the list form's short line and state
		ShortText.Text = FluentFin.Core.ViewModels.EditorialListForm.ShortLine(dto);
		StateText.Text = FluentFin.Core.ViewModels.EditorialListForm.State(dto);
		ShortText.Visibility = ListForm && ShortText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		StateText.Visibility = ListForm && StateText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
		// 1.6 colpage: the list form says it in words at the line's end ("Watched", "5 left"): no second mark on its small art
		WatchedMark.Visibility = ItemBadges.Watched(dto) && !ListForm ? Visibility.Visible : Visibility.Collapsed;
		// 1.4.0 (X10, E-m4): the count Narrator hears ("5 unwatched items") is on screen too
		CountText.Text = ItemBadges.ToWatchText(dto);
		CountChip.Visibility = CountText.Text.Length > 0 && !ListForm ? Visibility.Visible : Visibility.Collapsed;
		var progress = EditorialCards.Progress(dto);
		ProgressTrack.Visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
		ProgressFill.Width = Math.Max(4, (ArtWidthNow - 20) * progress / 100);
		StateText.Text = FluentFin.Core.ViewModels.EditorialListForm.State(dto);
		StateText.Visibility = ListForm && StateText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, "", FluentFin.Controls.BaseItemDtoCard.NameWithPrefix(CaptionPrefix, ItemLabels.CardName(Item)));
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

		// the item's page, first in its menu (a click opens the details in place; this is the way past them)
		EditorialMenus.WithPageEntry(flyout, "Full details", dto);

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
		if (_keyboardFocus)
		{
			CardFocus.Take(this);
		}
		else
		{
			CardFocus.Release(this);
		}

		UpdateRing();
	}

	private void OnCardFocusChanged(object? sender, EventArgs e)
	{
		var elsewhere = CardFocus.IsElsewhere(this);
		if (elsewhere != _keyboardElsewhere)
		{
			_keyboardElsewhere = elsewhere;
			UpdateRing();
		}
	}

	/// <summary>
	/// 1.4.0 (the library UX review, E-m4): Narrator hears "expanded" or "collapsed" when the details open or close, never on a
	/// focus change (the poster raised the event on every arrow key, whether or not anything opened).
	/// </summary>
	private void OnIsOpenChanged(bool was, bool now)
	{
		UpdateRing();
		if (was != now && FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
				was ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed, now ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
		}
	}

	/// <summary>What the poster draws now (<see cref="Look"/>).</summary>
	private void UpdateRing()
	{
		var look = Look(_pointerOver, _keyboardFocus, _keyboardElsewhere, IsOpen);
		FocusRing.Visibility = look.Ring ? Visibility.Visible : Visibility.Collapsed;
		OpenRim.Visibility = look.Open ? Visibility.Visible : Visibility.Collapsed;
		Notch.Visibility = look.Open && !ListForm ? Visibility.Visible : Visibility.Collapsed; // the list form's rim says it alone
		HoverBrighten.Visibility = look.Brighten ? Visibility.Visible : Visibility.Collapsed;
		ListBack.Background = ListForm && (_pointerOver || look.Ring) ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EditorialRowHoverBrush"] : null;
		Lift(look.Lift);
	}

	/// <summary>
	/// Nixie 1.4.0 (the library UX review, E-M8, X9): one mark per meaning. The keyboard's poster: the two-tone ring and the lift.
	/// An open poster: the accent rim and the notch, no ring, no lift (two posters never look focused at once). A free pointer:
	/// the lift; a pointer while the keyboard is on another card: the brighten alone.
	/// </summary>
	internal static PosterLook Look(bool pointerOver, bool keyboardFocus, bool keyboardElsewhere, bool open)
	{
		var freePointer = pointerOver && !keyboardElsewhere;
		var lift = keyboardFocus || (freePointer && !open);
		return new PosterLook(Ring: keyboardFocus, Open: open, Lift: lift, Brighten: pointerOver && !lift && !keyboardFocus);
	}

	/// <summary>The keyboard's focus and a free pointer lift the artwork a little (1.04 on a spring, in Full mode only) and deepen its shadow.</summary>
	private void Lift(bool lifted)
	{
		var visual = ElementCompositionPreview.GetElementVisual(Art);
		visual.CenterPoint = new Vector3((float)(ArtWidthNow / 2), (float)(ArtHeight / 2), 0);
		var scale = lifted && SafeMotion.Allowed && !ListForm ? 1.04f : 1f; // a list line does not lift (its ring and hover say it)
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

/// <summary>Nixie 1.4.0: what an Editorial poster draws for the focus, the pointer and its open details.</summary>
internal readonly record struct PosterLook(bool Ring, bool Open, bool Lift, bool Brighten);

/// <summary>
/// Narrator reads an Editorial poster as the shared card is read (ItemLabels.CardName: "Glass Canyon, 2022, favorite"), an item
/// of its row's list ("2 of 12"); Invoke opens it (its details in place), and it says whether those are open.
/// </summary>
internal sealed partial class EditorialPosterAutomationPeer(EditorialPoster element) : FrameworkElementAutomationPeer(element), IInvokeProvider, IExpandCollapseProvider
{
	// Nixie 1.8.1 (BH-175-M2): its element through Owner, never a field of the peer: UI Automation keeps a peer after its
	// element has left (Narrator, any assistive tool), and a peer holding the element held it and its whole tree
	private EditorialPoster Element => Owner as EditorialPoster ?? throw new Microsoft.UI.Xaml.Automation.ElementNotAvailableException();

	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

	protected override string GetClassNameCore() => nameof(EditorialPoster);

	protected override string GetNameCore()
	{
		var name = AutomationProperties.GetName(Element);
		return string.IsNullOrEmpty(name) ? FluentFin.Controls.BaseItemDtoCard.NameWithPrefix(Element.CaptionPrefix, ItemLabels.CardName(Element.Item)) : name;
	}

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	// Nixie 1.8.1 (the mem review): "3 of 24": the poster's own place, else the one its entry holds (the Collection page's CardPlace)
	protected override int GetPositionInSetCore() => FluentFin.Controls.CardPlace.Position(base.GetPositionInSetCore(), Owner);

	protected override int GetSizeOfSetCore() => FluentFin.Controls.CardPlace.Size(base.GetSizeOfSetCore(), Owner);

	protected override IList<AutomationPeer> GetChildrenCore() => [];

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface is PatternInterface.Invoke or PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);

	public void Invoke() => Element.Open();

	public ExpandCollapseState ExpandCollapseState => Element.IsOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

	public void Expand()
	{
		if (!Element.IsOpen)
		{
			Element.Open();
		}
	}

	public void Collapse()
	{
		if (Element.IsOpen)
		{
			Element.Open();
		}
	}
}

/// <summary>Editorial: the shared item menu, with the item's own page as its first entry.</summary>
internal static class EditorialMenus
{
	/// <summary>
	/// Puts "<paramref name="text"/>" (the item's page) first in <paramref name="flyout"/>, each time it opens: the shared
	/// menu makes its entries again as it opens (from the item as it is then), after which this one goes in.
	/// </summary>
	public static void WithPageEntry(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout, string text, Jellyfin.Sdk.Generated.Models.BaseItemDto dto)
	{
		if (flyout is not MenuFlyout menu)
		{
			return;
		}

		void Add()
		{
			if (menu.Items.FirstOrDefault() is MenuFlyoutItem { Tag: "EditorialPage" })
			{
				return;
			}

			var entry = new MenuFlyoutItem { Text = text, Tag = "EditorialPage", Icon = new FontIcon { Glyph = "" } };
			entry.Click += (_, _) => App.Commands.DisplayDto(dto);
			menu.Items.Insert(0, new MenuFlyoutSeparator());
			menu.Items.Insert(0, entry);
		}

		Add();
		menu.Opening += (_, _) => Add();
	}
}
